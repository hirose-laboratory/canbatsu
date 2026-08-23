using CanbatsuMS;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.XR;
using UnityEngine.Rendering.Universal;

/// <summary>
/// AR表示の雛形。作業開始と同時に起動し、グラス側に「空間に固定されたデモ表示」を出す。
/// スマホ画面は今まで通り作業中ページ (UI Toolkit) のまま。
///
/// 仕組み: XREAL SDKが有効だと、シーンのカメラはグラスへ描画される。
/// このクラスはそのカメラ (頭の動きに追従) とデモ用の3Dオブジェクトをコードで組み立てるだけ。
/// 将来はこのデモ部分を「選木マーカー・かかり木危険域・HUD」の各レイヤーに置き換えていく。
///
/// グラス未接続やエディタでは、カメラの描画先が無い/画面裏になるだけで実害はない。
/// </summary>
public class ArDemoController : MonoBehaviour
{
    static ArDemoController _instance;

    Transform _spinner;
    Camera _phoneCamera;
    Camera _arCamera;
    TextMesh _demoText;
    MotionStereoController _motionStereo;

    /// <summary>モーションステレオの直近の計測距離 (m)。未計測なら -1</summary>
    public static float LatestDistanceMeters { get; private set; } = -1f;

    /// <summary>モーションステレオの状態と直近結果 (診断表示用)。AR未起動なら空文字</summary>
    public static string MeasurementStatus =>
        _instance != null && _instance._motionStereo != null
            ? $"{_instance._motionStereo.Hud} {_instance._motionStereo.ResultText}".Trim()
            : "";

    /// <summary>ARデモを開始する (多重起動は無視)。先にXRを手動起動してからデモを組み立てる</summary>
    public static void StartDemo()
    {
        if (_instance != null) return;

        // XRはアプリ起動時ではなくここで立ち上げる (Initialize XR on Startupはオフ運用)
        if (!XrSession.EnsureStarted())
        {
            Debug.LogWarning($"ArDemo: XRなしで続行 ({XrSession.LastError})");
        }

        var go = new GameObject("ArDemo");
        _instance = go.AddComponent<ArDemoController>();
        try
        {
            _instance.BuildRig();
            _instance.BuildDemoContent();
            _instance.BuildMotionStereo();

            // XRが動いている間はUI Toolkitの通常描画がスマホに届かないので、テクスチャ経由に切り替える
            if (XrSession.IsRunning)
            {
                PhoneScreenUi.Attach(_instance._phoneCamera);
            }
        }
        catch (System.Exception e)
        {
            // デモの組み立てに失敗しても作業フロー (カメラ等) は止めない
            Debug.LogException(e);
        }
    }

    /// <summary>ARデモを停止して片付ける (XRも止める)</summary>
    public static void StopDemo()
    {
        if (_instance == null) return;
        PhoneScreenUi.Detach();
        Destroy(_instance.gameObject);
        _instance = null;
        LatestDistanceMeters = -1f;
        XrSession.Stop();
    }

    /// <summary>
    /// モーションステレオ距離計測 (選木の土台) を起動する。
    /// カメラはEyeCameraServiceが起動済みのものを購読する。頭の姿勢はARカメラから取る。
    /// </summary>
    void BuildMotionStereo()
    {
        var go = new GameObject("MotionStereo");
        go.transform.SetParent(transform);
        _motionStereo = go.AddComponent<MotionStereoController>();
        _motionStereo.trackedCamera = _arCamera; // シーンの2D用Main Cameraを掴まないように明示
        _motionStereo.saveKeyframes = false;
        _motionStereo.showDebugHud = false;
        _motionStereo.OnResult += OnMeasured;
    }

    void OnMeasured(MsResult result)
    {
        if (!result.Success) return;
        LatestDistanceMeters = result.TargetDistanceMeters;
        // グラス側のデモテキストにも出す (空間固定なので作業しながら読める)
        if (_demoText != null)
        {
            _demoText.text = $"対象まで {result.TargetDistanceMeters:F2} m\n({result.TargetPointCount}点)";
        }
    }

    void OnDestroy()
    {
        PhoneScreenUi.Detach(); // 想定外の破棄でもスマホ画面の描画経路を元に戻す
    }

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
    /// 何もしないとスマホ画面は最後のフレームのまま止まって見える (作業中ページが出ない原因)。
    /// XREAL SDKの仮想コントローラUIと同じ手法 (allowXRRendering=false) で、スマホ画面 (Display 1) を
    /// 背景色で塗り続けるカメラを置く。その上にUI Toolkitのアプリ画面が載る。3Dは何も描かない。
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

    /// <summary>デモ表示: 起動確認用の適当なオブジェクト群 (あとで本物のレイヤーに差し替える)</summary>
    void BuildDemoContent()
    {
        // 正面2mに回る緑のキューブ (「ARが動いている」ことが一目で分かる目印)
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = "DemoCube";
        cube.transform.SetParent(transform);
        cube.transform.position = new Vector3(0f, 0f, 2f);
        cube.transform.localScale = Vector3.one * 0.3f;
        cube.GetComponent<Renderer>().material = MakeUnlit(new Color(0.3f, 1f, 0.5f));
        Destroy(cube.GetComponent<Collider>());
        _spinner = cube.transform;

        // 足元の周囲に選木マーカー風の輪を置く (空間固定の見え方確認用)
        for (int i = 0; i < 6; i++)
        {
            float angle = i * Mathf.PI * 2f / 6f;
            var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            ring.name = $"DemoMarker_{i}";
            ring.transform.SetParent(transform);
            ring.transform.position = new Vector3(Mathf.Cos(angle) * 2.5f, -1.4f, Mathf.Sin(angle) * 2.5f + 2f);
            ring.transform.localScale = new Vector3(0.6f, 0.02f, 0.6f);
            ring.GetComponent<Renderer>().material =
                MakeUnlit(i % 3 == 0 ? new Color(1f, 0.55f, 0.15f) : new Color(0.3f, 1f, 0.5f));
            Destroy(ring.GetComponent<Collider>());
        }

        // 空間に浮かぶテキスト (日本語はOSフォントにフォールバックして表示される)
        var textGo = new GameObject("DemoText");
        textGo.transform.SetParent(transform);
        textGo.transform.position = new Vector3(0f, 0.5f, 2f);
        var text = textGo.AddComponent<TextMesh>();
        text.text = "CAN伐 ARデモ起動中";
        _demoText = text;
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.GetComponent<MeshRenderer>().material = text.font.material;
        text.fontSize = 48;
        text.characterSize = 0.02f;
        text.anchor = TextAnchor.MiddleCenter;
        text.color = new Color(0.85f, 1f, 0.9f);
    }

    void Update()
    {
        // キューブをゆっくり回す (止まって見えたらアプリが固まっている、の目印にもなる)
        if (_spinner != null)
        {
            _spinner.Rotate(0f, 40f * Time.deltaTime, 0f);
        }
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
                Debug.LogWarning("ArDemo: unlitシェーダーが見つからないので既定マテリアルで表示します");
                return null;
            }
            mat = new Material(shader);
        }
        mat.SetColor("_BaseColor", color);
        mat.color = color; // _BaseColorを持たないシェーダー向けの保険
        return mat;
    }
}
