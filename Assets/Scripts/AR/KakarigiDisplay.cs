using System.Collections.Generic;
using CanbatsuMS;
using HangUpTree.Core;
using UnityEngine;

/// <summary>
/// かかり木の危険域AR表示 (試作)。
/// 計算はAR担当の最新版 (HangUpTree 0831) の Core (Assets/HangUpTreeCore、SDK非依存の純幾何) を使い、
/// 描画は実機で実証済みのプリミティブ (Cube+Resources/AR/ArUnlit) だけで行う
/// (0831版のシェーダー/LineRendererは実機地雷があるため持ち込まない)。
///
/// スキャン起動の割り切り (手動3点指定の代わり):
///  - かかり木 = 視界中央の検出木、支持木 = その最寄りの検出木 (6m以内。無ければ同一点扱い)
///  - 主落下方位 = 支持木の方向 > 谷方向 (斜面) > カメラから見た奥方向 の順で決める
///  - 樹高は実測できないため設定の代表樹高で概算 (手法C相当。半径は暫定値=ガイドライン原典で要確認)
/// </summary>
public class KakarigiDisplay
{
    /// <summary>危険域のパラメータ (0831版の既定値のまま = すべて暫定値)</summary>
    readonly DangerZoneSettings _settings = new DangerZoneSettings();

    GameObject _root;
    Transform _label;

    public DangerZone Zone { get; private set; }
    public bool Active => _root != null;

    /// <summary>ラベルのTransform (ArDemoController側で毎フレームビルボードさせる)。無ければnull</summary>
    public Transform LabelTransform => _label;

    /// <summary>危険域の説明 (HUD用)。例: 半径40m・暫定値</summary>
    public string Summary { get; private set; } = "";

    /// <summary>
    /// 視界中央の木をかかり木として危険域を組み立てて表示する。
    /// </summary>
    /// <param name="parent">表示の親 (WorkContent。作業終了で一括破棄される)</param>
    /// <param name="target">かかり木 (視界中央の検出木)</param>
    /// <param name="support">支持木 (最寄りの検出木。無ければnull)</param>
    /// <param name="camPos">カメラ位置 (方位のフォールバックに使う)</param>
    /// <param name="groundPlane">周辺の地面平面 (点群から推定。斜面なら傾いた平面)</param>
    public void Show(Transform parent, MsTree target, MsTree support, Vector3 camPos, Plane groundPlane)
    {
        Clear();
        _root = new GameObject("KakarigiZone");
        _root.transform.SetParent(parent, false);

        Vector3 butt = target.TrunkBase;
        Vector3 supportBase = support != null ? support.TrunkBase : butt;
        var frame = GroundFrame.FromPlane(groundPlane, butt);

        // 主落下方位: 支持木の方向 > 谷方向 (斜面) > カメラから見た奥方向
        Vector3 fallDir;
        if (support != null && (supportBase - butt).sqrMagnitude > 0.04f)
        {
            fallDir = supportBase - butt;
        }
        else if (frame.SlopeDeg >= 3f)
        {
            fallDir = frame.DownhillDirection;
        }
        else
        {
            fallDir = butt - camPos;
        }
        float azimuthDeg = frame.AzimuthDeg(fallDir);

        // 樹高は実測できないので代表樹高で概算 (手法C相当。信頼度は低め=概算表示)
        float height = _settings.StandTreeHeight;
        var solution = new HangUpSolution(
            butt: butt,
            top: butt + frame.DirectionFromAzimuth(azimuthDeg) * (height * 0.3f)
                 + frame.Up * _settings.AssumedContactHeight,
            supportBase: supportBase,
            azimuthDeg: azimuthDeg,
            leanDeg: 45f,
            visibleLength: 0f,
            estimatedTreeHeight: height,
            method: TopResolveMethod.AssumedHeight,
            confidence: 0.3f);

        Zone = DangerZoneBuilder.Build(solution, _settings, frame);

        foreach (var sector in Zone.Sectors)
        {
            DrawSector(frame, sector);
        }
        BuildLabel(butt, frame.Up);

        float radius = height * _settings.MinimumExclusionRadiusFactor;
        Summary = $"半径{radius:0}m (樹高{height:0}m想定・暫定値)";
    }

    public void Clear()
    {
        if (_root != null) Object.Destroy(_root);
        _root = null;
        _label = null;
        Zone = null;
        Summary = "";
    }

