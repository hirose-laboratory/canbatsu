using System.Collections.Generic;
using HangUpTree.Core;
using UnityEngine;

/// <summary>
/// かかり木の危険予知 (試作)。3段階で進む:
///  1. 対象マーク: かかり木の根元 (元口) を見て開始 → 根元に印、最寄りの立木を支持木として印
///  2. 全体スキャン: 根元から支持木に引っかかっている先端まで視線でなぞる → なぞった所に印が付いていく。
///     先端で確定すると接触点を3Dで求める
///  3. 危険域表示: 実測した幹 (根元→接触点) から倒れる向き・樹高を出して危険域を描く
///
/// 計算はAR担当の最新版 (HangUpTree 0831) の Core をそのまま使う (Assets/HangUpTreeCore):
///  - 接触点は「先端への視線」と「支持木の鉛直な幹軸」の最近接点 (手法A)。
///    1m以上動いてもう一度先端を確定すると2視点の三角測量 (手法B) に格上げ。どちらも無理なら高さ仮定 (手法C)
///  - 樹高 = 見えている長さ×1.3 (接触点より先は見えないので外挿)。ただし林分の代表樹高 (20m) を下限にする
///    (Coreの安全側の設計。低く見積もって危険域を狭めることはしない)
/// スキャン中の印の3D位置: かかり木は支持木に寄りかかっているので、幹は「根元と支持木を通る鉛直な面」の中にある。
/// 視線とこの面の交点をなぞった位置とする。
/// 描画は実機で実証済みのプリミティブ (Cube/Sphere + Resources/AR/ArUnlit) だけで行う。
/// </summary>
public class KakarigiDisplay
{
    public enum State { Idle, Scanning, Shown }

    /// <summary>危険域のパラメータ (0831版の既定値のまま = すべて暫定値)</summary>
    readonly DangerZoneSettings _settings = new DangerZoneSettings();

    public State Current { get; private set; } = State.Idle;
    public bool Active => Current != State.Idle;
    public DangerZone Zone { get; private set; }

    /// <summary>ビルボードさせるラベル (ArDemoController側で毎フレームカメラに向ける)</summary>
    public IReadOnlyList<Transform> Labels => _labels;

    /// <summary>HUD用の説明 (状態ごとに変わる)</summary>
    public string Summary { get; private set; } = "";

    // ---- 観測 ----
    Vector3 _butt;               // かかり木の根元 (地面上)
    Vector3? _supportBase;       // 支持木の根元 (無ければnull=単独扱い)
    Plane _ground;
    GroundFrame _frame;
    Plane _scanPlane;            // なぞり位置を求める鉛直面 (根元と支持木を通る)
    Ray? _firstTopRay;           // 1回目に確定した先端への視線 (2視点の三角測量用)
    float _scannedLength;        // なぞった最遠点の根元からの距離 [m] (HUD表示用)
    Vector3 _lastDot;
    bool _hasDot;

    // ---- 表示 ----
    GameObject _root;            // この機能の表示すべての親 (解除で一括破棄)
    GameObject _scanRoot;        // スキャン中だけの表示 (印とカーソル)
    GameObject _zoneRoot;        // 危険域の表示 (確定し直すたびに作り直す)
    Transform _cursor;           // 今なぞっている位置のカーソル
    readonly List<Transform> _labels = new List<Transform>();
    Material _dotMaterial;

    const int MaxDots = 80;           // 印の最大数 (描画負荷の上限)
    const float DotSpacing = 0.4f;    // この間隔以上なぞり位置が動いたら印を置く [m]
    const float MaxScanHeight = 35f;  // これより高い交点は空を見ている扱いで無視

    static readonly Color TargetColor = new Color(1f, 0.35f, 0.2f);   // かかり木の印
    static readonly Color SupportColor = new Color(0.3f, 0.75f, 0.95f); // 支持木の印
    static readonly Color ScanColor = new Color(1f, 0.85f, 0.25f);   // なぞった所の印

    // ======================================================================
    // 1. 対象マーク
    // ======================================================================

