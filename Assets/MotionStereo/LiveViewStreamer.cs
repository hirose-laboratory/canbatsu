using System;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using Unity.XR.XREAL;
#endif

namespace CanbatsuMS
{
    /// <summary>
    /// デモ用ライブビュー配信。
    /// 「実カメラ映像 + ARマーカー」の合成映像(AR写真と同じ見た目)を MJPEG で
    /// HTTP 配信し、同じWi-Fi上のタブレット/PCのブラウザで見られるようにする。
    ///
    ///   タブレット側: ブラウザで http://(Beam ProのIP):8765/ を開くだけ
    ///
    /// 光学シースルーでは「背景の現実」は映像として存在しないため、
    /// 装着者視点の映像はこのようにアプリ内合成でしか作れない(画面キャスト不可)。
    /// クライアント未接続時は合成処理を行わない(計測への負荷ゼロ)。
    /// </summary>
    public class LiveViewStreamer : MonoBehaviour
    {
        [Tooltip("配信ポート")]
        public int port = 8765;
        [Tooltip("配信フレームレート(上げると計測処理への負荷が増える)")]
        public int targetFps = 5;
        [Tooltip("縮小率(2=1280x720→640x360)")]
        public int downscale = 2;
        [Tooltip("JPEG品質(1-100)")]
        public int jpegQuality = 60;
        [Tooltip("合成時に非表示にするルート(HUDなど)")]
        [HideInInspector] public Transform hideDuringRender;

        /// <summary>頭(ARカメラ)のTransform取得元。未設定時のみ Camera.main にフォールバック
        /// (本番ではARカメラを明示設定すること。統合メモ3-3と同じ方針)</summary>
        public Camera sourceCamera;

        /// <summary>配信URL(起動後に確定)。空なら起動失敗</summary>
        public string Url { get; private set; } = "";
        /// <summary>現在の視聴者数</summary>
        public int ClientCount => _clientCount;

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private readonly object _frameLock = new object();
        private byte[] _latestJpeg;
        private long _frameVersion;
        private int _clientCount;

        private Camera _rtCam;
        private RenderTexture _rt;
        private Texture2D _outTex;

        void Awake()
        {
            StartServer();
        }

        void Start()
        {
            StartCoroutine(FrameLoop());
        }

        // ================= HTTPサーバー(ワーカースレッド) =================

