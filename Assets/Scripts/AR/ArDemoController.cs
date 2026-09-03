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
    readonly List<(GameObject root, TextMesh label)> _markers = new List<(GameObject, TextMesh)>();

    // 作業中に計測した3D点群 (ワールド座標)。木検出 (TreeDetectorMS) の入力として蓄積する
    readonly List<Vector3> _cloudPoints = new List<Vector3>();
    int _treeCount;
    int _tooCloseCount; // 検出中の木のうち近接ペア (過密=オレンジ) の本数
    int _measureCount;  // 成功した計測の回数 (HUD表示用)
    GameObject _hudRoot; // HUDはカメラの子なので_workRootと別に破棄する

    /// <summary>モーションステレオの直近の計測距離 (m)。未計測なら -1</summary>
    public static float LatestDistanceMeters { get; private set; } = -1f;

    /// <summary>モーションステレオの状態と直近結果 (診断表示用)。計測停止中なら空文字</summary>
    public static string MeasurementStatus =>
        _instance != null && _instance._motionStereo != null
            ? ($"{_instance._motionStereo.Hud} {_instance._motionStereo.ResultText} " +
               $"| 計測{_instance._measureCount}回 点群{_instance._cloudPoints.Count} 木{_instance._treeCount}本").Trim()
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
        Destroy(_workRoot); // マーカー・MotionStereoはこの下にいるので一括で片付く
        Application.onBeforeRender -= UpdateHudPose;
        if (_hudRoot != null) Destroy(_hudRoot); // HUDは_workRootの外なので別に破棄する
        _hudRoot = null;
        _workRoot = null;
        _statusText = null;
        _motionStereo = null;
        _markers.Clear();
        _cloudPoints.Clear();
        _treeCount = 0;
        _tooCloseCount = 0;
        _measureCount = 0;
    }

    /// <summary>視界内でのHUDの位置: 右上 (選木の視界を塞がないように)。1.6m先の右上隅</summary>
    static readonly Vector3 HudOffset = new Vector3(0.5f, 0.24f, 1.6f);

    /// <summary>
    /// HUD: 画面の右上に常時固定の状態表示。
    /// 単にカメラの子にするだけではダメ (XRは描画直前に最新の頭の姿勢で画を補正するため、
    /// Update時点の姿勢に置いた子オブジェクトは頭を動かすと泳いで見える)。
    /// そこで Application.onBeforeRender = 描画される直前の最後のタイミングで
    /// 毎フレーム位置を合わせ直すことで画面に固定する。
    /// 1行目に「検出できているか+本数」を大きく色付きで出す。
    /// </summary>
    void BuildStatusBoard()
    {
        _hudRoot = new GameObject("StatusHud");
        _hudRoot.transform.SetParent(transform, false);

        _statusText = _hudRoot.AddComponent<TextMesh>();
        _statusText.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        _statusText.GetComponent<MeshRenderer>().material = _statusText.font.material;
        _statusText.fontSize = 48;
        _statusText.characterSize = 0.0065f;
        _statusText.anchor = TextAnchor.UpperRight;   // 右上隅を基準に左下へ伸びる
        _statusText.alignment = TextAlignment.Right;
        _statusText.color = new Color(0.92f, 1f, 0.95f);
        _statusText.richText = true; // 検出行の色分けに使う

        UpdateHudPose();
        Application.onBeforeRender += UpdateHudPose;
    }

    /// <summary>HUDをカメラの最新姿勢に張り付ける (描画直前に呼ばれるので画面に固定されて見える)</summary>
    void UpdateHudPose()
    {
        if (_hudRoot == null || _arCamera == null) return;
        var cam = _arCamera.transform;
        _hudRoot.transform.SetPositionAndRotation(
            cam.position + cam.rotation * HudOffset,
            cam.rotation);
    }

    // onBeforeRenderが来ない環境 (エディタの一部構成) 向けの保険
    void LateUpdate() => UpdateHudPose();

    void Update()
    {
        if (_arCamera != null)
        {
            // マーカーのラベルは空間固定のままカメラの方を向ける (固定向きだと回り込みで鏡文字になる)
            foreach (var marker in _markers)
            {
                if (marker.label != null) BillboardToCamera(marker.label.transform);
            }
        }

        if (_statusText == null) return;

        // ---- HUD (毎フレーム読み直す) ----
        // 1行目: 検出できているか + 本数 (大きく・色付きで一目で分かるように)
        string detectLine;
        if (_treeCount > 0)
        {
            string tooClose = _tooCloseCount > 0 ? $" (うち過密{_tooCloseCount}本)" : "";
            detectLine = $"<size=56><color=#50FF82>● 検出中: {_treeCount}本{tooClose}</color></size>";
        }
        else if (_measureCount > 0)
        {
            detectLine = "<size=56><color=#FFD24C>● 木は未確定 (計測を重ねて)</color></size>";
        }
        else
        {
            detectLine = "<size=56><color=#BBBBBB>● 検出待ち (まだ計測なし)</color></size>";
        }

        string hud = _motionStereo != null ? _motionStereo.Hud : "";
        string result = _motionStereo != null ? _motionStereo.ResultText : "";
        _statusText.text =
            detectLine + "\n" +
            "<size=30>① 5〜6m歩く ② 木を見て静止 ③ 横に30cm→静止</size>\n" +
            $"<size=34>{hud}\n{result}\n計測 {_measureCount}回 / 点群 {_cloudPoints.Count}点</size>".TrimEnd();
    }

    /// <summary>テキストをカメラの方へ向ける (TextMeshは+Zが背面なので「カメラから遠ざかる向き」を向かせる)</summary>
    void BillboardToCamera(Transform text)
    {
        if (text == null) return;
        var look = text.position - _arCamera.transform.position;
        look.y = 0f; // 上下には傾けない
        if (look.sqrMagnitude > 0.01f)
        {
            text.rotation = Quaternion.LookRotation(look);
        }
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

    /// <summary>
    /// 計測成功: 三角測量点 (0831版からワールド座標付き) を点群に蓄積し、
    /// 木検出 (TreeDetectorMS = AI担当の tree_detect_ms.py C#移植、斜面対応) にかけて、
    /// 検出した幹の位置にマーカーを立て直す。近すぎるペア (=間伐候補) はオレンジで示す。
    /// </summary>
    void OnMeasured(MsResult result)
    {
        if (!result.Success || _motionStereo == null) return;
        LatestDistanceMeters = result.TargetDistanceMeters;
        _measureCount++;

        var kf = _motionStereo.LastKeyframeA;
        if (kf == null || _workRoot == null) return;

        foreach (var p in result.Points)
        {
            _cloudPoints.Add(p.WorldPosition);
        }

        // 距離フィルタの基準は計測時のカメラ位置 (10m超の検出は誤差が大きいので既定で除外される)
        // 地面の高さは木ごとの MsTree.GroundY (局所地面、斜面対応) を使う
        var trees = TreeDetectorMS.Detect(
            _cloudPoints, kf.CamPosition.y, kf.CamPosition, out _, out _);
        _treeCount = trees.Count;

        if (trees.Count > 0)
        {
            RebuildTreeMarkers(trees, kf.CamPosition);
        }
        else
        {
            // まだ幹として確定できる点群がない。従来どおり画像中央の対象に仮マーカーを出して手応えは返す
            float u = kf.Width * 0.5f;
            float v = kf.Height * 0.5f;
            var dirCamera = new Vector3((u - kf.Cx) / kf.Fx, -((v - kf.Cy) / kf.Fy), 1f).normalized;
            var target = kf.CamPosition + kf.CamRotation * dirCamera * result.TargetDistanceMeters;
            ClearMarkers();
            _tooCloseCount = 0;
            PlaceMarker(target, $"{result.TargetDistanceMeters:F1}m?", new Color(0.8f, 0.8f, 0.8f));
        }
    }

    /// <summary>検出した幹ごとにマーカーを立て直す (点群は蓄積式なので毎回作り直すのが簡単で確実)</summary>
    void RebuildTreeMarkers(List<MsTree> trees, Vector3 measureCamPos)
    {
        ClearMarkers();
        _tooCloseCount = 0;
        foreach (var tree in trees)
        {
            if (tree.IsTooClose) _tooCloseCount++;
            float dist = tree.HorizontalDistanceFrom(measureCamPos);
            // 近すぎるペア (1m未満) は過密=間伐候補としてオレンジ表示。それ以外は緑
            var color = tree.IsTooClose ? new Color(1f, 0.55f, 0.1f) : new Color(0.3f, 1f, 0.5f);
            // 足元の高さは木ごとの局所地面 (斜面対応)
            PlaceMarker(tree.TrunkBase, $"{dist:F1}m", color);
        }
    }

    void ClearMarkers()
    {
        foreach (var marker in _markers)
        {
            if (marker.root != null) Destroy(marker.root);
        }
        _markers.Clear();
    }

    /// <summary>木マーカー: 足元の輪 + ラベル (ラベルの向きはUpdateで毎フレームカメラへ向ける)</summary>
    void PlaceMarker(Vector3 groundPos, string text, Color color)
    {
        var root = new GameObject($"TreeMarker_{_markers.Count}");
        root.transform.SetParent(_workRoot.transform);
        root.transform.position = groundPos;

        var ring = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        ring.name = "Ring";
        ring.transform.SetParent(root.transform, false);
        ring.transform.localPosition = new Vector3(0f, 0.02f, 0f);
        ring.transform.localScale = new Vector3(0.8f, 0.02f, 0.8f);
        Destroy(ring.GetComponent<Collider>());
        ring.GetComponent<Renderer>().material = MakeUnlit(color);

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(root.transform, false);
        labelGo.transform.localPosition = new Vector3(0f, 1.4f, 0f);
        var label = labelGo.AddComponent<TextMesh>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.GetComponent<MeshRenderer>().material = label.font.material;
        label.fontSize = 48;
        label.characterSize = 0.02f;
        label.anchor = TextAnchor.LowerCenter;
        label.color = color;
        label.text = text;

        _markers.Add((root, label));
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
        Application.onBeforeRender -= UpdateHudPose;
        PhoneScreenUi.Detach(); // アプリ終了などの想定外の破棄でもスマホ画面の描画経路を元に戻す
    }
}