    /// <summary>
    /// かかり木の根元を見ている視線からスキャンを始める。戻り値=開始できたか (失敗理由はmessage)。
    /// </summary>
    /// <param name="parent">表示の親 (WorkContent。作業終了で一括破棄される)</param>
    /// <param name="gaze">根元を見ている視線 (カメラ位置+向き)</param>
    /// <param name="ground">根元まわりの地面平面</param>
    /// <param name="supportBase">支持木の根元 (最寄りの立木。無ければnull)</param>
    public bool Begin(Transform parent, Ray gaze, Plane ground, Vector3? supportBase, out string message)
    {
        Clear();
        if (!HangUpSolver.TryResolveOnGround(gaze, ground, out Vector3 butt)
            || Vector3.Distance(gaze.origin, butt) > 20f)
        {
            message = "かかり木の根元 (地面との境目) を見てください";
            return false;
        }

        _butt = butt;
        _ground = ground;
        _frame = GroundFrame.FromPlane(ground, butt);
        _supportBase = supportBase;

        // なぞり位置を求める鉛直面: 根元と支持木を通る面 (支持木が無ければ自分に正対する面)
        Vector3 along = supportBase.HasValue ? supportBase.Value - butt : butt - gaze.origin;
        along.y = 0f;
        if (along.sqrMagnitude < 1e-4f) along = Vector3.forward;
        Vector3 normal = Vector3.Cross(along.normalized, Vector3.up);
        _scanPlane = new Plane(normal, butt);

        _root = new GameObject("Kakarigi");
        _root.transform.SetParent(parent, false);
        _scanRoot = new GameObject("KakarigiScan");
        _scanRoot.transform.SetParent(_root.transform, false);
        _dotMaterial = ArDemoController.MakeUnlit(ScanColor);

        // 根元の印 (かかり木) と支持木の印
        AddPillar(butt, TargetColor, 0.5f, "かかり木");
        if (supportBase.HasValue) AddPillar(supportBase.Value, SupportColor, 2.5f, "支持木");

        _cursor = AddSphere(_scanRoot.transform, butt, 0.35f, TargetColor).transform;

        _firstTopRay = null;
        _scannedLength = 0f;
        _hasDot = false;
        Current = State.Scanning;
        Summary = supportBase.HasValue
            ? "根元から引っかかっている先端まで視線でなぞり、先端で「てっぺん」"
            : "根元から先端まで視線でなぞり、先端で「てっぺん」 (支持木が見つからないので概算)";
        message = supportBase.HasValue ? "かかり木をマーク。先端までなぞってください"
                                       : "かかり木をマーク (支持木なし)。先端までなぞってください";
        return true;
    }

    // ======================================================================
    // 2. 全体スキャン (毎フレーム)
    // ======================================================================

    /// <summary>今の視線でなぞり位置を更新する。一定間隔ごとに印を置いていく</summary>
    public void UpdateScan(Ray gaze)
    {
        if (Current != State.Scanning || _cursor == null) return;
        if (!TryScanPoint(gaze, out Vector3 p))
        {
            _cursor.gameObject.SetActive(false); // 木の外 (空・遠く) を見ている
            return;
        }
        _cursor.gameObject.SetActive(true);
        _cursor.position = p;

        if (!_hasDot || (p - _lastDot).sqrMagnitude > DotSpacing * DotSpacing)
        {
            if (_scanRoot.transform.childCount < MaxDots)
            {
                AddSphere(_scanRoot.transform, p, 0.18f, ScanColor, _dotMaterial);
            }
            _lastDot = p;
            _hasDot = true;
            _scannedLength = Mathf.Max(_scannedLength, Vector3.Distance(_butt, p));
            Summary = $"スキャン中: {_scannedLength:0.0}m まで。先端で「てっぺん」";
        }
    }

