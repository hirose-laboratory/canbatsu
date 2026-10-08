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
    readonly List<List<Vector3>> _recentPairs = new List<List<Vector3>>(); // 直近ペア窓 (古い点は6DoFドリフトでずれるため捨てる)
    MlTreeTracker _mlTracker; // ML幹検出 (null = モデル無し → 従来の幾何方式のみ)
    LiveViewStreamer _liveView; // 検証・デモ用ライブ配信 (同一Wi-Fiのブラウザで視聴。視聴者ゼロなら負荷ゼロ)
    float _gazeDownTimer; // 足元 (真下) を2秒見続けると計測リセット (検証アプリと同じ操作)
    // ラベルの視線追従: ラベルごとの定位置 (見上げると幹に沿って視線の高さまで上がり、戻すと定位置へ)
    readonly Dictionary<TextMesh, Vector3> _labelBasePos = new Dictionary<TextMesh, Vector3>();
    int _treeCount;
    int _tooCloseCount; // 検出中の木のうち近接ペア (過密=オレンジ) の本数
    int _measureCount;  // 成功した計測の回数 (HUD表示用)
    GameObject _hudRoot; // HUDはカメラの子なので_workRootと別に破棄する

    // ---- 選木・基準点・再訪復元の状態 ----

    /// <summary>選木1本分 (セッション座標で保持し、保存時にマップ座標へ変換する)</summary>
    class SelectedTree
    {
        public Vector3 SessionPos; // 幹の足元 (セッション座標)
        public int WidthCm;        // 幹の太さ概算 [cm]
    }

    readonly List<SelectedTree> _selected = new List<SelectedTree>();
    List<MsTree> _latestTrees = new List<MsTree>(); // 直近の検出リスト (視線選木の対象)
    Vector3 _latestMeasureCamPos;                   // 直近計測時のカメラ位置 (マーカー再構築用)

    // 基準点: マップ座標系 = 基準点を原点、基準方向 (yawのXZ投影) を+Z、yは基準点からの相対
    bool _anchorSet;
    Vector3 _anchorPos;
    float _anchorYawRad;
    PlanTreeStore.AnchorGeo _anchorGeo; // 基準点セット時のGPS+方位 (取れなかったらnull)

    // かかり木の危険域表示 (試作。音声「かかりぎ」/ボタンで視界中央の木に対して起動)
    readonly KakarigiDisplay _kakarigi = new KakarigiDisplay();
    float _lastGroundY;       // 直近の計測で推定した地面の高さ (かかり木の根元を視線から求める目安)
    bool _hasGroundEstimate;  // 一度でも計測して地面の高さを得たか
    GameObject _reticle;      // かかり木の支持木・接点を選ぶ間だけ出す視界中央の照準
    MsMapTransform _mapToSession; // マップ→セッション変換 (幹マップ照合が成功すると精密版に置き換わる)

    List<PlanTreeStore.PlanTree> _savedTrees; // この計画の保存済みマップ (読み込み完了までnull)
    readonly List<(GameObject root, TextMesh label, Vector3 mapPos)> _restoredMarkers =
        new List<(GameObject, TextMesh, Vector3)>(); // 前回の選木の復元マーカー (_markersと別に管理)

    string _alignStatus = "";  // 幹マップ照合の結果表示 (成功したときだけ更新)
    string _feedbackText = ""; // 音声/操作の結果フィードバック (3秒で消える)
    float _feedbackUntil;

    /// <summary>同じ木とみなす水平距離 [m] (選木のトグル判定・保存時の重複除去に使う)</summary>
    const float SameTreeRadius = 0.6f;

    static readonly Color SelectedColor = new Color(0.9f, 0.24f, 0.2f);   // 選木=赤
    static readonly Color RestoredColor = new Color(0.3f, 0.75f, 0.95f);  // 前回の選木=水色

    /// <summary>ARカメラ (グラス側の視点)。AR未起動ならnull</summary>
    public static Camera HeadCamera => _instance != null ? _instance._arCamera : null;

    /// <summary>作業中の計画ID。作業ページへの遷移時に設定され、BeginWorkで保存済みマップの読み込みに使う</summary>
    public static string CurrentPlanId;

    /// <summary>モーションステレオの直近の計測距離 (m)。未計測なら -1</summary>
    public static float LatestDistanceMeters { get; private set; } = -1f;

    /// <summary>モーションステレオの状態と直近結果 (診断表示用)。計測停止中なら空文字</summary>
    public static string MeasurementStatus =>
        _instance != null && _instance._motionStereo != null
            ? ($"{_instance._motionStereo.Hud} {_instance._motionStereo.ResultText} " +
               $"| 計測{_instance._measureCount}回 点群{_instance._cloudPoints.Count} 木{_instance._treeCount}本").Trim()
            : "";

    /// <summary>ライブ配信の視聴アドレス (ip:port)。配信していなければ空文字。
    /// 長い計測行に混ぜると画面端で見切れるため、独立した行として表示すること</summary>
    public static string LiveViewUrl =>
        _instance != null && _instance._liveView != null && !string.IsNullOrEmpty(_instance._liveView.Url)
            ? _instance._liveView.Url.Replace("http://", "").TrimEnd('/')
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

        // 再訪復元用: この計画の保存済みマップを裏で読み始める (基準点セット時に使う)
        _savedTrees = null;
        if (!string.IsNullOrEmpty(CurrentPlanId)) LoadSavedMapAsync(CurrentPlanId);
    }

    void EndWork()
    {
        if (_workRoot == null) return;
        SaveTreeMap(); // 終了時点の検出と選木を保存する (基準点セット済みのときだけ)
        Destroy(_workRoot); // マーカー・MotionStereoはこの下にいるので一括で片付く
        Application.onBeforeRender -= UpdateHudPose;
        if (_hudRoot != null) Destroy(_hudRoot); // HUDは_workRootの外なので別に破棄する
        _hudRoot = null;
        _workRoot = null;
        _statusText = null;
        _motionStereo = null;
        _markers.Clear();
        _cloudPoints.Clear();
        _recentPairs.Clear();
        if (_mlTracker != null)
        {
            _mlTracker.Reset();
            _mlTracker.Detector?.Dispose(); // GPUリソース (推論Worker) を解放。次の作業開始で作り直す
            _mlTracker.BendClassifier?.Dispose();
            _mlTracker = null;
        }
        _liveView = null; // 実体はWorkContentと一緒に破棄され、配信サーバーはOnDestroyで停止する
        _treeCount = 0;
        _tooCloseCount = 0;
        _measureCount = 0;
        _selected.Clear();
        _latestTrees.Clear();
        _restoredMarkers.Clear(); // 実体は_workRoot下なので破棄済み
        _labelBasePos.Clear();    // ラベル視線追従の定位置テーブルも空に (破棄済みラベルの残骸を持ち越さない)
        _kakarigi.Clear();        // 実体は_workRoot下だが内部状態 (Zone等) も戻す
        // 照準は作業をまたいで使い回すので隠すだけ (HUDが無いとUpdateHudPoseが隠しに来ないため)
        if (_reticle != null) _reticle.SetActive(false);
        _savedTrees = null;
        _anchorSet = false;
        _anchorGeo = null;
        _alignStatus = "";
        _feedbackText = "";
    }

    /// <summary>保存済みマップを裏で読み込む (BeginWorkから。完了時に作業が続いていれば反映する)</summary>
    async void LoadSavedMapAsync(string planId)
    {
        var map = await PlanTreeStore.LoadAsync(planId);
        // 読み込み中に作業が終わった/別の計画に変わっていたら捨てる
        if (_workRoot == null || planId != CurrentPlanId) return;
        _savedTrees = map.Trees;
        // 基準点が先にセットされていたら、この時点で復元マーカーを出す
        if (_anchorSet)
        {
            int n = RebuildRestoredMarkers();
            if (n > 0) ShowFeedback($"前回の選木{n}本を復元");
        }
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

        // かかり木の支持木・接点を選ぶ間だけ視界中央に照準を出す (HUDと同じく描画直前に張り付けて泳がないように)
        bool aiming = _kakarigi.Current == KakarigiDisplay.State.TargetMarked
                      || _kakarigi.Current == KakarigiDisplay.State.SupportMarked;
        if (aiming && _reticle == null) _reticle = BuildReticle();
        if (_reticle != null)
        {
            _reticle.SetActive(aiming);
            if (aiming)
            {
                _reticle.transform.SetPositionAndRotation(cam.position + cam.forward * 2f, cam.rotation);
            }
        }
    }

    /// <summary>視界中央の照準 (十字)。かかり木の手順で視線がどこに当たっているかを示す</summary>
    GameObject BuildReticle()
    {
        var go = new GameObject("Reticle");
        go.transform.SetParent(transform, false);
        var text = go.AddComponent<TextMesh>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.GetComponent<MeshRenderer>().material = text.font.material;
        text.fontSize = 64;
        text.characterSize = 0.01f;
        text.anchor = TextAnchor.MiddleCenter;
        text.alignment = TextAlignment.Center;
        text.color = new Color(1f, 0.85f, 0.25f);
        text.text = "＋";
        return go;
    }

    // onBeforeRenderが来ない環境 (エディタの一部構成) 向けの保険
    void LateUpdate() => UpdateHudPose();

    void Update()
    {
        if (_arCamera != null)
        {
            // 視線追従の仰角 (見上げるほど正に大きい)
            Vector3 camPos = _arCamera.transform.position;
            Vector3 camFwd = _arCamera.transform.forward;
            float horiz = Mathf.Sqrt(camFwd.x * camFwd.x + camFwd.z * camFwd.z);
            float tanPitch = horiz > 0.01f ? camFwd.y / horiz : (camFwd.y > 0 ? 100f : -100f);

            // マーカーのラベルは空間固定のままカメラの方を向ける (固定向きだと回り込みで鏡文字になる)
            foreach (var marker in _markers)
            {
                if (marker.label == null) continue;
                FollowGaze(marker.label, camPos, tanPitch);
                BillboardToCamera(marker.label.transform);
            }
            foreach (var marker in _restoredMarkers)
            {
                if (marker.label == null) continue;
                FollowGaze(marker.label, camPos, tanPitch);
                BillboardToCamera(marker.label.transform);
            }
            foreach (var label in _kakarigi.Labels)
            {
                if (label != null) BillboardToCamera(label);
            }

            // リセットジェスチャー: 足元 (真下58度以上) を2秒見続けると計測をやり直す
            if (_workRoot != null && _motionStereo != null)
            {
                bool lookingDown = Vector3.Dot(_arCamera.transform.forward, Vector3.down) > 0.85f;
                if (lookingDown)
                {
                    _gazeDownTimer += Time.deltaTime;
                    if (_gazeDownTimer >= 2f)
                    {
                        ResetSurvey();
                        _gazeDownTimer = -3f; // 連続発動を防ぐクールダウン
                    }
                    else if (_gazeDownTimer > 0.5f)
                    {
                        ShowFeedback($"リセットまで {2f - _gazeDownTimer:F1} 秒 (足元を見続ける)");
                    }
                }
                else
                {
                    _gazeDownTimer = 0f;
                }
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

        // 選木の本数 (前回分は基準点セット後だけ意味を持つ)
        string selectionLine = $"選木: {_selected.Count}本";
        int restoredSelected = CountSavedSelected();
        if (_anchorSet && restoredSelected > 0) selectionLine += $" (前回{restoredSelected}本)";

        string extraLines = $"\n<size=34>{selectionLine}</size>";
        if (!_anchorSet && _selected.Count > 0)
        {
            // 基準点がないとマップ座標に変換できず保存されないことを知らせる
            extraLines += "\n<size=30><color=#FFD24C>基準点未セット (保存は座標なし扱い)</color></size>";
        }
        if (_alignStatus.Length > 0)
        {
            extraLines += $"\n<size=30><color=#4CD9F0>{_alignStatus}</color></size>";
        }
        if (_kakarigi.Active)
        {
            // 手順の途中は次の操作の案内 (ナビ) なので大きめ・黄色、表示中は結果なのでオレンジ
            extraLines += _kakarigi.Current != KakarigiDisplay.State.Shown
                ? $"\n<size=40><color=#FFD84A>【かかり木】次: {_kakarigi.Summary}</color></size>"
                : $"\n<size=30><color=#FF8C40>かかり木: {_kakarigi.Summary}</color></size>";
            // 危険域の中にいるあいだは点滅の強い警告を出す
            if (_arCamera != null && _kakarigi.Contains(_arCamera.transform.position))
            {
                bool blink = (int)(Time.time * 3f) % 2 == 0;
                string kindText = "危険域内";
                if (_kakarigi.Zone.TryGetWorstKind(_arCamera.transform.position, out var kind))
                {
                    kindText = kind == HangUpTree.Core.DangerRegionKind.MainFall ? "主落下側の危険域内"
                        : kind == HangUpTree.Core.DangerRegionKind.Kickback ? "跳ね返り側の危険域内"
                        : "立入禁止円の中";
                }
                if (blink) extraLines += $"\n<size=56><color=#FF3020>⚠ {kindText}! 離れて</color></size>";
            }
        }
        if (_feedbackText.Length > 0 && Time.time < _feedbackUntil)
        {
            // 音声/操作の結果フィードバック (3秒で消える)
            extraLines += $"\n<size=34><color=#FFFFFF>{_feedbackText}</color></size>";
        }

        string hud = _motionStereo != null ? _motionStereo.Hud : "";
        string result = _motionStereo != null ? _motionStereo.ResultText : "";
        _statusText.text =
            detectLine + "\n" +
            "<size=30>① 5〜6m歩く ② 木を見て静止 ③ 横に30cm→静止</size>\n" +
            $"<size=34>{hud}\n{result}\n計測 {_measureCount}回 / 点群 {_cloudPoints.Count}点</size>".TrimEnd() +
            extraLines;
    }

    /// <summary>保存済みマップのうち選木されている本数</summary>
    int CountSavedSelected()
    {
        if (_savedTrees == null) return 0;
        int n = 0;
        foreach (var t in _savedTrees)
        {
            if (t.Selected) n++;
        }
        return n;
    }

    /// <summary>操作結果の1行フィードバックをHUDに出す (3秒で自動で消える)</summary>
    void ShowFeedback(string message)
    {
        _feedbackText = message;
        _feedbackUntil = Time.time + 3f;
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
        // 三角測量の品質を検証アプリと同一に締める (点の質が上がり、1回の計測も速くなる)
        _motionStereo.EstimatorConfig.MaxRaySkewMeters = 0.15f;
        _motionStereo.EstimatorConfig.MinDistance = 0.5f;
        _motionStereo.EstimatorConfig.MaxDistance = 25f;
        _motionStereo.OnResult += OnMeasured;

        // ML幹検出 (Resources/AR/trunk_mix.onnx)。読込失敗時は null → 幾何方式のみで従来どおり動く
        _mlTracker = new MlTreeTracker
        {
            Detector = TrunkDetectorML.TryCreate(),
            BendClassifier = BendClassifierML.TryCreate(), // 曲がり木分類 (モデルが無ければnull=判定なし)
        };
        StartCoroutine(WarmUpMl());

        // 検証・デモ用ライブ配信: 同一Wi-Fi (スマホテザリング可・圏外OK) のブラウザで
        // 装着者視点 (実景+マーカー合成) を視聴できる。URLは作業中ページの診断行に表示
        _liveView = _workRoot.AddComponent<LiveViewStreamer>();
        _liveView.sourceCamera = _arCamera;
        _liveView.hideDuringRender = null; // HUD (「移動中」等の案内板) も配信に映す (隠したくなったら _hudRoot.transform を渡す)
    }

    /// <summary>ML推論の初回はGPUシェーダー準備で4〜8秒かかるため、起動直後に空推論で温めておく</summary>
    System.Collections.IEnumerator WarmUpMl()
    {
        if (_mlTracker == null || _mlTracker.Detector == null) yield break;
        yield return new WaitForSeconds(2f); // AR起動処理が落ち着いてから
        var dummy = new MsKeyframe
        {
            Width = 1280, Height = 720,
            Fx = 608f, Fy = 608f, Cx = 640f, Cy = 360f,
            Image = new float[1280 * 720],
        };
        try { _mlTracker.Detector.Detect(dummy); }
        catch (System.Exception e) { Debug.LogWarning("[TrunkML] warmup失敗: " + e.Message); }
        _mlTracker.BendClassifier?.WarmUp();
        Debug.Log("[TrunkML] warmup完了");
    }

    /// <summary>
    /// 計測成功: 三角測量点 (0831版からワールド座標付き) を点群に蓄積し、
    /// 木検出 (TreeDetectorMS = AI担当の tree_detect_ms.py C#移植、斜面対応) にかけて、
    /// 検出した幹の位置にマーカーを立て直す。近すぎるペア (=間伐候補) はオレンジで示す。
    /// </summary>
    void OnMeasured(MsResult result)
    {
        if (_motionStereo == null) return;
        if (result.Success)
        {
            LatestDistanceMeters = result.TargetDistanceMeters;
            _measureCount++;
        }

        var kf = _motionStereo.LastKeyframeA;
        if (kf == null || _workRoot == null) return;
        // 中央に対象が無い (Success=false) ペアの点群も木検出には使える。弱いペアだけ捨てる
        if (result.Points.Count < 30) return;

        // 蓄積は直近6ペアまで (古いペアは6DoFドリフトでずれ、位置ズレ・誤検出の原因になる)
        var pairPts = new List<Vector3>(result.Points.Count);
        foreach (var p in result.Points)
        {
            pairPts.Add(p.WorldPosition);
        }
        _recentPairs.Add(pairPts);
        while (_recentPairs.Count > 6) _recentPairs.RemoveAt(0);
        _cloudPoints.Clear();
        foreach (var pp in _recentPairs) _cloudPoints.AddRange(pp);

        // 距離フィルタの基準は計測時のカメラ位置 (10m超の検出は誤差が大きいので既定で除外される)
        // 地面の高さは木ごとの MsTree.GroundY (局所地面、斜面対応) を使う
        var trees = TreeDetectorMS.Detect(
            _cloudPoints, kf.CamPosition.y, kf.CamPosition, out float ground, out _);
        _lastGroundY = ground;
        _hasGroundEstimate = true;

        // ML幹検出: 確定した木があれば幾何方式の結果を置き換える。
        // MLは「2回以上の計測で確認できた木」だけを返す (幽霊対策) ため、序盤1〜2回は幾何方式のまま。
        // スケール較正は未導入なので sessionScale=1 (SLAM座標をそのまま実寸として扱う)
        var mlTrees = _mlTracker != null
            ? _mlTracker.Track(result, kf, 1f, ground, kf.CamPosition)
            : null;
        if (mlTrees != null && mlTrees.Count > 0) trees = mlTrees;

        _treeCount = trees.Count;
        _latestTrees = trees;                 // 視線選木の対象として保持する
        _latestMeasureCamPos = kf.CamPosition;

        if (trees.Count > 0)
        {
            RebuildTreeMarkers(trees, kf.CamPosition);
            TryAlignSavedMap(trees); // 保存済みマップと幹配置を照合し、復元マーカーを精密位置へ置き直す
        }
        else if (result.Success)
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

        // 近すぎるペアをここで判定し直す (ML経由の木は IsTooClose が未設定のため)。
        // しきい値は作業計画の設定値と連動: 伐採間隔 (FellingIntervalM) と伐採基準 (FellingStandardCm=この直径未満は間伐候補)
        WorkPlan plan = null;
        if (!string.IsNullOrEmpty(CurrentPlanId))
        {
            foreach (var p in PlanStore.Plans)
            {
                if (p.Id == CurrentPlanId) { plan = p; break; }
            }
        }
        float closePairMeters = plan != null && plan.FellingIntervalM > 0.1f ? plan.FellingIntervalM : 1.0f;
        int thinStandardCm = plan != null ? plan.FellingStandardCm : 0; // 0 = 基準なし
        var pairs = new List<(int a, int b, float d)>();
        foreach (var t in trees) t.IsTooClose = false;
        for (int i = 0; i < trees.Count; i++)
        {
            for (int j = i + 1; j < trees.Count; j++)
            {
                float dx = trees[i].X - trees[j].X, dz = trees[i].Z - trees[j].Z;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d < closePairMeters)
                {
                    pairs.Add((i, j, d));
                    trees[i].IsTooClose = true;
                    trees[j].IsTooClose = true;
                }
            }
        }

        _tooCloseCount = 0;
        foreach (var tree in trees)
        {
            if (tree.IsTooClose) _tooCloseCount++;
            float dist = tree.HorizontalDistanceFrom(measureCamPos);
            // 伐採基準: 太さが測れていて基準未満なら間伐候補 (近すぎペアと同じオレンジ)
            int widthCm = Mathf.RoundToInt(tree.WidthMeters * 100f);
            bool thin = thinStandardCm > 0 && tree.WidthMeters > 0.02f && widthCm < thinStandardCm;
            // 曲がり木: 分類モデルのスコアがしきい値以上なら間伐候補 (形質不良は優先的に伐る対象)
            bool bent = tree.BentScore >= BendClassifierML.Threshold;
            // 色の優先順位: 選木=赤 > 間伐候補 (近すぎペア or 基準未満 or 曲がり) のオレンジ > 通常の青
            bool selected = FindSelectedIndexNear(tree.TrunkBase) >= 0;
            var color = selected ? SelectedColor
                : (tree.IsTooClose || thin || bent) ? new Color(1f, 0.55f, 0.1f)
                : new Color(0.35f, 0.6f, 1f);
            // ラベルは2行: 距離 + 太さ (幅の推定が小さすぎる=根拠不足のときは距離だけ)
            string label = $"{dist:F1}m";
            if (tree.WidthMeters > 0.02f)
            {
                label += $"\n太さ{widthCm}cm" + (thin ? " 基準未満" : "");
            }
            if (bent) label += "\n曲がり候補";
            if (selected) label = "伐 " + label;
            // 足元の高さは木ごとの局所地面 (斜面対応)。円柱の太さは幹の推定幅から
            PlaceMarker(tree.TrunkBase, label, color, tree.WidthMeters);
        }

        // 近すぎるペアは幹の間に棒を渡して間隔を表示 (どの木とどの木のペアかを明示)
        foreach (var (a, b, d) in pairs)
        {
            PlacePairBar(trees[a], trees[b], d);
        }
    }

    /// <summary>近すぎるペアの2本の幹の間に棒を渡し、中点に間隔を表示する</summary>
    void PlacePairBar(MsTree a, MsTree b, float distMeters)
    {
        var root = new GameObject("ClosePairBar");
        root.transform.SetParent(_workRoot.transform);
        var barColor = new Color(1f, 0.45f, 0.2f);
        float y = (a.GroundY + b.GroundY) * 0.5f + 1.2f;
        var pA = new Vector3(a.X, y, a.Z);
        var pB = new Vector3(b.X, y, b.Z);

        var bar = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        bar.transform.SetParent(root.transform, false);
        Destroy(bar.GetComponent<Collider>());
        bar.transform.position = (pA + pB) * 0.5f;
        bar.transform.rotation = Quaternion.FromToRotation(Vector3.up, pB - pA);
        bar.transform.localScale = new Vector3(0.15f, Vector3.Distance(pA, pB) * 0.5f, 0.15f); // 円柱は高さ2unit基準
        bar.GetComponent<Renderer>().material = MakeUnlit(barColor);

        foreach (var pe in new[] { pA, pB }) // 両端の球で「どの木のペアか」を明示
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            s.transform.SetParent(root.transform, false);
            Destroy(s.GetComponent<Collider>());
            s.transform.position = pe;
            s.transform.localScale = Vector3.one * 0.32f;
            s.GetComponent<Renderer>().material = MakeUnlit(barColor);
        }

        var labelGo = new GameObject("PairLabel");
        labelGo.transform.SetParent(root.transform, false);
        labelGo.transform.position = (pA + pB) * 0.5f + new Vector3(0f, -0.2f, 0f); // 棒の下側 (木ラベルと重ねない)
        var label = labelGo.AddComponent<TextMesh>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.GetComponent<MeshRenderer>().material = label.font.material;
        label.fontSize = 48;
        label.characterSize = 0.014f;
        label.anchor = TextAnchor.UpperCenter;
        label.color = barColor;
        label.text = $"{distMeters:F2}m";
        _labelBasePos[label] = labelGo.transform.position; // 視線追従の定位置

        _markers.Add((root, label)); // ラベルのビルボードと一括破棄は既存の仕組みに乗せる
    }

    /// <summary>計測のやり直し: 点群・ML追跡・検出マーカーを消す。
    /// 選木済み (_selected) と保存マップの復元マーカーは作業成果なので消さない</summary>
    void ResetSurvey()
    {
        _recentPairs.Clear();
        _cloudPoints.Clear();
        _mlTracker?.Reset();
        _latestTrees.Clear();
        ClearMarkers();
        _treeCount = 0;
        _tooCloseCount = 0;
        ShowFeedback("計測をリセットしました");
        Debug.Log("[ArWork] 計測リセット (足元注視)");
    }

    /// <summary>見上げたときラベルが幹の鉛直軸に沿って視線の高さまで上がる (最大+6m)。
    /// 正面に戻すと定位置へ。急な動きでガクつかないよう平滑化する (検証アプリと同じ挙動)</summary>
    void FollowGaze(TextMesh label, Vector3 camPos, float tanPitch)
    {
        if (!_labelBasePos.TryGetValue(label, out var basePos)) return;
        float dx = basePos.x - camPos.x, dz = basePos.z - camPos.z;
        float dh = Mathf.Sqrt(dx * dx + dz * dz);
        float targetY = Mathf.Clamp(camPos.y + tanPitch * dh, basePos.y, basePos.y + 6f);
        var pos = label.transform.position;
        pos.x = basePos.x;
        pos.z = basePos.z;
        pos.y = Mathf.Lerp(pos.y, targetY, 0.25f);
        label.transform.position = pos;
    }

    void ClearMarkers()
    {
        foreach (var marker in _markers)
        {
            if (marker.label != null) _labelBasePos.Remove(marker.label);
            if (marker.root != null) Destroy(marker.root);
        }
        _markers.Clear();
    }

    /// <summary>木マーカー: 幹に重なる縦の円柱 + ラベル (ラベルの向きはUpdateで毎フレームカメラへ向ける)</summary>
    void PlaceMarker(Vector3 groundPos, string text, Color color, float widthMeters = 0.25f)
    {
        var marker = CreateMarker(groundPos, text, color, widthMeters);
        _markers.Add(marker);
    }

    /// <summary>マーカーの実体を作る (検出マーカーと復元マーカーで共用)。
    /// 検証アプリと同じ「高さ2mの縦円柱」方式 (足元の輪より遠目でも見やすい)</summary>
    (GameObject root, TextMesh label) CreateMarker(Vector3 groundPos, string text, Color color, float widthMeters = 0.25f)
    {
        var root = new GameObject($"TreeMarker_{_markers.Count + _restoredMarkers.Count}");
        root.transform.SetParent(_workRoot.transform);
        root.transform.position = groundPos;

        const float markerHeight = 2.0f;
        var trunk = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        trunk.name = "Trunk";
        trunk.transform.SetParent(root.transform, false);
        trunk.transform.localPosition = new Vector3(0f, markerHeight * 0.5f, 0f);
        float w = Mathf.Clamp(widthMeters * 0.6f, 0.10f, 0.35f); // 幹の推定幅の6割 (実物を隠しすぎない)
        trunk.transform.localScale = new Vector3(w, markerHeight * 0.5f, w); // 円柱は高さ2unit基準
        Destroy(trunk.GetComponent<Collider>());
        trunk.GetComponent<Renderer>().material = MakeUnlit(color);

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(root.transform, false);
        labelGo.transform.localPosition = new Vector3(0f, markerHeight + 0.35f, 0f); // 円柱の頭上
        var label = labelGo.AddComponent<TextMesh>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.GetComponent<MeshRenderer>().material = label.font.material;
        label.fontSize = 48;
        label.characterSize = 0.02f;
        label.anchor = TextAnchor.LowerCenter;
        label.color = color;
        label.text = text;
        _labelBasePos[label] = labelGo.transform.position; // 視線追従の定位置

        return (root, label);
    }

    // ---- 選木 (視線トグル) ----

    /// <summary>視線の先の木を選木する (0.6m以内に選木済みがあれば解除のトグル)。戻り値=操作できたか</summary>
    public static bool MarkTreeAtGaze()
    {
        if (_instance == null || _instance._workRoot == null) return false;
        return _instance.ToggleAtGaze(unmarkOnly: false);
    }

    /// <summary>視線の先の木の選木を解除する。戻り値=操作できたか</summary>
    public static bool UnmarkTreeAtGaze()
    {
        if (_instance == null || _instance._workRoot == null) return false;
        return _instance.ToggleAtGaze(unmarkOnly: true);
    }

    /// <summary>
    /// 視線の先の木を選木/解除する。対象は直近の検出リストのうち
    /// 「幹 (足元+1m) への方向とカメラforwardの角度が最小かつ12°以内、水平10m以内」の木。
    /// 今回の選木だけでなく、前回から復元した選木 (水色) もここで解除できる。
    /// </summary>
    bool ToggleAtGaze(bool unmarkOnly)
    {
        var tree = FindGazeTree();
        if (tree == null)
        {
            ShowFeedback("視線の先に木がありません");
            return false;
        }

        int idx = FindSelectedIndexNear(tree.TrunkBase);
        int restoredIdx = FindRestoredSelectedNear(tree.TrunkBase);
        if (idx >= 0)
        {
            _selected.RemoveAt(idx);
            ShowFeedback("マークを外しました");
        }
        else if (restoredIdx >= 0)
        {
            _savedTrees[restoredIdx].Selected = false;
            RebuildRestoredMarkers();
            ShowFeedback("前回のマークを外しました");
        }
        else if (unmarkOnly)
        {
            ShowFeedback("この木はマークされていません");
            return false;
        }
        else
        {
            _selected.Add(new SelectedTree
            {
                SessionPos = tree.TrunkBase,
                WidthCm = Mathf.RoundToInt(tree.WidthMeters * 100f),
            });
            ShowFeedback("マークしました");
        }

        // マーカーの色/ラベルを反映し、基準点セット済みなら保存する
        if (_latestTrees.Count > 0) RebuildTreeMarkers(_latestTrees, _latestMeasureCamPos);
        SaveTreeMap();
        return true;
    }

    /// <summary>視線の先の木を返す (角度最小かつ12°以内、水平10m以内)。該当なしはnull</summary>
    MsTree FindGazeTree()
    {
        if (_arCamera == null) return null;
        var camPos = _arCamera.transform.position;
        var forward = _arCamera.transform.forward;
        MsTree best = null;
        float bestAngle = 12f; // これ以内でないと対象にしない
        foreach (var tree in _latestTrees)
        {
            if (tree.HorizontalDistanceFrom(camPos) > 10f) continue;
            // 狙い先は幹の見た目の中心あたり (足元+1m)
            var toTree = tree.TrunkBase + Vector3.up * 1f - camPos;
            float angle = Vector3.Angle(forward, toTree);
            if (angle <= bestAngle)
            {
                bestAngle = angle;
                best = tree;
            }
        }
        return best;
    }

    /// <summary>この位置の近く (水平0.6m以内) の選木を探す。無ければ-1</summary>
    int FindSelectedIndexNear(Vector3 sessionPos)
    {
        for (int i = 0; i < _selected.Count; i++)
        {
            var d = _selected[i].SessionPos - sessionPos;
            d.y = 0f;
            if (d.sqrMagnitude <= SameTreeRadius * SameTreeRadius) return i;
        }
        return -1;
    }

    /// <summary>この位置の近くにある「前回から復元した選木」を保存済みマップから探す。無ければ-1</summary>
    int FindRestoredSelectedNear(Vector3 sessionPos)
    {
        if (!_anchorSet || _savedTrees == null) return -1;
        for (int i = 0; i < _savedTrees.Count; i++)
        {
            if (!_savedTrees[i].Selected) continue;
            var d = _mapToSession.Apply(_savedTrees[i].MapPos) - sessionPos;
            d.y = 0f;
            if (d.sqrMagnitude <= SameTreeRadius * SameTreeRadius) return i;
        }
        return -1;
    }

    // ---- かかり木モード (試作) ----

    /// <summary>
    /// 手順1: 視界中央 = かかり木の根元を見ている前提で、根元に印を付ける (音声「かかり」/ボタン「かかり木」)。
    /// 戻り値=マークできたか
    /// </summary>
    public static bool MarkKakarigiAtGaze()
    {
        if (_instance == null || _instance._workRoot == null || _instance._arCamera == null) return false;
        return _instance.MarkKakarigiTarget();
    }

    /// <summary>手順2: 視界中央の木の根元を支持木として印を付ける (音声「しじ」/ボタン「支持木」)</summary>
    public static bool MarkSupportAtGaze()
    {
        if (_instance == null || _instance._arCamera == null) return false;
        return _instance.MarkSupport();
    }

    /// <summary>
    /// 手順3: 今見ている所を接点 (かかり木が支持木に触れている所) として確定し、危険域を出す
    /// (音声「せってん」/ボタン「接点」)。表示中に1m以上動いてもう一度呼ぶと2視点の三角測量に格上げ
    /// </summary>
    public static bool MarkContactAtGaze()
    {
        if (_instance == null || _instance._arCamera == null) return false;
        bool ok = _instance._kakarigi.MarkContact(_instance.GazeRay(), out string message);
        _instance.ShowFeedback(message);
        return ok;
    }

    /// <summary>かかり木モードを終える (音声「かいじょ」/ボタン「かかり木解除」)。どの手順の途中でも止められる</summary>
    public static void ClearKakarigi()
    {
        if (_instance == null) return;
        _instance._kakarigi.Clear();
        _instance.ShowFeedback("かかり木モードを終了しました");
    }

    /// <summary>かかり木モードの段階 (作業ページのボタンの文言切替用)</summary>
    public static KakarigiDisplay.State KakarigiState =>
        _instance != null ? _instance._kakarigi.Current : KakarigiDisplay.State.Idle;

    /// <summary>かかり木モード中か (手順の途中または表示中)</summary>
    public static bool KakarigiActive => _instance != null && _instance._kakarigi.Active;

    /// <summary>視界の中心を通る視線 (音声・ボタン操作はすべてこの視線で対象を決める)</summary>
    Ray GazeRay() => new Ray(_arCamera.transform.position, _arCamera.transform.forward);

    bool MarkKakarigiTarget()
    {
        var gaze = GazeRay();

        // 根元の目安: 視線と「直近の計測で推定した地面の高さ」の交点。その周りで地面平面を当てはめ直す。
        // まだ計測していなければ目の高さ (1.5m) 下を地面とみなす
        float groundY = _hasGroundEstimate ? _lastGroundY : _arCamera.transform.position.y - 1.5f;
        var roughGround = new Plane(Vector3.up, new Vector3(0f, groundY, 0f));
        if (!roughGround.Raycast(gaze, out float t) || t <= 0f)
        {
            ShowFeedback("かかり木の根元 (地面との境目) を見てください");
            return false;
        }
        Plane ground = FitGroundPlane(gaze.GetPoint(t));

        bool ok = _kakarigi.MarkTarget(_workRoot.transform, gaze, ground, out string message);
        ShowFeedback(message);
        return ok;
    }

    bool MarkSupport()
    {
        if (!_kakarigi.TryGroundPoint(GazeRay(), out Vector3 point))
        {
            ShowFeedback(_kakarigi.Active
                ? "支持木の根元 (地面との境目) を見てください"
                : "先に「かかり木」で根元をマークしてください");
            return false;
        }

        // 近く (1m以内) に検出済みの木があれば、その計測した根元に吸着させる (視線と地面の交点より正確)
        float bestD = 1f * 1f;
        foreach (var tree in _latestTrees)
        {
            float dx = tree.X - point.x, dz = tree.Z - point.z;
            float d2 = dx * dx + dz * dz;
            if (d2 < bestD)
            {
                bestD = d2;
                point = tree.TrunkBase;
            }
        }

        bool ok = _kakarigi.MarkSupport(point, out string message);
        ShowFeedback(message);
        return ok;
    }

    /// <summary>
    /// 指定位置の周辺の地面平面を計測点群から推定する (斜面なら傾いた平面になり、
    /// 危険域が谷側へ伸びる計算に効く)。点が足りなければその木の足元の水平面。
    /// 高さの当てはめ y=ax+bz+c を最小二乗で解く。
    /// </summary>
    Plane FitGroundPlane(Vector3 around)
    {
        var xs = new List<Vector3>();
        foreach (var p in _cloudPoints)
        {
            float dx = p.x - around.x, dz = p.z - around.z;
            // 周囲8m以内の低い点 (地面付近) だけ使う
            if (dx * dx + dz * dz < 64f && p.y < around.y + 0.5f) xs.Add(p);
        }
        if (xs.Count < 12) return new Plane(Vector3.up, around);

        // 正規方程式 (3x3) を組んで y = a*x + b*z + c を解く
        double sxx = 0, sxz = 0, sx = 0, szz = 0, sz = 0, n = xs.Count;
        double sxy = 0, szy = 0, sy = 0;
        foreach (var p in xs)
        {
            sxx += p.x * (double)p.x; sxz += p.x * (double)p.z; sx += p.x;
            szz += p.z * (double)p.z; sz += p.z;
            sxy += p.x * (double)p.y; szy += p.z * (double)p.y; sy += p.y;
        }
        double det = sxx * (szz * n - sz * sz) - sxz * (sxz * n - sz * sx) + sx * (sxz * sz - szz * sx);
        if (System.Math.Abs(det) < 1e-6) return new Plane(Vector3.up, around);
        double a = (sxy * (szz * n - sz * sz) - sxz * (szy * n - sz * sy) + sx * (szy * sz - szz * sy)) / det;
        double b = (sxx * (szy * n - sy * sz) - sxy * (sxz * n - sz * sx) + sx * (sxz * sy - szy * sx)) / det;

        var normal = new Vector3(-(float)a, 1f, -(float)b).normalized;
        // 平面推定の外れで危険域が立ち上がらないよう、傾きは35°までに制限する
        if (Vector3.Angle(normal, Vector3.up) > 35f) return new Plane(Vector3.up, around);
        return new Plane(normal, around);
    }

    // ---- 基準点とマップ座標系 ----

    /// <summary>
    /// 現在のカメラ位置とyawをセッションの基準点にする。
    /// 保存済みマップがあれば前回の選木を復元表示する。戻り値=セットできたか
    /// </summary>
    public static bool SetAnchorHere()
    {
        if (_instance == null || _instance._workRoot == null || _instance._arCamera == null) return false;
        return _instance.SetAnchor();
    }

    bool SetAnchor()
    {
        var cam = _arCamera.transform;
        var forward = cam.forward;
        forward.y = 0f; // yawだけ使う (上下の傾きは基準にしない)
        // トラッキングが外れていると位置や向きが NaN になることがある (NaN は < 比較を素通りするので先に弾く)
        if (float.IsNaN(forward.x) || float.IsNaN(forward.z)
            || float.IsNaN(cam.position.x) || float.IsNaN(cam.position.y) || float.IsNaN(cam.position.z))
        {
            ShowFeedback("位置を見失っています。少し動いてからもう一度基準点をセットしてください");
            return false;
        }
        if (forward.sqrMagnitude < 1e-6f)
        {
            ShowFeedback("真上/真下を向いたままでは基準点にできません");
            return false;
        }

        _anchorPos = cam.position;
        _anchorYawRad = Mathf.Atan2(forward.x, forward.z);
        _anchorSet = true;
        _mapToSession = AnchorTransform(); // 照合が成功するまでは基準点由来の変換で表示する

        // 撮影セッションにも基準点を残す (サーバー側の3Dマップ化が、このセッションの
        // 座標系→計画のマップ座標系の変換に使う。無いセッションはサーバー処理の対象外になる)
        PlanCaptureService.Active?.WriteAnchor(_anchorPos, _anchorYawRad);

        // 基準点の地理情報 (GPS+方位)。選木結果を2Dの地図に載せる位置合わせに使う (取れたときだけ)
        if (LocationProvider.HasFix)
        {
            _anchorGeo = new PlanTreeStore.AnchorGeo
            {
                Lat = LocationProvider.Latitude,
                Lon = LocationProvider.Longitude,
                HeadingDeg = LocationProvider.HeadingDeg,
                HasHeading = LocationProvider.HasHeading,
            };
        }

        int restored = RebuildRestoredMarkers();
        ShowFeedback(restored > 0 ? $"基準点OK / 前回の選木{restored}本を復元" : "基準点をセットしました");
        return true;
    }

    /// <summary>基準点由来のマップ→セッション変換 (回転はyawのみ)</summary>
    MsMapTransform AnchorTransform() => new MsMapTransform
    {
        Theta = -_anchorYawRad,
        Tx = _anchorPos.x,
        Tz = _anchorPos.z,
        Ty = _anchorPos.y,
    };

    /// <summary>セッション座標→マップ座標 (基準点を原点、基準方向を+Z、yは基準点からの相対)</summary>
    Vector3 SessionToMap(Vector3 sessionPos)
    {
        var r = sessionPos - _anchorPos;
        float c = Mathf.Cos(_anchorYawRad), s = Mathf.Sin(_anchorYawRad);
        return new Vector3(c * r.x - s * r.z, r.y, s * r.x + c * r.z);
    }

    // ---- 保存と再訪復元 ----

    /// <summary>
    /// 検出済みの全木 (選木フラグ付き) をマップ座標に変換して保存する。
    /// 基準点がないとマップ座標に変換できないため、基準点セット済み+計画IDありのときだけ保存する。
    /// 前回の選木で今回まだ検出できていないものも、消えないよう保存に引き継ぐ。
    /// </summary>
    void SaveTreeMap()
    {
        if (!_anchorSet || string.IsNullOrEmpty(CurrentPlanId)) return;
        // 何も検出・選木していないのに上書きして前回のマップを消さない
        if (_latestTrees.Count == 0 && _selected.Count == 0) return;

        var entries = new List<PlanTreeStore.PlanTree>();
        var savedSelected = new bool[_selected.Count];
        foreach (var tree in _latestTrees)
        {
            int idx = FindSelectedIndexNear(tree.TrunkBase);
            if (idx >= 0) savedSelected[idx] = true;
            entries.Add(new PlanTreeStore.PlanTree
            {
                MapPos = SessionToMap(tree.TrunkBase),
                WidthCm = Mathf.RoundToInt(tree.WidthMeters * 100f),
                Selected = idx >= 0,
            });
        }

        // 検出リストの入れ替わりで消えた選木も落とさない
        for (int i = 0; i < _selected.Count; i++)
        {
            if (savedSelected[i]) continue;
            entries.Add(new PlanTreeStore.PlanTree
            {
                MapPos = SessionToMap(_selected[i].SessionPos),
                WidthCm = _selected[i].WidthCm,
                Selected = true,
            });
        }

        // 前回の選木は上書き保存で消えないよう引き継ぐ (今回の座標系に変換して重複は統合)
        if (_savedTrees != null)
        {
            foreach (var old in _savedTrees)
            {
                if (!old.Selected) continue;
                var mapPos = SessionToMap(_mapToSession.Apply(old.MapPos));
                var near = FindEntryNear(entries, mapPos);
                if (near != null)
                {
                    near.Selected = true; // 今回も検出できた木: 選木フラグだけ引き継ぐ
                }
                else
                {
                    entries.Add(new PlanTreeStore.PlanTree
                    {
                        MapPos = mapPos,
                        WidthCm = old.WidthCm,
                        Selected = true,
                    });
                }
            }
        }

        PlanTreeStore.Save(CurrentPlanId, entries, _anchorGeo);
    }

    /// <summary>保存候補の中から水平0.6m以内のものを探す (前回分との重複統合用)</summary>
    static PlanTreeStore.PlanTree FindEntryNear(List<PlanTreeStore.PlanTree> entries, Vector3 mapPos)
    {
        foreach (var e in entries)
        {
            float dx = e.MapPos.x - mapPos.x, dz = e.MapPos.z - mapPos.z;
            if (dx * dx + dz * dz <= SameTreeRadius * SameTreeRadius) return e;
        }
        return null;
    }

    /// <summary>前回の選木の復元マーカーを作り直す。戻り値=表示した本数</summary>
    int RebuildRestoredMarkers()
    {
        foreach (var marker in _restoredMarkers)
        {
            if (marker.label != null) _labelBasePos.Remove(marker.label);
            if (marker.root != null) Destroy(marker.root);
        }
        _restoredMarkers.Clear();
        if (!_anchorSet || _savedTrees == null || _workRoot == null) return 0;

        foreach (var tree in _savedTrees)
        {
            if (!tree.Selected) continue;
            var marker = CreateMarker(_mapToSession.Apply(tree.MapPos), "伐(前回)", RestoredColor);
            _restoredMarkers.Add((marker.root, marker.label, tree.MapPos));
        }
        return _restoredMarkers.Count;
    }

    /// <summary>
    /// 保存済みマップの幹配置と今回の検出を照合し、成功したら復元マーカーを精密位置へ置き直す。
    /// 双方3本以上のときだけ試す (点数が少なく数十本規模なのでメインスレッドで足りる)。
    /// </summary>
    void TryAlignSavedMap(List<MsTree> trees)
    {
        if (!_anchorSet || _savedTrees == null || _savedTrees.Count < 3 || trees.Count < 3) return;

        var oldXZ = new List<Vector2>(_savedTrees.Count);
        foreach (var t in _savedTrees) oldXZ.Add(new Vector2(t.MapPos.x, t.MapPos.z));
        var newXZ = new List<Vector2>(trees.Count);
        foreach (var t in trees) newXZ.Add(new Vector2(t.X, t.Z));

        var result = TreeMapAligner.Align(oldXZ, newXZ, coarseInit: AnchorTransform());
        if (!result.Success) return; // 失敗時は基準点由来の変換のまま

        var transform2d = result.Transform;
        transform2d.Ty = _anchorPos.y; // Alignは水平のみ推定するので高さは基準点の相対のまま
        _mapToSession = transform2d;
        foreach (var marker in _restoredMarkers)
        {
            if (marker.root != null) marker.root.transform.position = _mapToSession.Apply(marker.mapPos);
        }
        _alignStatus = $"照合OK: {result.InlierCount}本一致 (誤差{result.RmsMeters:F2}m)";
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
    public static Material MakeUnlit(Color color)
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
