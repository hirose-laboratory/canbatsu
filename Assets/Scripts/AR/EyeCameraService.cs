using System;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using Unity.XR.XREAL;
using UnityEngine.Android;
#endif

/// <summary>
/// XREAL Eye (グラスの目線カメラ) の映像取得サービス。
/// 作業開始で起動し、AI推論への入力とプレビュー表示を供給する (AR企画書フェーズ2の映像パイプライン)。
///
/// カメラ共有ルール (motion_stereoのAPP_INTEGRATION_NOTES.md準拠):
///  - アプリ全体でこのサービスが唯一のカメラ所有者 (StartCaptureを呼ぶのはここだけ)
///  - 画像が欲しい機能 (AI検出・距離計測など) は XREALRGBCameraTexture.Singleton の
///    OnRGBCameraUpdate を購読するか、このサービスの FrameUpdated / GetYuvPlanes() を使う
///
/// 挙動:
///  - 権限が未許可なら要求し、許可されたら自動でリトライする (作業開始し直し不要)
///  - XR (XrSession) の起動後にカメラを開く
///  - バックグラウンドに回ったらカメラを解放し、復帰時に自動再開 (再起動時の暗転ハング対策)
///  - プレビューはYUV→RGB変換シェーダーでカラー化 (エディタはPCのWebカメラで代用)
/// </summary>
public class EyeCameraService : MonoBehaviour
{
    public bool IsCapturing { get; private set; }

    /// <summary>状態の説明 (作業中ページに表示する)</summary>
    public string Status { get; private set; } = "カメラ停止中";

    /// <summary>プレビュー用テクスチャ (カラー)。フレーム未取得の間はnull</summary>
    public Texture PreviewTexture { get; private set; }

    public Vector2Int Resolution { get; private set; }

    /// <summary>新しいフレームが来るたびに通知 (AI推論のトリガーに使う予定)</summary>
    public event Action FrameUpdated;

    /// <summary>StartCaptureが呼ばれてStopCaptureがまだか (自動リトライ・復帰再開の判定)</summary>
    bool _wantCapture;
    float _nextRetryAt;

    Material _yuvMaterial;
    RenderTexture _previewRt;

    public void StartCapture()
    {
        _wantCapture = true;
        _nextRetryAt = 0f;
        TryStart();
    }

    public void StopCapture()
    {
        _wantCapture = false;
        ReleaseCamera();
        Status = "カメラ停止中";
    }

    void Update()
    {
        // 権限待ち・XR起動待ち・一時停止からの復帰を自動でリトライする
        if (_wantCapture && !IsCapturing && Time.unscaledTime >= _nextRetryAt)
        {
            _nextRetryAt = Time.unscaledTime + 1.5f;
            TryStart();
        }
        UpdatePlatform();
    }

    void OnApplicationPause(bool paused)
    {
        // バックグラウンドではカメラを必ず解放する (握ったままだと次回起動が暗転ハングする)
        if (paused && IsCapturing)
        {
            ReleaseCamera();
            Status = "一時停止中";
        }
        // 復帰時はUpdateの自動リトライが再開してくれる (_wantCaptureが立ったまま)
    }