    /// <summary>視線と鉛直面の交点 = なぞっている幹上の位置。空や遠すぎる所はfalse</summary>
    bool TryScanPoint(Ray gaze, out Vector3 p)
    {
        p = default;
        if (!_scanPlane.Raycast(gaze, out float t) || t <= 0f || t > 40f) return false;
        p = gaze.GetPoint(t);
        float height = _frame.HeightOf(p);
        if (height < -0.5f || height > MaxScanHeight) return false;
        // 根元から水平に離れすぎた所 (支持木の向こう側など) は幹ではない
        Vector3 flat = p - _butt;
        flat.y = 0f;
        float limit = _supportBase.HasValue
            ? Vector3.Distance(new Vector3(_supportBase.Value.x, 0f, _supportBase.Value.z),
                               new Vector3(_butt.x, 0f, _butt.z)) + 3f
            : 25f;
        return flat.magnitude <= limit;
    }

    // ======================================================================
    // 3. 先端を確定して危険域を表示
    // ======================================================================

    /// <summary>
    /// 今の視線を先端 (支持木との接触点) として確定し、危険域を出す。
    /// 2回目以降の確定で1回目から1m以上離れていれば、2視点の三角測量で精度を上げる。
    /// </summary>
    public bool ConfirmTop(Ray gaze, out string message)
    {
        if (Current == State.Idle)
        {
            message = "先に「かかり」でかかり木をマークしてください";
            return false;
        }

        Vector3 top;
        TopResolveMethod method;
        float confidence;

        // 手法B: 2視点の三角測量 (1回目の確定から十分に動いたとき)
        if (_firstTopRay.HasValue && HangUpSolver.TryResolveTopByTriangulation(
                _firstTopRay.Value, gaze, _settings.MaxReprojectionGap, _settings.MinTriangulationBaseline,
                out top, out float triGap))
        {
            method = TopResolveMethod.Triangulation;
            confidence = 0.9f * Mathf.Clamp01(1f - triGap / _settings.MaxReprojectionGap);
        }
        // 手法A: 支持木の鉛直な幹軸との最近接点
        else if (_supportBase.HasValue && HangUpSolver.TryResolveTopBySupportAxis(
                     gaze, new Line3(_supportBase.Value, Vector3.up), _settings.MaxReprojectionGap,
                     out top, out float axGap))
        {
            method = TopResolveMethod.SupportAxis;
            confidence = 0.75f * Mathf.Clamp01(1f - axGap / _settings.MaxReprojectionGap);
        }
        // 手法C: 接触点の高さを仮定 (支持木が無い/視線が支持木から外れているとき)
        else if (HangUpSolver.TryResolveTopByAssumedHeight(gaze, _ground, _settings.AssumedContactHeight, out top))
        {
            method = TopResolveMethod.AssumedHeight;
            confidence = 0.3f;
        }
        else
        {
            message = "先端を求められませんでした。引っかかっている所を見てもう一度";
            return false;
        }

        if (_frame.HeightOf(top) < 1f)
        {
            message = "先端が低すぎます。引っかかっている所 (上の方) を見てもう一度";
            return false;
        }

        if (!_firstTopRay.HasValue) _firstTopRay = gaze;

        // 接触点の真下 (重力方向) の地面の点。支持木があればその根元と一致する
        Vector3 belowTop = HangUpSolver.TryResolveOnGround(new Ray(top, Vector3.down), _ground, out var hit)
            ? hit : top - Vector3.up * _frame.HeightOf(top);
        Vector3 supportBase = _supportBase ?? belowTop;
        var composed = HangUpSolver.Compose(_butt, top, supportBase, _frame, _ground, _settings,
            method, confidence);
        if (!composed.IsValid)
        {
            message = "幹の向きを決められませんでした。先端を見てもう一度";
            return false;
        }

        // 倒れる向きの補正: Coreは幹の線を斜面に垂直に投影した向きを使うが、木は重力で倒れるので
        // 実際の向きは「幹を含む鉛直な面と斜面の交線」= 根元→接触点の真下の地面、になる。
        // 平地では両者は一致し、斜面では幹の高さのぶん山側にずれる (20°の斜面で約24°) ため補正する
        float fallAzimuth = _frame.AzimuthDeg(belowTop - _butt);
        var solution = new HangUpSolution(
            composed.Butt, composed.Top, composed.SupportBase,
            fallAzimuth, composed.LeanDeg, composed.VisibleLength, composed.EstimatedTreeHeight,
            composed.Method, composed.Confidence);

        Zone = DangerZoneBuilder.Build(solution, _settings, _frame);
        DrawZone(solution);

        // スキャン中の表示はもう不要 (実測した幹の線に置き換わる)
        if (_scanRoot != null) Object.Destroy(_scanRoot);
        _scanRoot = null;
        _cursor = null;
        Current = State.Shown;

        string methodText = method == TopResolveMethod.Triangulation ? "2視点・精度高"
            : method == TopResolveMethod.SupportAxis ? "支持木軸・精度中"
            : "高さ仮定・概算";
        float radius = solution.EstimatedTreeHeight * _settings.MinimumExclusionRadiusFactor;
        bool floored = solution.VisibleLength * _settings.LengthExtrapolationFactor < _settings.StandTreeHeight;
        // 表示する傾きは鉛直からの角度 (見た目の傾き。Coreの LeanDeg は斜面の法線基準なので斜面では大きく出る)
        float leanFromVertical = Vector3.Angle(solution.Top - solution.Butt, Vector3.up);
        Summary = $"幹{solution.VisibleLength:0.0}m・傾き{leanFromVertical:0}°→樹高{solution.EstimatedTreeHeight:0}m" +
                  (floored ? "(下限)" : "") + $"・半径{radius:0}m ({methodText}・暫定値)";
        message = method == TopResolveMethod.Triangulation
            ? "2視点で先端を確定 (精度高)"
            : "危険域を表示。1m以上横に動いて先端をもう一度「てっぺん」で精度アップ";
        return true;
    }

