using System.Collections.Generic;
using CanbatsuMS;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering.Universal;

/// <summary>
/// AR作業表示のコントローラ。
/// 初回の作業開始でXR・カメラリグ・スマホ描画経路 (PhoneScreenUi) を立ち上げ、
/// **以後アプリ終了まで維持する** (XRの停止/再開とスマホ描画経路の行き来は実機で不安定なため。
/// 作業終了時はグラスに出すコンテンツだけを畳む。グラスは何も表示しない=素通しになる)。
///
/// 作業中の表示は、モーションステレオが実際に識別・計測した木を空間に固定表示する:
///  - 案内板: 計測プロトコルの手順と現在の状態
///  - 木マーカー: 計測した木の足元の輪 + 距離ラベル (計測するたびに増える。選木表示の原型)
/// </summary>
public class ArDemoController : MonoBehaviour
{
    static ArDemoController _instance;

    Camera _phoneCamera;
    Camera _arCamera;
    GameObject _workRoot;   // 作業1回分の表示。終了で破棄する
    TextMesh _statusText;
    MotionStereoController _motionStereo;
    readonly List<(Vector3 pos, TextMesh label)> _markers = new List<(Vector3, TextMesh)>();

    /// <summary>モーションステレオの直近の計測距離 (m)。未計測なら -1</summary>
    public static float LatestDistanceMeters { get; private set; } = -1f;

    /// <summary>モーションステレオの状態と直近結果 (診断表示用)。計測停止中なら空文字</summary>
    public static string MeasurementStatus =>
        _instance != null && _instance._motionStereo != null
            ? $"{_instance._motionStereo.Hud} {_instance._motionStereo.ResultText}".Trim()
            : "";