    void OnDestroy()
    {
        _wantCapture = false;
        ReleaseCamera();
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    // ---- 実機: XREAL Eyeカメラ (YUV420) ----

    XREALRGBCameraTexture _camera;

    void TryStart()
    {
        if (IsCapturing) return;

        // ビルドにCAMERA権限を入れさせるためのダミー参照 (XREALSettings側でも宣言済み)
        var _ = WebCamTexture.devices;

        if (!Permission.HasUserAuthorizedPermission(Permission.Camera))
        {
            Permission.RequestUserPermission(Permission.Camera);
            Status = "カメラの許可待ち...";
            return; // 許可されたらUpdateの自動リトライで再開する
        }

        if (!XrSession.IsRunning)
        {
            Status = "AR起動待ち...";
            return;
        }

        try
        {
            _camera = XREALRGBCameraTexture.CreateSingleton();
            _camera.OnRGBCameraUpdate -= OnFrame; // 二重購読防止
            _camera.OnRGBCameraUpdate += OnFrame;
            if (_camera.StartCapture())
            {
                IsCapturing = true;
                Status = "XREAL Eyeから映像取得中";
            }
            else
            {
                Status = "カメラを開始できません (グラスとEyeの接続を確認)";
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Eyeカメラの初期化に失敗: {e.Message}");
            Status = "カメラ初期化に失敗 (自動で再試行します)";
        }
    }

    void OnFrame()
    {
        Resolution = _camera.GetResolution();
        UpdatePreview(_camera.GetYUVFormatTextures());
        FrameUpdated?.Invoke();
    }

    void ReleaseCamera()
    {
        IsCapturing = false;
        if (_camera != null)
        {
            _camera.OnRGBCameraUpdate -= OnFrame;
            try { _camera.StopCapture(); } catch (Exception) { }
        }
    }

    void UpdatePlatform() { }

    /// <summary>AI推論用: YUVの3プレーン (0=Y輝度フル解像度, 1=U, 2=V 半解像度)。未取得ならnull</summary>
    public Texture2D[] GetYuvPlanes() => _camera != null ? _camera.GetYUVFormatTextures() : null;

#else
    // ---- エディタ: PCのWebカメラで代用 ----

    WebCamTexture _webcam;

    void TryStart()
    {
        if (IsCapturing) return;
        try
        {
            _webcam = new WebCamTexture(1280, 720, 30);
            _webcam.Play();
            PreviewTexture = _webcam;
            IsCapturing = true;
            Status = "PCカメラで代用中 (実機ではXREAL Eyeになる)";
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Webカメラを開始できません: {e.Message}");
            Status = "PCにカメラがないため映像なし (実機では動きます)";
            _wantCapture = false; // エディタでは繰り返さない
        }
    }

    void UpdatePlatform()
    {
        if (!IsCapturing || _webcam == null || !_webcam.didUpdateThisFrame) return;
        Resolution = new Vector2Int(_webcam.width, _webcam.height);
        FrameUpdated?.Invoke();
    }

    void ReleaseCamera()
    {
        IsCapturing = false;
        if (_webcam != null)
        {
            _webcam.Stop();
            _webcam = null;
            PreviewTexture = null;
        }
    }

    public Texture2D[] GetYuvPlanes() => null;
#endif

    /// <summary>YUVプレーンをシェーダーでRGBに変換してプレビュー用RenderTextureへ焼く</summary>
    void UpdatePreview(Texture2D[] planes)
    {
        if (planes == null || planes[0] == null) return;

        if (_yuvMaterial == null)
        {
            // 実機ビルドに確実に含めるためResourcesから読む (Shader.Findは参照の無いシェーダーを見つけられない)
            var shader = Resources.Load<Shader>("AR/YuvToRgb") ?? Shader.Find("Canbatsu/YuvToRgb");
            if (shader == null)
            {
                // シェーダーが見つからない場合は輝度プレーンをそのまま出す (白黒だが映りはする)
                PreviewTexture = planes[0];
                return;
            }
            _yuvMaterial = new Material(shader);
        }

        if (_previewRt == null || _previewRt.width != planes[0].width)
        {
            if (_previewRt != null) _previewRt.Release();
            _previewRt = new RenderTexture(planes[0].width, planes[0].height, 0);
        }

        // SDKの配列順は {Y, U, V} だが、実機 (2026-08-22) で赤と青が入れ替わって映ったため
        // 色差プレーンを逆に割り当てる (SDK内部のプレーン番号付けと表記がずれている模様)
        _yuvMaterial.SetTexture("_YTex", planes[0]);
        _yuvMaterial.SetTexture("_UTex", planes[2]);
        _yuvMaterial.SetTexture("_VTex", planes[1]);
        Graphics.Blit(null, _previewRt, _yuvMaterial);
        PreviewTexture = _previewRt;
    }
}