    public void Clear()
    {
        if (_root != null) Object.Destroy(_root);
        _root = null;
        _scanRoot = null;
        _zoneRoot = null;
        _cursor = null;
        _labels.Clear();
        _firstTopRay = null;
        Zone = null;
        Summary = "";
        Current = State.Idle;
    }

    /// <summary>現在位置が危険域内か (危険域を表示していないときはfalse)</summary>
    public bool Contains(Vector3 worldPos) => Current == State.Shown && Zone != null && Zone.Contains(worldPos);

    // ======================================================================
    // 描画
    // ======================================================================

    static readonly Dictionary<DangerRegionKind, (Color color, float lift)> KindStyle =
        new Dictionary<DangerRegionKind, (Color, float)>
        {
            // liftは地面からの浮かし量 (重ね順の代わり。z-fight回避)
            { DangerRegionKind.MinimumExclusion, (new Color(0.95f, 0.15f, 0.1f), 0.02f) }, // 立入禁止円=赤
            { DangerRegionKind.MainFall,         (new Color(1f, 0.55f, 0.1f),   0.05f) },  // 主落下=オレンジ
            { DangerRegionKind.Kickback,         (new Color(1f, 0.85f, 0.2f),   0.05f) },  // 跳ね返り=黄
            { DangerRegionKind.SupportDebris,    (new Color(0.95f, 0.4f, 0.55f), 0.03f) }, // 落下物=ピンク
        };

    void DrawZone(in HangUpSolution s)
    {
        if (_zoneRoot != null) Object.Destroy(_zoneRoot); // 2回目の確定 (精度アップ) で描き直す
        _zoneRoot = new GameObject("KakarigiZone");
        _zoneRoot.transform.SetParent(_root.transform, false);

        foreach (var sector in Zone.Sectors)
        {
            DrawSector(sector);
        }

        // 実測した幹 (根元→接触点) を太い線で示す
        AddSegment(_zoneRoot.transform, s.Butt, s.Top, ArDemoController.MakeUnlit(TargetColor), 0.14f, flat: false);
        AddSphere(_zoneRoot.transform, s.Top, 0.3f, TargetColor);
    }