        private void StartServer()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Any, port);
                _listener.Start();
                _running = true;
                _acceptThread = new Thread(AcceptLoop) { IsBackground = true };
                _acceptThread.Start();
                Url = $"http://{GetLocalIp()}:{port}/";
                Debug.Log($"[LiveView] 配信開始 {Url}");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[LiveView] サーバー起動失敗: " + e.Message);
                Url = "";
            }
        }

        private static string GetLocalIp()
        {
            string best = "127.0.0.1";
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                        continue;
                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                            continue;
                        string s = addr.Address.ToString();
                        if (s.StartsWith("127.")) continue;
                        best = s;
                        if (s.StartsWith("192.168.")) return s; // Wi-Fi/テザリングを優先
                    }
                }
            }
            catch { }
            return best;
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                try
                {
                    var client = _listener.AcceptTcpClient();
                    var t = new Thread(() => HandleClient(client)) { IsBackground = true };
                    t.Start();
                }
                catch { if (_running) Thread.Sleep(100); }
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                // リクエスト1行目だけ読む(GET /path HTTP/1.1)
                var buf = new byte[2048];
                int n = stream.Read(buf, 0, buf.Length);
                string req = Encoding.ASCII.GetString(buf, 0, Math.Max(n, 0));
                string path = "/";
                var parts = req.Split(' ');
                if (parts.Length >= 2) path = parts[1];

                if (path.StartsWith("/stream"))
                    ServeMjpeg(stream);
                else if (path.StartsWith("/shot"))
                    ServeShot(stream);
                else
                    ServeHtml(stream);
            }
            catch { }
            finally { try { client.Close(); } catch { } }
        }

        private void ServeHtml(NetworkStream stream)
        {
            string html =
                "<!doctype html><html><head><meta charset='utf-8'>" +
                "<meta name='viewport' content='width=device-width,initial-scale=1'>" +
                "<title>CANbatsu LiveView</title>" +
                "<style>html,body{margin:0;height:100%;background:#000}" +
                "img{width:100%;height:100%;object-fit:contain}</style></head>" +
                "<body><img id='v' src='/stream'>" +
                "<script>var v=document.getElementById('v');" +
                "v.onerror=function(){setTimeout(function(){v.src='/stream?'+Date.now()},1000)};" +
                "</script></body></html>";
            byte[] body = Encoding.UTF8.GetBytes(html);
            string head = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n" +
                          $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(head);
            stream.Write(hb, 0, hb.Length);
            stream.Write(body, 0, body.Length);
        }

        private void ServeShot(NetworkStream stream)
        {
            byte[] jpg;
            lock (_frameLock) jpg = _latestJpeg;
            if (jpg == null) jpg = new byte[0];
            string head = "HTTP/1.1 200 OK\r\nContent-Type: image/jpeg\r\n" +
                          $"Content-Length: {jpg.Length}\r\nConnection: close\r\n\r\n";
            byte[] hb = Encoding.ASCII.GetBytes(head);
            stream.Write(hb, 0, hb.Length);
            stream.Write(jpg, 0, jpg.Length);
        }

        private void ServeMjpeg(NetworkStream stream)
        {
            Interlocked.Increment(ref _clientCount);
            try
            {
                string head = "HTTP/1.1 200 OK\r\n" +
                              "Content-Type: multipart/x-mixed-replace; boundary=frame\r\n" +
                              "Cache-Control: no-cache\r\nConnection: close\r\n\r\n";
                byte[] hb = Encoding.ASCII.GetBytes(head);
                stream.Write(hb, 0, hb.Length);

                long seen = -1;
                while (_running)
                {
                    byte[] jpg = null;
                    lock (_frameLock)
                    {
                        if (_frameVersion != seen && _latestJpeg != null)
                        {
                            jpg = _latestJpeg;
                            seen = _frameVersion;
                        }
                    }
                    if (jpg == null)
                    {
                        Thread.Sleep(30);
                        continue;
                    }
                    string part = $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {jpg.Length}\r\n\r\n";
                    byte[] pb = Encoding.ASCII.GetBytes(part);
                    stream.Write(pb, 0, pb.Length);
                    stream.Write(jpg, 0, jpg.Length);
                    stream.Write(new byte[] { 13, 10 }, 0, 2);
                }
            }
            catch { /* クライアント切断 */ }
            finally { Interlocked.Decrement(ref _clientCount); }
        }

        // ================= フレーム生成(メインスレッド) =================

        private IEnumerator FrameLoop()
        {
            var wait = new WaitForSeconds(1f / Mathf.Max(1, targetFps));
            var eof = new WaitForEndOfFrame();
            while (true)
            {
                yield return wait;
                if (_clientCount <= 0) continue;      // 視聴者なし=負荷ゼロ
                yield return eof;                      // 描画完了後にレンダリング
                try { ProduceFrame(); }
                catch (Exception e) { Debug.LogWarning("[LiveView] frame: " + e.Message); }
            }
        }

        private void ProduceFrame()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var camTex = XREALRGBCameraTexture.Singleton;
            var cam = sourceCamera != null ? sourceCamera : Camera.main;
            var head = cam != null ? cam.transform : null;
            if (camTex == null || !camTex.IsCapturing || head == null) return;
            var texs = camTex.GetYUVFormatTextures();
            var res = camTex.GetResolution();
            if (texs[0] == null || res.x <= 0) return;
            int w = res.x, h = res.y;
            int ds = Mathf.Max(1, downscale);
            int sw = w / ds, sh = h / ds;

            // ---- 1. YUV→RGB(縮小サンプリング、BT.601) ----
            byte[] Y = texs[0].GetRawTextureData();
            // 注意: SDKの色差プレーンは [1]=V, [2]=U の順(逆に読むと黄色がシアンになる。実機で確認済み)
            byte[] U = texs[2] != null ? texs[2].GetRawTextureData() : null;
            byte[] V = texs[1] != null ? texs[1].GetRawTextureData() : null;
            int hw = w / 2;
            var pixels = new Color32[sw * sh];
            for (int sy = 0; sy < sh; sy++)
            {
                int y0 = sy * ds;
                int row = y0 * w;
                int crow = (y0 / 2) * hw;
                int srow = sy * sw;
                for (int sx = 0; sx < sw; sx++)
                {
                    int x0 = sx * ds;
                    float yy = Y[row + x0];
                    float uu = U != null ? U[crow + x0 / 2] - 128f : 0f;
                    float vv = V != null ? V[crow + x0 / 2] - 128f : 0f;
                    pixels[srow + sx] = new Color32(
                        (byte)Mathf.Clamp(yy + 1.402f * vv, 0, 255),
                        (byte)Mathf.Clamp(yy - 0.344f * uu - 0.714f * vv, 0, 255),
                        (byte)Mathf.Clamp(yy + 1.772f * uu, 0, 255), 255);
                }
            }

            // ---- 2. RGBカメラ視点でARレイヤをレンダリング(縮小サイズ) ----
            Pose camOffset = Pose.identity;
            XREALPlugin.GetDevicePoseFromHead(
                XREALComponent.XREAL_COMPONENT_RGB_CAMERA, ref camOffset);
            Vector2 focal = Vector2.zero, principal = Vector2.zero;
            XREALPlugin.GetCameraIntrinsic(
                XREALComponent.XREAL_COMPONENT_RGB_CAMERA, ref focal, ref principal);
            float fy = focal.y > 1f ? focal.y : 608f;
            float fx = focal.x > 1f ? focal.x : 608f;

            if (_rtCam == null)
            {
                var go = new GameObject("LiveViewCam");
                go.transform.SetParent(transform, false);
                _rtCam = go.AddComponent<Camera>();
                _rtCam.enabled = false;
                _rtCam.clearFlags = CameraClearFlags.SolidColor;
                _rtCam.backgroundColor = new Color(0, 0, 0, 0);
                _rtCam.nearClipPlane = 0.1f;
            }
            if (_rt == null || _rt.width != sw)
            {
                if (_rt != null) _rt.Release();
                _rt = new RenderTexture(sw, sh, 24);
                _outTex = new Texture2D(sw, sh, TextureFormat.RGBA32, false);
            }
            _rtCam.transform.position = head.position + head.rotation * camOffset.position;
            _rtCam.transform.rotation = head.rotation * camOffset.rotation;
            _rtCam.cullingMask = cam.cullingMask; // ARカメラと同じものだけ描く (スマホUI等の映り込み防止)
            // VR有効時は fieldOfView 代入が拒否されるため、投影行列を直接設定する
            float fov = 2f * Mathf.Atan2(h * 0.5f, fy) * Mathf.Rad2Deg;
            float aspect = (w * fy) / (h * fx);
            _rtCam.projectionMatrix = Matrix4x4.Perspective(fov, aspect, 0.1f, 1000f);
            _rtCam.targetTexture = _rt;
            bool hid = hideDuringRender != null && hideDuringRender.gameObject.activeSelf;
            if (hid) hideDuringRender.gameObject.SetActive(false);
            _rtCam.Render();
            if (hid) hideDuringRender.gameObject.SetActive(true);

            RenderTexture.active = _rt;
            _outTex.ReadPixels(new Rect(0, 0, sw, sh), 0, 0);
            RenderTexture.active = null;
            _rtCam.targetTexture = null;
            var virt = _outTex.GetPixels32();

            // ---- 3. 合成 → JPEG ----
            for (int i = 0; i < pixels.Length; i++)
            {
                var v0 = virt[i];
                if (v0.r + v0.g + v0.b > 30)
                    pixels[i] = new Color32(v0.r, v0.g, v0.b, 255);
            }
            _outTex.SetPixels32(pixels);
            _outTex.Apply(false);
            byte[] jpg = _outTex.EncodeToJPG(jpegQuality);
            lock (_frameLock)
            {
                _latestJpeg = jpg;
                _frameVersion++;
            }
#endif
        }

        void OnDestroy()
        {
            _running = false;
            try { _listener?.Stop(); } catch { }
            if (_rt != null) _rt.Release();
        }
    }
}
