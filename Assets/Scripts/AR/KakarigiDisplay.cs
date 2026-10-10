using System.Collections.Generic;
using HangUpTree.Core;
using UnityEngine;

/// <summary>
/// かかり木の危険予知 (試作)。「かかり木」でモードに入り、照準を合わせて3点を順に決定する:
///  0. 「かかり木」 → かかり木モードに入る (この時点では何も置かない。照準が出る)
///  1. かかり木の根元 (元口) に照準を合わせてもう一度「かかり木」 → 根元に印
///  2. 支持木 (寄りかかっている相手の木) の根元に照準を合わせて「支持木」 → 根元に印 (近くに検出木があれば呼び出し側で吸着)
///  3. かかり木が支持木に触れている所に照準を合わせて「接点」 → 接点を3Dで求めて危険域を描く
///  根元の決定後に「かかり木」と言えば最初からやり直し。「かいじょ」でモードを抜ける
///
/// 計算はAR担当の最新版 (HangUpTree 0831) の Core をそのまま使う (Assets/HangUpTreeCore):
///  - 接点は「接点への視線」と「支持木の鉛直な幹軸」の最近接点 (手法A)。
///    1m以上動いてもう一度「接点」すると2視点の三角測量 (手法B) に格上げ。どちらも無理なら高さ仮定 (手法C)
///  - 樹高 = 見えている長さ×1.3 (接点より先は見えないので外挿)。ただし林分の代表樹高 (20m) を下限にする
///    (Coreの安全側の設計。低く見積もって危険域を狭めることはしない)
/// 描画は実機で実証済みのプリミティブ (Cube/Sphere + Resources/AR/ArUnlit) だけで行う。
/// </summary>
public class KakarigiDisplay
{
    /// <summary>
    /// Idle=モード外 / AimTarget=根元を狙っている / AimSupport=支持木の根元を狙っている /
    /// AimContact=接点を狙っている / Shown=危険域を表示中
    /// </summary>
    public enum State { Idle, AimTarget, AimSupport, AimContact, Shown }

    /// <summary>照準を出す段階か (3点のどれかを狙っている間)</summary>
    public bool IsAiming => Current == State.AimTarget || Current == State.AimSupport || Current == State.AimContact;

    /// <summary>危険域のパラメータ (0831版の既定値のまま = すべて暫定値)</summary>
    readonly DangerZoneSettings _settings = new DangerZoneSettings();

    public State Current { get; private set; } = State.Idle;
    public bool Active => Current != State.Idle;
    public DangerZone Zone { get; private set; }

    /// <summary>ビルボードさせるラベル (ArDemoController側で毎フレームカメラに向ける)</summary>
    public IReadOnlyList<Transform> Labels => _labels;

    /// <summary>HUD用: 手順中は次の操作の案内、表示中は結果の要約</summary>
    public string Summary { get; private set; } = "";

    public const string NavTarget = "かかり木の根元 (地面との境目) に照準を合わせて、もう一度「かかり木」";
    public const string NavSupport = "支持木 (寄りかかっている相手の木) の根元に照準を合わせて「支持木」";
    public const string NavContact = "かかり木が支持木に触れている所に照準を合わせて「接点」";

    // ---- 観測 ----
    Vector3 _butt;               // かかり木の根元 (地面上)
    Vector3 _supportBase;        // 支持木の根元 (地面上)
    Plane _ground;
    GroundFrame _frame;
    Ray? _firstContactRay;       // 1回目に確定した接点への視線 (2視点の三角測量用)

    // ---- 表示 ----
    GameObject _root;            // この機能の表示すべての親 (解除で一括破棄)
    GameObject _targetRoot;      // かかり木の根元の印
    GameObject _supportRoot;     // 支持木の印 (置き直しで作り直す)
    GameObject _zoneRoot;        // 接点と危険域 (確定し直すたびに作り直す)
    readonly List<Transform> _labels = new List<Transform>();

    const float MaxMarkDistance = 20f;     // これより遠い根元は誤操作扱い
    const float MinSupportDistance = 0.5f; // かかり木と支持木の根元がこれより近ければ同じ木
    const float MaxSupportDistance = 15f;

    static readonly Color TargetColor = new Color(1f, 0.35f, 0.2f);    // かかり木
    static readonly Color SupportColor = new Color(0.3f, 0.75f, 0.95f); // 支持木
    static readonly Color ContactColor = new Color(1f, 0.85f, 0.25f);   // 接点

    // ======================================================================
    // 0. モードに入る
    // ======================================================================

    /// <summary>
    /// かかり木モードに入る (何も置かず、根元を狙う段階にする)。すでにモード中なら最初からやり直す。
    /// </summary>
    /// <param name="parent">表示の親 (WorkContent。作業終了で一括破棄される)</param>
    public void Begin(Transform parent)
    {
        Clear();
        _root = new GameObject("Kakarigi");
        _root.transform.SetParent(parent, false);
        Current = State.AimTarget;
        Summary = NavTarget;
    }

