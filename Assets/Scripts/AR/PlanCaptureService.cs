using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using Unity.XR.XREAL;

/// <summary>
/// 作業中の視界画像を頭の姿勢付きで定期保存するサービス (実証実験のデータ収集用)。
/// 作業ページが Begin で開始し、作業終了時に End で止める。
///
/// 保存先: persistentDataPath/captures/{planId}/session_yyyyMMdd_HHmmss/
///  - cap_NNN.jpg    : プレビュー映像のJPG (品質70)
///  - captures.jsonl : 1行=1枚のメタ情報。フィールド名は ms_capture
///                     (MotionStereoController.SaveKeyframe) と同じにしてPC側の解析ツールを流用できるようにする
///
/// カメラは所有しない (所有者は EyeCameraService。ここは PreviewTexture を読むだけで
/// StartCapture/StopCapture は呼ばない)。アップロードは CaptureUploader が別途行う。
/// </summary>
public class PlanCaptureService : MonoBehaviour
{
    /// <summary>撮影間隔 [秒] (0.33Hzならメインスレッドでのエンコードでも負荷は許容範囲)</summary>
    const float IntervalSec = 3f;

    /// <summary>動作中のインスタンス (動いていなければnull)</summary>
    public static PlanCaptureService Active { get; private set; }

    /// <summary>撮影対象の計画ID</summary>
    public string PlanId { get; private set; }

    /// <summary>このセッションで保存した枚数</summary>
    public int SavedCount { get; private set; }

    /// <summary>このセッションで保存した容量 [byte] (UIの目安表示用)</summary>
    public long SavedBytes { get; private set; }

    /// <summary>このセッションの保存先ディレクトリ (CaptureUploaderが書き込み中セッションを避けるのに使う)</summary>
    public string SessionDir { get; private set; }

    StreamWriter _metaWriter;
    float _nextCaptureAt;
    EyeCameraService _eyeCamera;
    Texture2D _readTex; // ReadPixels用バッファ (毎回作るとGCとVRAM確保が無駄なので使い回す)

    /// <summary>撮影を開始する。二重Beginは前のセッションを終了してから始める</summary>
    public static PlanCaptureService Begin(GameObject host, string planId)
    {
        if (Active != null) Active.End();

        var service = host.AddComponent<PlanCaptureService>();
        service.PlanId = planId;
        service.SessionDir = Path.Combine(
            Application.persistentDataPath, "captures", planId,
            "session_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(service.SessionDir);
            service._metaWriter = new StreamWriter(
                Path.Combine(service.SessionDir, "captures.jsonl"),
                false, Encoding.UTF8) { AutoFlush = true };
        }
        catch (Exception e)
        {
            // 保存先を作れない場合は撮影せず空回りさせる (作業自体は止めない)
            Debug.LogWarning($"画像の保存先を作成できません: {e.Message}");
        }
        Active = service;
        return service;
    }

    /// <summary>撮影を終了する (メタファイルを閉じてコンポーネントを外す)</summary>
    public void End()
    {
        Destroy(this);
    }