    /// <summary>現在位置が危険域内か (作業終了後などrootが無いときはfalse)</summary>
    public bool Contains(Vector3 worldPos) => Active && Zone != null && Zone.Contains(worldPos);

    // ---- 描画 (Cubeプリミティブの線分でリング/扇形を組む) ----

    static readonly Dictionary<DangerRegionKind, (Color color, float lift)> KindStyle =
        new Dictionary<DangerRegionKind, (Color, float)>
        {
            // liftは地面からの浮かし量 (重ね順の代わり。z-fight回避)
            { DangerRegionKind.MinimumExclusion, (new Color(0.95f, 0.15f, 0.1f), 0.02f) }, // 立入禁止円=赤
            { DangerRegionKind.MainFall,         (new Color(1f, 0.55f, 0.1f),   0.05f) },  // 主落下=オレンジ
            { DangerRegionKind.Kickback,         (new Color(1f, 0.85f, 0.2f),   0.05f) },  // 跳ね返り=黄
            { DangerRegionKind.SupportDebris,    (new Color(0.95f, 0.4f, 0.55f), 0.03f) }, // 落下物=ピンク
        };

    void DrawSector(in GroundFrame frame, in DangerSector sector)
    {
        if (sector.Radius <= 0.01f) return;
        var (color, lift) = KindStyle.TryGetValue(sector.Kind, out var s)
            ? s : (Color.red, 0.02f);
        var material = ArDemoController.MakeUnlit(color);

        float from = sector.IsFullCircle ? -180f : sector.AzimuthDeg - sector.HalfAngleDeg;
        float to = sector.IsFullCircle ? 180f : sector.AzimuthDeg + sector.HalfAngleDeg;
        // 弧の分割数は角度に応じて (全周48分割相当)
        int segments = Mathf.Max(6, Mathf.CeilToInt((to - from) / 7.5f));

        Vector3 prev = RimPoint(frame, sector, from, lift);
        for (int i = 1; i <= segments; i++)
        {
            float az = Mathf.Lerp(from, to, (float)i / segments);
            Vector3 next = RimPoint(frame, sector, az, lift);
            AddSegment(prev, next, material);
            prev = next;
        }

        // 扇形は中心から縁への線 (スポーク) で開きを見せる
        if (!sector.IsFullCircle)
        {
            Vector3 center = sector.Center + frame.Up * lift;
            AddSegment(center, RimPoint(frame, sector, from, lift), material);
            AddSegment(center, RimPoint(frame, sector, to, lift), material);
        }
    }

    static Vector3 RimPoint(in GroundFrame frame, in DangerSector sector, float azimuthDeg, float lift)
    {
        // RadiusAtで斜面の谷側の伸びも反映された縁になる
        float r = sector.RadiusAt(azimuthDeg);
        Vector3 onPlane = frame.AsPlane().ClosestPointOnPlane(sector.Center);
        return onPlane + frame.DirectionFromAzimuth(azimuthDeg) * r + frame.Up * lift;
    }

    void AddSegment(Vector3 a, Vector3 b, Material material)
    {
        var seg = GameObject.CreatePrimitive(PrimitiveType.Cube);
        seg.name = "Seg";
        seg.transform.SetParent(_root.transform, false);
        Object.Destroy(seg.GetComponent<Collider>());
        var dir = b - a;
        seg.transform.position = (a + b) * 0.5f;
        if (dir.sqrMagnitude > 1e-8f) seg.transform.rotation = Quaternion.LookRotation(dir);
        seg.transform.localScale = new Vector3(0.12f, 0.04f, dir.magnitude);
        if (material != null) seg.GetComponent<Renderer>().material = material;
    }

    void BuildLabel(Vector3 butt, Vector3 up)
    {
        var go = new GameObject("KakarigiLabel");
        go.transform.SetParent(_root.transform, false);
        go.transform.position = butt + up * 2.4f;
        var text = go.AddComponent<TextMesh>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.GetComponent<MeshRenderer>().material = text.font.material;
        text.fontSize = 48;
        text.characterSize = 0.024f;
        text.anchor = TextAnchor.LowerCenter;
        text.alignment = TextAlignment.Center;
        text.color = new Color(1f, 0.3f, 0.25f);
        text.text = "⚠ かかり木";
        _label = go.transform;
    }
}