    // ======================================================================
    // 1. かかり木の根元
    // ======================================================================

    /// <summary>
    /// 照準 (視線) の先をかかり木の根元として決定する。戻り値=決定できたか (結果の文言はmessage)。
    /// </summary>
    /// <param name="gaze">根元を見ている視線 (カメラ位置+向き)</param>
    /// <param name="ground">根元まわりの地面平面</param>
    public bool MarkTarget(Ray gaze, Plane ground, out string message)
    {
        if (Current != State.AimTarget)
        {
            message = "先に「かかり木」でかかり木モードに入ってください";
            return false;
        }
        if (!HangUpSolver.TryResolveOnGround(gaze, ground, out Vector3 butt)
            || Vector3.Distance(gaze.origin, butt) > MaxMarkDistance)
        {
            message = "かかり木の根元 (地面との境目) に照準を合わせてください";
            return false;
        }

        _butt = butt;
        _ground = ground;
        _frame = GroundFrame.FromPlane(ground, butt);

        _targetRoot = new GameObject("KakarigiTarget");
        _targetRoot.transform.SetParent(_root.transform, false);
        AddPillar(_targetRoot.transform, butt, TargetColor, 0.5f, "かかり木");

        Current = State.AimSupport;
        Summary = NavSupport;
        message = "かかり木の根元を決定しました";
        return true;
    }

    /// <summary>視線と地面 (かかり木の根元まわりの平面) の交点。支持木の根元候補を求めるのに使う</summary>
    public bool TryGroundPoint(Ray gaze, out Vector3 point)
    {
        point = default;
        return Current != State.Idle
            && HangUpSolver.TryResolveOnGround(gaze, _ground, out point)
            && Vector3.Distance(gaze.origin, point) <= MaxMarkDistance;
    }

    // ======================================================================
    // 2. 支持木
    // ======================================================================

    /// <summary>支持木の根元を決定する。接点の決定後や表示中に呼べば置き直し (接点と危険域は消える)</summary>
    public bool MarkSupport(Vector3 supportBase, out string message)
    {
        if (Current == State.Idle || Current == State.AimTarget)
        {
            message = Current == State.Idle
                ? "先に「かかり木」でかかり木モードに入ってください"
                : "先にかかり木の根元に照準を合わせて「かかり木」で決定してください";
            return false;
        }
        Vector3 flat = supportBase - _butt;
        flat.y = 0f;
        float d = flat.magnitude;
        if (d < MinSupportDistance)
        {
            message = "かかり木と同じ所です。寄りかかっている相手の木の根元に照準を合わせてください";
            return false;
        }
        if (d > MaxSupportDistance)
        {
            message = $"支持木が遠すぎます ({d:0}m)。寄りかかっている相手の木の根元に照準を合わせてください";
            return false;
        }

        // 置き直し: 前の支持木と、それに基づく接点・危険域は消す
        DestroyGroup(ref _zoneRoot);
        DestroyGroup(ref _supportRoot);
        Zone = null;
        _firstContactRay = null;

        _supportBase = supportBase;
        _supportRoot = new GameObject("KakarigiSupport");
        _supportRoot.transform.SetParent(_root.transform, false);
        AddPillar(_supportRoot.transform, supportBase, SupportColor, 2.5f, "支持木");

        Current = State.AimContact;
        Summary = NavContact;
        message = $"支持木の根元を決定しました (かかり木から{d:0.0}m)";
        return true;
    }

    // ======================================================================
    // 3. 接点 → 危険域
    // ======================================================================