    /// <summary>
    /// このセッションの基準点 (位置+yaw) を anchor.json に残す。
    /// サーバー側の3Dマップ化が「セッション座標系→計画のマップ座標系」の変換に使う。
    /// 基準点をセットし直したら上書きされる (最後の基準点が有効)。
    /// </summary>
    public void WriteAnchor(Vector3 anchorPos, float anchorYawRad)
    {
        try
        {
            // 数値でない値を書くとサーバー側で読めないファイルになるので書かない
            if (float.IsNaN(anchorPos.x) || float.IsNaN(anchorPos.y) || float.IsNaN(anchorPos.z)
                || float.IsNaN(anchorYawRad) || float.IsInfinity(anchorYawRad))
            {
                Debug.LogWarning("基準点の値が不正なため anchor.json を書きませんでした");
                return;
            }
            string json = FormattableString.Invariant(
                $"{{\"pos\":[{anchorPos.x:F5},{anchorPos.y:F5},{anchorPos.z:F5}],\"yawRad\":{anchorYawRad:F6}}}");
            File.WriteAllText(Path.Combine(SessionDir, "anchor.json"), json);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"基準点の保存に失敗しました: {e.Message}");
        }
    }

    void Update()
    {
        if (Time.unscaledTime < _nextCaptureAt) return;
        _nextCaptureAt = Time.unscaledTime + IntervalSec;
        TryCapture();
    }

    void TryCapture()
    {
        try
        {
            if (_metaWriter == null) return;

            // カメラ映像と頭の姿勢が両方そろっているときだけ撮る (山でのAR未起動・カメラ許可待ちを考慮)
            if (_eyeCamera == null) _eyeCamera = FindFirstObjectByType<EyeCameraService>();
            Texture source = _eyeCamera != null ? _eyeCamera.PreviewTexture : null;
            Camera head = ArDemoController.HeadCamera;
            if (source == null || head == null) return;

            int w = source.width, h = source.height;
            if (w <= 16 || h <= 16) return; // WebCamTexture初期化直後のダミーサイズは捨てる

            byte[] jpg = EncodeToJpg(source, w, h);
            string fileName = string.Format(CultureInfo.InvariantCulture, "cap_{0:D3}.jpg", SavedCount);
            File.WriteAllBytes(Path.Combine(SessionDir, fileName), jpg);
            WriteMeta(fileName, w, h, head.transform);

            SavedCount++;
            SavedBytes += jpg.LongLength;
        }
        catch (Exception e)
        {
            // 1枚の失敗でサービスは止めない (次の周期で自動的に再試行される)
            Debug.LogWarning($"作業画像の保存に失敗しました: {e.Message}");
        }
    }

    /// <summary>プレビュー用Texture (RenderTexture等でCPUから読めない) を読み出してJPGにする</summary>
    byte[] EncodeToJpg(Texture source, int w, int h)
    {
        var rt = RenderTexture.GetTemporary(w, h, 0);
        var prevActive = RenderTexture.active;
        try
        {
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;
            if (_readTex == null || _readTex.width != w || _readTex.height != h)
            {
                if (_readTex != null) Destroy(_readTex);
                _readTex = new Texture2D(w, h, TextureFormat.RGB24, false);
            }
            _readTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            _readTex.Apply();
        }
        finally
        {
            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(rt);
        }
        return _readTex.EncodeToJPG(70);
    }

    /// <summary>ms_capture (keyframes.jsonl) と同じフィールド名でメタ情報を1行追記する</summary>
    void WriteMeta(string fileName, int w, int h, Transform head)
    {
        // 頭→RGBカメラのオフセットと内部パラメータ (エディタや取得失敗時は0値のまま書く)
        Pose camOffset = Pose.identity;
        Vector2 focal = Vector2.zero, principal = Vector2.zero;
        try
        {
            XREALPlugin.GetDevicePoseFromHead(
                XREALComponent.XREAL_COMPONENT_RGB_CAMERA, ref camOffset);
            XREALPlugin.GetCameraIntrinsic(
                XREALComponent.XREAL_COMPONENT_RGB_CAMERA, ref focal, ref principal);
        }
        catch (Exception)
        {
            camOffset = Pose.identity;
            focal = Vector2.zero;
            principal = Vector2.zero;
        }

        // 数値はカルチャ非依存で整形する (小数点がカンマになる言語設定でもJSONが壊れないように)
        Vector3 hp = head.position;
        Quaternion hq = head.rotation;
        string line =
            FormattableString.Invariant($"{{\"idx\":{SavedCount},\"file\":\"{fileName}\",\"w\":{w},\"h\":{h},") +
            FormattableString.Invariant($"\"t_unity\":{Time.unscaledTime:F3},\"t_frame\":0,") +
            FormattableString.Invariant($"\"head_pos\":[{hp.x:F5},{hp.y:F5},{hp.z:F5}],") +
            FormattableString.Invariant($"\"head_rot\":[{hq.x:F6},{hq.y:F6},{hq.z:F6},{hq.w:F6}],") +
            FormattableString.Invariant($"\"cam_off_pos\":[{camOffset.position.x:F6},{camOffset.position.y:F6},{camOffset.position.z:F6}],") +
            FormattableString.Invariant($"\"cam_off_rot\":[{camOffset.rotation.x:F6},{camOffset.rotation.y:F6},{camOffset.rotation.z:F6},{camOffset.rotation.w:F6}],") +
            FormattableString.Invariant($"\"fx\":{focal.x:F3},\"fy\":{focal.y:F3},\"cx\":{principal.x:F3},\"cy\":{principal.y:F3}}}");
        _metaWriter.WriteLine(line);
    }

    void OnDestroy()
    {
        if (Active == this) Active = null;
        if (_metaWriter != null)
        {
            try { _metaWriter.Close(); } catch (Exception) { }
            _metaWriter = null;
        }
        if (_readTex != null)
        {
            Destroy(_readTex);
            _readTex = null;
        }
    }
}