    /// <summary>作業開始: 初回はXRごと立ち上げ、2回目以降はコンテンツだけ再構築する</summary>
    public static void StartDemo()
    {
        if (_instance == null)
        {
            // XRはアプリ起動時ではなくここで立ち上げる (Initialize XR on Startupはオフ運用)
            if (!XrSession.EnsureStarted())
            {
                Debug.LogWarning($"ArWork: XRなしで続行 ({XrSession.LastError})");
            }

            var go = new GameObject("ArWork");
            _instance = go.AddComponent<ArDemoController>();
            try
            {
                _instance.BuildRig();
                // XRが動いている間はUI Toolkitの通常描画がスマホに届かないので、テクスチャ経由に切り替える
                // (以後アプリ終了までこの経路のまま。経路の行き来はしない)
                if (XrSession.IsRunning)
                {
                    PhoneScreenUi.Attach(_instance._phoneCamera);
                }
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }

        try
        {
            _instance.BeginWork();
        }
        catch (System.Exception e)
        {
            // AR表示の組み立てに失敗しても作業フロー (カメラ等) は止めない
            Debug.LogException(e);
        }
    }

    /// <summary>作業終了: グラスのコンテンツと計測だけを畳む (XRとスマホ描画経路は維持)</summary>
    public static void StopDemo()
    {
        if (_instance == null) return;
        _instance.EndWork();
        LatestDistanceMeters = -1f;
    }

    // ---- 作業コンテンツの組み立て/破棄 ----

    void BeginWork()
    {
        if (_workRoot != null) return; // すでに作業中
        _workRoot = new GameObject("WorkContent");
        _workRoot.transform.SetParent(transform);

        BuildStatusBoard();
        BuildMotionStereo();
    }

    void EndWork()
    {
        if (_workRoot == null) return;
        Destroy(_workRoot); // 案内板・マーカー・MotionStereoはこの下にいるので一括で片付く
        _workRoot = null;
        _statusText = null;
        _motionStereo = null;
        _markers.Clear();
    }

    /// <summary>案内板: 計測プロトコルの手順と状態を出す空間固定のテキスト (開始位置の正面2m)</summary>
    void BuildStatusBoard()
    {
        var boardGo = new GameObject("StatusBoard");
        boardGo.transform.SetParent(_workRoot.transform);

        var origin = _arCamera != null ? _arCamera.transform : transform;
        var forward = origin.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
        forward.Normalize();
        boardGo.transform.position = origin.position + forward * 2f;
        boardGo.transform.rotation = Quaternion.LookRotation(forward);

        _statusText = boardGo.AddComponent<TextMesh>();
        _statusText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        _statusText.GetComponent<MeshRenderer>().material = _statusText.font.material;
        _statusText.fontSize = 48;
        _statusText.characterSize = 0.012f;
        _statusText.anchor = TextAnchor.MiddleCenter;
        _statusText.alignment = TextAlignment.Center;
        _statusText.color = new Color(0.85f, 1f, 0.9f);
    }

    void Update()
    {
        if (_statusText == null) return;
        // 案内板に手順+現在の計測状態を出す (毎フレーム読み直す)
        string hud = _motionStereo != null ? _motionStereo.Hud : "";
        string result = _motionStereo != null ? _motionStereo.ResultText : "";
        _statusText.text =
            "① 5〜6m歩く  ② 木を見て静止\n③ 横に30cmステップして静止\n" +
            $"{hud}\n{result}";
    }

    /// <summary>
    /// モーションステレオ距離計測 (選木の土台) を起動する。
    /// カメラはEyeCameraServiceが起動済みのものを購読する。頭の姿勢はARカメラから取る。
    /// </summary>
    void BuildMotionStereo()
    {
        var go = new GameObject("MotionStereo");
        go.transform.SetParent(_workRoot.transform);
        _motionStereo = go.AddComponent<MotionStereoController>();
        _motionStereo.trackedCamera = _arCamera; // シーンの2D用Main Cameraを掴まないように明示
        _motionStereo.saveKeyframes = false;
        _motionStereo.showDebugHud = false;
        _motionStereo.OnResult += OnMeasured;
    }

    /// <summary>計測成功: 対象の3D位置を逆算して、その場所に木マーカーを立てる</summary>
    void OnMeasured(MsResult result)
    {
        if (!result.Success || _motionStereo == null) return;
        LatestDistanceMeters = result.TargetDistanceMeters;

        var kf = _motionStereo.LastKeyframeA;
        if (kf == null || _workRoot == null) return;

        // 注目領域=画像中央の画素を、キーフレームAのカメラ姿勢と内部パラメータでワールドへ逆投影する
        float u = kf.Width * 0.5f;
        float v = kf.Height * 0.5f;
        var dirCamera = new Vector3(
            (u - kf.Cx) / kf.Fx,
            -((v - kf.Cy) / kf.Fy), // 画像は下向きが+v、カメラ空間は上向きが+y
            1f).normalized;
        var dirWorld = kf.CamRotation * dirCamera;
        var target = kf.CamPosition + dirWorld * result.TargetDistanceMeters;

        PlaceTreeMarker(target, result.TargetDistanceMeters);
    }

    /// <summary>木マーカー: 足元の輪 + 距離ラベル。近い場所の再計測は既存マーカーを更新する</summary>
    void PlaceTreeMarker(Vector3 worldPos, float distanceMeters)
    {
        // 既存マーカーの近く (水平0.8m以内) なら同じ木とみなして距離だけ更新
        for (int i = 0; i < _markers.Count; i++)
        {
            var d = worldPos - _markers[i].pos;
            d.y = 0f;
            if (d.magnitude < 0.8f)
            {
                if (_markers[i].label != null) _markers[i].label.text = $"{distanceMeters:F1}m";
                return;
            }
        }

        // 地面の高さは頭の位置から近似 (目線-1.5m)。地面検出が入ったら置き換える
        float groundY = (_arCamera != null ? _arCamera.transform.position.y : 1.6f) - 1.5f;

        var root = new GameObject($"TreeMarker_{_markers.Count}");
        root.transform.SetParent(_workRoot.transform);
        root.transform.position = new Vector3(worldPos.x, groundY, worldPos.z);

        var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ring.name = "Ring";
        ring.transform.SetParent(root.transform, false);
        ring.transform.localPosition = new Vector3(0f, 0.02f, 0f);
        ring.transform.localScale = new Vector3(0.8f, 0.02f, 0.8f);
        Destroy(ring.GetComponent<Collider>());
        ring.GetComponent<Renderer>().material = MakeUnlit(new Color(0.3f, 1f, 0.5f));

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(root.transform, false);
        labelGo.transform.localPosition = new Vector3(0f, 1.4f, 0f);
        if (_arCamera != null)
        {
            // 立てた瞬間のカメラの方を向ける (TextMeshは+Zが背面なのでこの向きで正しく読める)
            var labelWorldPos = root.transform.position + Vector3.up * 1.4f;
            var look = labelWorldPos - _arCamera.transform.position;
            look.y = 0f; // 上下には傾けない
            if (look.sqrMagnitude > 0.01f)
            {
                labelGo.transform.rotation = Quaternion.LookRotation(look);
            }
        }
        var label = labelGo.AddComponent<TextMesh>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.GetComponent<MeshRenderer>().material = label.font.material;
        label.fontSize = 48;
        label.characterSize = 0.02f;
        label.anchor = TextAnchor.LowerCenter;
        label.color = new Color(0.3f, 1f, 0.5f);
        label.text = $"{distanceMeters:F1}m";

        _markers.Add((root.transform.position, label));
    }

    // ---- リグ (初回だけ作り、以後維持) ----

    /// <summary>頭の動きに追従するカメラ (グラス側の視点) + スマホ画面用カメラ</summary>
    void BuildRig()
    {
        _phoneCamera = BuildPhoneScreenCamera();

        var camGo = new GameObject("AR Camera");
        camGo.transform.SetParent(transform);

        var cam = camGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black; // 光学シースルーでは黒=透明 (現実がそのまま見える)
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 300f;
        cam.cullingMask = ~(1 << LayerMask.NameToLayer("UI")); // スマホ画面用UIはグラスに出さない
        cam.GetUniversalAdditionalCameraData().allowXRRendering = true; // こちらはグラスへ描く
        _arCamera = cam;

        // 頭の位置・向きをカメラに流す (XREAL SDKがXR入力として供給してくる)
        var driver = camGo.AddComponent<TrackedPoseDriver>();
        driver.enabled = false; // アクションを割り当ててから有効化する
        driver.positionInput = new InputActionProperty(new InputAction(binding: "<XRHMD>/centerEyePosition"));
        driver.rotationInput = new InputActionProperty(new InputAction(binding: "<XRHMD>/centerEyeRotation"));
        driver.enabled = true;
    }

    /// <summary>
    /// スマホ画面用カメラ。XRが動いている間、XR用カメラはグラスにしか描かないので、
    /// スマホ画面 (Display 1) を描き続けるカメラが必要 (XREAL SDKの仮想コントローラUIと同じ手法)。
    /// </summary>
    Camera BuildPhoneScreenCamera()
    {
        var go = new GameObject("Phone Screen Camera");
        go.transform.SetParent(transform);

        var cam = go.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.91f, 0.91f, 0.91f); // アプリの背景グレーと同じ
        cam.cullingMask = 1 << LayerMask.NameToLayer("UI"); // スマホ画面用のUIキャンバスだけ描く
        cam.depth = -10;
        // stereoTargetEyeは触らない (XREAL SDKのUIカメラと同じ。allowXRRendering=falseだけで十分)

        var data = cam.GetUniversalAdditionalCameraData();
        data.allowXRRendering = false; // ← これがスマホ画面に描くための肝
        data.renderPostProcessing = false;
        return cam;
    }

    /// <summary>
    /// 単色のunlitマテリアルを作る。
    /// 実機ビルドでは参照されていないシェーダーは削られて Shader.Find が null を返すため、
    /// Resources/AR/ArUnlit.mat (URP Unlitを参照するマテリアル) を元にコピーして色だけ変える。
    /// </summary>
    static Material MakeUnlit(Color color)
    {
        var baseMat = Resources.Load<Material>("AR/ArUnlit");
        Material mat;
        if (baseMat != null)
        {
            mat = new Material(baseMat);
        }
        else
        {
            // 保険: エディタ等ではShader.Findが効く
            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            if (shader == null)
            {
                Debug.LogWarning("ArWork: unlitシェーダーが見つからないので既定マテリアルで表示します");
                return null;
            }
            mat = new Material(shader);
        }
        mat.SetColor("_BaseColor", color);
        mat.color = color; // _BaseColorを持たないシェーダー向けの保険
        return mat;
    }

    void OnDestroy()
    {
        PhoneScreenUi.Detach(); // アプリ終了などの想定外の破棄でもスマホ画面の描画経路を元に戻す
    }
}