    /// <summary>
    /// 今の視線を接点 (かかり木が支持木に触れている所) として確定し、危険域を出す。
    /// 2回目以降の確定で1回目から1m以上離れていれば、2視点の三角測量で精度を上げる。
    /// </summary>
    public bool MarkContact(Ray gaze, out string message)
    {
        if (Current != State.AimContact && Current != State.Shown)
        {
            message = Current == State.Idle ? "先に「かかり木」でかかり木モードに入ってください"
                : Current == State.AimTarget ? "先にかかり木の根元に照準を合わせて「かかり木」で決定してください"
                : "先に支持木の根元に照準を合わせて「支持木」で決定してください";
            return false;
        }

        Vector3 top;
        TopResolveMethod method;
        float confidence;

        // 手法B: 2視点の三角測量 (1回目の確定から十分に動いたとき)
        if (_firstContactRay.HasValue && HangUpSolver.TryResolveTopByTriangulation(
                _firstContactRay.Value, gaze, _settings.MaxReprojectionGap, _settings.MinTriangulationBaseline,
                out top, out float triGap))
        {
            method = TopResolveMethod.Triangulation;
            confidence = 0.9f * Mathf.Clamp01(1f - triGap / _settings.MaxReprojectionGap);
        }
        // 手法A: 支持木の鉛直な幹軸との最近接点
        else if (HangUpSolver.TryResolveTopBySupportAxis(
                     gaze, new Line3(_supportBase, Vector3.up), _settings.MaxReprojectionGap,
                     out top, out float axGap))
        {
            method = TopResolveMethod.SupportAxis;
            confidence = 0.75f * Mathf.Clamp01(1f - axGap / _settings.MaxReprojectionGap);
        }
        // 手法C: 接点の高さを仮定 (視線が支持木の幹から大きく外れているとき)
        else if (HangUpSolver.TryResolveTopByAssumedHeight(gaze, _ground, _settings.AssumedContactHeight, out top))
        {
            method = TopResolveMethod.AssumedHeight;
            confidence = 0.3f;
        }
        else
        {
            message = "接点を求められませんでした。触れている所を見てもう一度";
            return false;
        }

        if (_frame.HeightOf(top) < 1f)
        {
            message = "接点が低すぎます。触れている所 (上の方) を見てもう一度";
            return false;
        }

        if (!_firstContactRay.HasValue) _firstContactRay = gaze;

        var composed = HangUpSolver.Compose(_butt, top, _supportBase, _frame, _ground, _settings,
            method, confidence);
        if (!composed.IsValid)
        {
            message = "幹の向きを決められませんでした。接点を見てもう一度";
            return false;
        }

        // 倒れる向きの補正: Coreは幹の線を斜面に垂直に投影した向きを使うが、木は重力で倒れるので
        // 実際の向きは「幹を含む鉛直な面と斜面の交線」= 根元→接点の真下の地面、になる。
        // 平地では両者は一致し、斜面では幹の高さのぶん山側にずれる (20°の斜面で約24°) ため補正する
        Vector3 belowTop = HangUpSolver.TryResolveOnGround(new Ray(top, Vector3.down), _ground, out var hit)
            ? hit : top - Vector3.up * _frame.HeightOf(top);
        float fallAzimuth = _frame.AzimuthDeg(belowTop - _butt);
        var solution = new HangUpSolution(
            composed.Butt, composed.Top, composed.SupportBase,
            fallAzimuth, composed.LeanDeg, composed.VisibleLength, composed.EstimatedTreeHeight,
            composed.Method, composed.Confidence);

        Zone = DangerZoneBuilder.Build(solution, _settings, _frame);
        DrawZone(solution);
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
            ? "2視点で接点を確定 (精度高)"
            : "危険域を表示。1m以上横に動いてもう一度「接点」で精度アップ";
        return true;
    }

    public void Clear()
    {
        if (_root != null) Object.Destroy(_root);
        _root = null;
        _targetRoot = null;
        _supportRoot = null;
        _zoneRoot = null;
        _labels.Clear();
        _firstContactRay = null;
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
        DestroyGroup(ref _zoneRoot); // 2回目の確定 (精度アップ) で描き直す
        _zoneRoot = new GameObject("KakarigiZone");
        _zoneRoot.transform.SetParent(_root.transform, false);

        foreach (var sector in Zone.Sectors)
        {
            DrawSector(sector);
        }

        // 実測した幹 (根元→接点) を太い線で示し、接点に印とラベル
        AddSegment(_zoneRoot.transform, s.Butt, s.Top, ArDemoController.MakeUnlit(TargetColor), 0.14f, flat: false);
        AddSphere(_zoneRoot.transform, s.Top, 0.3f, ContactColor);
        AddLabel(_zoneRoot.transform, s.Top + Vector3.up * 0.4f, ContactColor, "接点");
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
    void AddPillar(Transform parent, Vector3 basePos, Color color, float height, string text)
    {
        AddSegment(parent, basePos, basePos + Vector3.up * height, ArDemoController.MakeUnlit(color), 0.2f,
            flat: false);
        AddLabel(parent, basePos + Vector3.up * (height + 0.4f), color, text);
    }

    void AddLabel(Transform parent, Vector3 pos, Color color, string text)
    {
        var go = new GameObject("KakarigiLabel");
        go.transform.SetParent(parent, false);
        go.transform.position = pos;
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

    /// <summary>表示のまとまりを消す。その下のラベルはビルボード対象からも外す</summary>
    void DestroyGroup(ref GameObject group)
    {
        if (group == null) return;
        var root = group.transform;
        _labels.RemoveAll(t => t == null || t.IsChildOf(root));
        Object.Destroy(group);
        group = null;
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

    static GameObject AddSphere(Transform parent, Vector3 pos, float size, Color color)
    {
        var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        s.name = "Dot";
        s.transform.SetParent(parent, false);
        Object.Destroy(s.GetComponent<Collider>());
        s.transform.position = pos;
        s.transform.localScale = Vector3.one * size;
        var material = ArDemoController.MakeUnlit(color);
        if (material != null) s.GetComponent<Renderer>().material = material;
        return s;
    }
}
