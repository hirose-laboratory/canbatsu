using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Unity.XR.XREAL;

namespace CanbatsuMS
{
    /// <summary>
    /// モーションステレオ実機コントローラ(キャプチャ+推定の統合)
    ///
    /// 動作: 静止するとキーフレームを取得し、2枚たまるたびに
    /// ワーカースレッドで MotionStereoEstimator を実行して距離を出す。
    ///
    /// 運用プロトコル(README参照。守らないと精度が出ない):
    ///   1. 起動後まず5〜6m歩き回る(6DoFスケール収束)
    ///   2. 対象を画面中央に見て静止 → 横に30〜50cmステップ(向きは変えない)→ 静止
    ///   3. 50cm印を使う場合は trueBaselineMeters=0.5 に設定(誤差2%未満)
    ///
    /// 本番アプリ (canbatsu) 向けの変更 (APP_INTEGRATION_NOTES.md §3 を適用。詳細は README_統合メモ.md):
    ///   - カメラは起動しない。アプリの所有者 (EyeCameraService) が起動した Singleton のフレームを購読するだけ
    ///   - trackedCamera を明示設定できる (未設定時のみ Camera.main)
    ///   - デバッグHUDは showDebugHud=true のときだけ、キーフレーム保存は既定でオフ
    /// 結果は OnResult イベントで受け取る。
    /// </summary>
    public class MotionStereoController : MonoBehaviour
    {
        [Tooltip("頭の姿勢として使うカメラ。未設定なら Camera.main (本番ではARカメラを設定すること)")]
        public Camera trackedCamera;

        [Tooltip("キーフレーム間の最小移動距離 [m]")]
        public float minBaseline = 0.25f;

        [Tooltip("静止とみなす移動量 [m](窓時間内)")]
        public float stillnessThreshold = 0.02f;

        [Tooltip("静止判定の窓時間 [秒]")]
        public float stillnessWindow = 0.5f;

        [Tooltip("実測ステップ距離 [m]。0以下なら6DoFの基線長をそのまま使う")]
        public float trueBaselineMeters = -1f;

        [Tooltip("キーフレーム間の回転がこの角度[度]を超えたら警告(LKは大回転に弱い)")]
        public float maxRotationDeg = 15f;

        [Tooltip("検証用にキーフレームを保存する(PC解析と突き合わせ可能)。本番ではオフ")]
        public bool saveKeyframes = false;

        [Tooltip("画面左上にデバッグHUDを描く (本番UIと重なるので既定オフ)")]
        public bool showDebugHud = false;

        /// <summary>推定完了ごとに発火(ワーカースレッドではなくメインスレッドで呼ばれる)</summary>
        public event Action<MsResult> OnResult;

        /// <summary>直近の推定に使ったキーフレームペア(OnResult発火時点で有効。Aが距離の基準)</summary>
        public MsKeyframe LastKeyframeA { get; private set; }
        public MsKeyframe LastKeyframeB { get; private set; }

        /// <summary>状態の説明 (アプリのUI表示用)</summary>
        public string Hud => _hud;

        /// <summary>直近の結果の説明 (アプリのUI表示用)</summary>
        public string ResultText => _resultText;

        private readonly MotionStereoEstimator _estimator = new MotionStereoEstimator();
        private MsKeyframe _prevKf;
        private Quaternion _prevKfRot;
        private Vector3 _lastKfPos = new Vector3(9999, 9999, 9999);
        private float _lastKfTime = -999;
        private bool _cameraReady, _captureRequested, _estimating;
        private string _hud = "初期化中...";
        private string _resultText = "";
        private int _kfCount;
        private string _sessionDir;
        private StreamWriter _metaWriter;
        private MsResult _pendingResult;
        private readonly List<(float t, Vector3 p)> _posHistory = new List<(float, Vector3)>();
        private XREALRGBCameraTexture _subscribedCamera;

        private Transform Head
        {
            get
            {
                if (trackedCamera != null) return trackedCamera.transform;
                return Camera.main != null ? Camera.main.transform : null;
            }
        }