    void DrawSector(in DangerSector sector)
    {
        if (sector.Radius <= 0.01f) return;
        var (color, lift) = KindStyle.TryGetValue(sector.Kind, out var st) ? st : (Color.red, 0.02f);
        var material = ArDemoController.MakeUnlit(color);

        float from = sector.IsFullCircle ? -180f : sector.AzimuthDeg - sector.HalfAngleDeg;
        float to = sector.IsFullCircle ? 180f : sector.AzimuthDeg + sector.HalfAngleDeg;
        int segments = Mathf.Max(6, Mathf.CeilToInt((to - from) / 7.5f)); // 全周48分割相当

        Vector3 prev = RimPoint(sector, from, lift);
        for (int i = 1; i <= segments; i++)
        {
            Vector3 next = RimPoint(sector, Mathf.Lerp(from, to, (float)i / segments), lift);
            AddSegment(_zoneRoot.transform, prev, next, material, 0.12f);
            prev = next;
        }

        // 扇形は中心から縁への線 (スポーク) で開きを見せる
        if (!sector.IsFullCircle)
        {
            Vector3 center = sector.Center + _frame.Up * lift;
            AddSegment(_zoneRoot.transform, center, RimPoint(sector, from, lift), material, 0.12f);
            AddSegment(_zoneRoot.transform, center, RimPoint(sector, to, lift), material, 0.12f);
        }
    }

    Vector3 RimPoint(in DangerSector sector, float azimuthDeg, float lift)
    {
        // RadiusAtで斜面の谷側の伸びも反映された縁になる
        float r = sector.RadiusAt(azimuthDeg);
        Vector3 onPlane = _frame.AsPlane().ClosestPointOnPlane(sector.Center);
        return onPlane + _frame.DirectionFromAzimuth(azimuthDeg) * r + _frame.Up * lift;
    }

    /// <summary>根元に立てる印 (縦の柱+ラベル)</summary>
    void AddPillar(Vector3 basePos, Color color, float height, string text)
    {
        var material = ArDemoController.MakeUnlit(color);
        AddSegment(_root.transform, basePos, basePos + Vector3.up * height, material, 0.2f, flat: false);

        var go = new GameObject("KakarigiLabel");
        go.transform.SetParent(_root.transform, false);
        go.transform.position = basePos + Vector3.up * (height + 0.4f);
        var label = go.AddComponent<TextMesh>();
        label.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        label.GetComponent<MeshRenderer>().material = label.font.material;
        label.fontSize = 48;
        label.characterSize = 0.022f;
        label.anchor = TextAnchor.LowerCenter;
        label.alignment = TextAlignment.Center;
        label.color = color;
        label.text = text;
        _labels.Add(go.transform);
    }

    /// <param name="flat">true=地面に這う平たい帯 (危険域の縁)、false=角柱 (柱・幹の線)</param>
    static void AddSegment(Transform parent, Vector3 a, Vector3 b, Material material, float thickness,
        bool flat = true)
    {
        var seg = GameObject.CreatePrimitive(PrimitiveType.Cube);
        seg.name = "Seg";
        seg.transform.SetParent(parent, false);
        Object.Destroy(seg.GetComponent<Collider>());
        var dir = b - a;
        seg.transform.position = (a + b) * 0.5f;
        if (dir.sqrMagnitude > 1e-8f) seg.transform.rotation = Quaternion.LookRotation(dir);
        seg.transform.localScale = new Vector3(thickness, flat ? thickness * 0.35f : thickness, dir.magnitude);
        if (material != null) seg.GetComponent<Renderer>().material = material;
    }

    static GameObject AddSphere(Transform parent, Vector3 pos, float size, Color color, Material shared = null)
    {
        var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        s.name = "Dot";
        s.transform.SetParent(parent, false);
        Object.Destroy(s.GetComponent<Collider>());
        s.transform.position = pos;
        s.transform.localScale = Vector3.one * size;
        var material = shared != null ? shared : ArDemoController.MakeUnlit(color);
        if (material != null) s.GetComponent<Renderer>().material = material;
        return s;
    }
}