        void Start()
        {
            if (saveKeyframes)
            {
                _sessionDir = Path.Combine(Application.persistentDataPath, "ms_capture",
                    "session_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                Directory.CreateDirectory(_sessionDir);
                _metaWriter = new StreamWriter(Path.Combine(_sessionDir, "keyframes.jsonl"),
                    false, Encoding.UTF8) { AutoFlush = true };
            }
            StartCoroutine(WaitForCamera());
        }

        /// <summary>
        /// カメラはアプリの所有者 (EyeCameraService) が起動する。ここでは起動済みになるのを待って購読するだけ。
        /// (複数箇所がStartCaptureするとストリームが衝突するため)
        /// </summary>
        private IEnumerator WaitForCamera()
        {
            _hud = "カメラ待ち...";
            while (XREALRGBCameraTexture.Singleton == null || !XREALRGBCameraTexture.Singleton.IsCapturing)
            {
                yield return new WaitForSeconds(0.5f);
            }
            _subscribedCamera = XREALRGBCameraTexture.Singleton;
            _subscribedCamera.OnRGBCameraUpdate -= OnCameraFrame;
            _subscribedCamera.OnRGBCameraUpdate += OnCameraFrame;
            _cameraReady = true;
            _hud = "まず5〜6m歩いてから、対象を見て静止してください";
        }

        void Update()
        {
            var head = Head;
            if (head == null) return;
            float now = Time.unscaledTime;

            _posHistory.Add((now, head.position));
            _posHistory.RemoveAll(e => now - e.t > stillnessWindow);

            // ワーカースレッドの結果をメインスレッドで発火
            if (_pendingResult != null)
            {
                var r = _pendingResult;
                _pendingResult = null;
                _estimating = false;
                _resultText = r.Success
                    ? $"距離: {r.TargetDistanceMeters:F2} m ({r.TargetPointCount}点, {r.ElapsedMs}ms)" +
                      (r.ScaleFactor != 1f ? $" [較正x{r.ScaleFactor:F2}]" : "")
                    : $"失敗: {r.Message}";
                Debug.Log("[MSController] " + _resultText);
                OnResult?.Invoke(r);
            }

            if (!_cameraReady || _captureRequested) return;

            float moved = Vector3.Distance(head.position, _lastKfPos);
            bool still = IsStill();
            if (still && moved >= minBaseline && now - _lastKfTime >= 1f)
                _captureRequested = true;

            _hud = $"KF:{_kfCount}  移動:{Mathf.Min(moved, 99):F2}m  {(still ? "静止" : "移動中")}" +
                   (_estimating ? "  [計算中...]" : "");
        }

        private bool IsStill()
        {
            if (_posHistory.Count < 5) return false;
            if (_posHistory[_posHistory.Count - 1].t - _posHistory[0].t < stillnessWindow * 0.8f)
                return false;
            Vector3 mn = _posHistory[0].p, mx = _posHistory[0].p;
            foreach (var e in _posHistory)
            {
                mn = Vector3.Min(mn, e.p);
                mx = Vector3.Max(mx, e.p);
            }
            return Vector3.Distance(mn, mx) < stillnessThreshold;
        }

        private void OnCameraFrame()
        {
            if (!_captureRequested) return;
            _captureRequested = false;
            try
            {
                var camTex = XREALRGBCameraTexture.Singleton;
                var texY = camTex.GetYUVFormatTextures()[0];
                if (texY == null) return;
                var res = camTex.GetResolution();
                var head = Head;
                if (head == null) return;

                Pose camOffset = Pose.identity;
                XREALPlugin.GetDevicePoseFromHead(
                    XREALComponent.XREAL_COMPONENT_RGB_CAMERA, ref camOffset);
                Vector2 focal = Vector2.zero, principal = Vector2.zero;
                XREALPlugin.GetCameraIntrinsic(
                    XREALComponent.XREAL_COMPONENT_RGB_CAMERA, ref focal, ref principal);

                var kf = MsKeyframe.FromYPlane(
                    texY.GetRawTextureData(), res.x, res.y,
                    focal.x, focal.y, principal.x, principal.y,
                    head.position, head.rotation,
                    camOffset.position, camOffset.rotation);

                SaveKeyframe(texY, res, head, camOffset, focal, principal);
                _lastKfPos = head.position;
                _lastKfTime = Time.unscaledTime;
                _kfCount++;

                if (_prevKf != null && !_estimating)
                {
                    float rotDeg = Quaternion.Angle(_prevKfRot, head.rotation);
                    if (rotDeg > maxRotationDeg)
                    {
                        _resultText = $"警告: キーフレーム間の回転が{rotDeg:F0}°" +
                                      "(向きを変えずに横移動してください)";
                    }
                    var a = _prevKf;
                    var b = kf;
                    LastKeyframeA = a;
                    LastKeyframeB = b;
                    float tb = trueBaselineMeters;
                    _estimating = true;
                    Task.Run(() =>
                    {
                        try { _pendingResult = _estimator.Estimate(a, b, null, tb); }
                        catch (Exception e)
                        {
                            _pendingResult = new MsResult { Message = "例外: " + e.Message };
                        }
                    });
                }
                _prevKf = kf;
                _prevKfRot = head.rotation;
            }
            catch (Exception e)
            {
                Debug.LogError("[MSController] capture failed: " + e);
            }
        }

        private void SaveKeyframe(Texture2D texY, Vector2Int res, Transform head,
                                  Pose camOffset, Vector2 focal, Vector2 principal)
        {
            if (!saveKeyframes || _metaWriter == null) return;
            string imgFile = $"kf_{_kfCount:D3}.y";
            File.WriteAllBytes(Path.Combine(_sessionDir, imgFile), texY.GetRawTextureData());
            Vector3 hp = head.position; Quaternion hq = head.rotation;
            _metaWriter.WriteLine(
                "{" +
                $"\"idx\":{_kfCount},\"file\":\"{imgFile}\",\"w\":{res.x},\"h\":{res.y}," +
                $"\"t_unity\":{Time.unscaledTime:F3},\"t_frame\":0," +
                $"\"head_pos\":[{hp.x:F5},{hp.y:F5},{hp.z:F5}]," +
                $"\"head_rot\":[{hq.x:F6},{hq.y:F6},{hq.z:F6},{hq.w:F6}]," +
                $"\"cam_off_pos\":[{camOffset.position.x:F6},{camOffset.position.y:F6},{camOffset.position.z:F6}]," +
                $"\"cam_off_rot\":[{camOffset.rotation.x:F6},{camOffset.rotation.y:F6},{camOffset.rotation.z:F6},{camOffset.rotation.w:F6}]," +
                $"\"fx\":{focal.x:F3},\"fy\":{focal.y:F3},\"cx\":{principal.x:F3},\"cy\":{principal.y:F3}" +
                "}");
        }

        void OnGUI()
        {
            if (!showDebugHud) return;
            GUI.Label(new Rect(20, 20, 900, 60), $"<size=28><color=lime>{_hud}</color></size>");
            GUI.Label(new Rect(20, 80, 900, 60), $"<size=28><color=yellow>{_resultText}</color></size>");
        }

        void OnDestroy()
        {
            _metaWriter?.Close();
            // カメラの所有者ではないので購読解除だけ (StopCaptureは呼ばない)
            if (_subscribedCamera != null)
            {
                _subscribedCamera.OnRGBCameraUpdate -= OnCameraFrame;
                _subscribedCamera = null;
            }
        }
    }
}
