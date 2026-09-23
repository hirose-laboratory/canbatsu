using System.Collections.Generic;
using UnityEngine;

namespace HangUpTree.Core
{
    public enum DangerRegionKind
    {
        /// <summary>主落下方向。最も危険。</summary>
        MainFall = 0,

        /// <summary>元口の跳ね返り。</summary>
        Kickback = 1,

        /// <summary>支持木からの落下物（折れ枝・樹冠）。</summary>
        SupportDebris = 2,

        /// <summary>法令・ガイドライン準拠の最低保証立入禁止円。</summary>
        MinimumExclusion = 3,
    }

    /// <summary>
    /// 地面平面上の扇形領域。HalfAngleDeg >= 180 なら全円。
    /// </summary>
    public readonly struct DangerSector
    {
        public readonly Vector3 Center;
        public readonly float AzimuthDeg;
        public readonly float HalfAngleDeg;

        /// <summary>基準半径。斜面では方位ごとに増減する。実際の判定には RadiusAt を使うこと。</summary>
        public readonly float Radius;

        public readonly DangerRegionKind Kind;

        /// <summary>谷方向の方位角。斜面非対称化の基準。</summary>
        public readonly float DownhillAzimuthDeg;

        /// <summary>谷側での半径の増分（0.6 なら 1.6 倍）。</summary>
        public readonly float DownhillGain;

        /// <summary>山側での半径の減少分（0.3 なら 0.7 倍）。</summary>
        public readonly float UphillReduction;

        public bool IsFullCircle => HalfAngleDeg >= 180f;

        public DangerSector(Vector3 center, float azimuthDeg, float halfAngleDeg, float radius,
            DangerRegionKind kind,
            float downhillAzimuthDeg = 0f, float downhillGain = 0f, float uphillReduction = 0f)
        {
            Center = center;
            AzimuthDeg = azimuthDeg;
            HalfAngleDeg = halfAngleDeg;
            Radius = Mathf.Max(0f, radius);
            Kind = kind;
            DownhillAzimuthDeg = downhillAzimuthDeg;
            DownhillGain = Mathf.Max(0f, downhillGain);
            UphillReduction = Mathf.Clamp01(uphillReduction);
        }

        public static DangerSector Circle(Vector3 center, float radius, DangerRegionKind kind,
            float downhillAzimuthDeg = 0f, float downhillGain = 0f, float uphillReduction = 0f)
            => new DangerSector(center, 0f, 180f, radius, kind,
                downhillAzimuthDeg, downhillGain, uphillReduction);

        /// <summary>
        /// 指定方位における半径。斜面では谷側へ伸び、山側へ縮む。
        ///
        /// 谷方向となす角の cos で内挿する。真谷で最大、真山で最小、等高線方向で基準値。
        /// かかり木は谷側へ滑落・転動するため、円形モデルでは谷側の到達距離を過小評価する。
        /// </summary>
        public float RadiusAt(float azimuthDeg)
        {
            if (DownhillGain <= 0f && UphillReduction <= 0f) return Radius;

            float delta = Mathf.DeltaAngle(DownhillAzimuthDeg, azimuthDeg);
            float alignment = Mathf.Cos(delta * Mathf.Deg2Rad); // 谷側 +1、山側 -1

            float factor = alignment >= 0f
                ? 1f + DownhillGain * alignment
                : 1f + UphillReduction * alignment; // alignment が負なので縮む

            return Radius * Mathf.Max(0f, factor);
        }

        /// <summary>全方位での最大半径。描画範囲や探索上限の見積もりに使う。</summary>
        public float MaxRadius => Radius * (1f + DownhillGain);

        public bool Contains(in GroundFrame frame, Vector3 world)
        {
            if (Radius <= 0f) return false;

            Vector3 v = Vector3.ProjectOnPlane(world - Center, frame.Up);
            float sqr = v.sqrMagnitude;
            if (sqr > MaxRadius * MaxRadius) return false;
            if (sqr < 1e-6f) return true;

            float az = frame.AzimuthDeg(v);

            if (!IsFullCircle && Mathf.Abs(Mathf.DeltaAngle(AzimuthDeg, az)) > HalfAngleDeg) return false;

            float r = RadiusAt(az);
            return sqr <= r * r;
        }
    }

    /// <summary>
    /// 危険域＝複数扇形の和集合。判定と退避方向探索を持つ。
    /// </summary>
    public sealed class DangerZone
    {
        public GroundFrame Frame { get; }
        public IReadOnlyList<DangerSector> Sectors => _sectors;
        public HangUpSolution Solution { get; }

        private readonly DangerSector[] _sectors;
        private readonly float _maxRadius;

        public DangerZone(GroundFrame frame, DangerSector[] sectors, HangUpSolution solution)
        {
            Frame = frame;
            _sectors = sectors ?? new DangerSector[0];
            Solution = solution;

            float m = 0f;
            foreach (var s in _sectors)
            {
                // 斜面では谷側に伸びるので、基準半径ではなく最大半径で見積もる
                float d = Vector3.ProjectOnPlane(s.Center - frame.Origin, frame.Up).magnitude + s.MaxRadius;
                if (d > m) m = d;
            }
            _maxRadius = m;
        }

        public bool Contains(Vector3 world)
        {
            for (int i = 0; i < _sectors.Length; i++)
                if (_sectors[i].Contains(Frame, world)) return true;
            return false;
        }

        /// <summary>最も危険度の高い（＝列挙順が若い）該当領域。含まれなければ false。</summary>
        public bool TryGetWorstKind(Vector3 world, out DangerRegionKind kind)
        {
            bool hit = false;
            kind = DangerRegionKind.MinimumExclusion;
            for (int i = 0; i < _sectors.Length; i++)
            {
                if (!_sectors[i].Contains(Frame, world)) continue;
                if (!hit || _sectors[i].Kind < kind) kind = _sectors[i].Kind;
                hit = true;
            }
            return hit;
        }

        /// <summary>
        /// world から危険域を最短で抜けられる方向を探す。
        /// 和集合の厳密な最近傍境界は面倒なので、方位を離散サンプルして各方向へ前進しながら判定する近似。
        /// 毎フレーム回さず 5Hz 程度に間引くこと。
        /// </summary>
        public bool TryFindEscape(
            Vector3 world, DangerZoneSettings cfg,
            out Vector3 direction, out float distance)
        {
            direction = Frame.Forward;
            distance = float.PositiveInfinity;

            int samples = Mathf.Max(4, cfg.EscapeDirectionSamples);
            float step = Mathf.Max(0.05f, cfg.EscapeMarchStep);
            float maxDist = _maxRadius * 2f + step;
            bool found = false;

            for (int i = 0; i < samples; i++)
            {
                float az = 360f * i / samples;
                Vector3 dir = Frame.DirectionFromAzimuth(az);

                for (float d = step; d <= maxDist; d += step)
                {
                    if (Contains(world + dir * d)) continue;

                    if (d < distance)
                    {
                        distance = d;
                        direction = dir;
                        found = true;
                    }
                    break;
                }
            }

            if (!found) distance = 0f;
            return found;
        }
    }

    public static class DangerZoneBuilder
    {
        /// <summary>
        /// 解と設定から危険域を組み立てる。
        ///
        /// 注意: 既定値のままだと最低保証円（樹高×2）が主落下セクタ（同じく樹高×2）を
        /// ほぼ飲み込むため、和集合の外形は円になります。セクタは「どこにエネルギーが向くか」を
        /// 伝えるための表示であり、立入判定の下限は円が担う、という役割分担です。
        /// </summary>
        public static DangerZone Build(in HangUpSolution s, DangerZoneSettings cfg, GroundFrame frame)
        {
            if (!s.IsValid) return new DangerZone(frame, new DangerSector[0], s);

            float h = s.EstimatedTreeHeight;
            ComputeSlopeAsymmetry(cfg, frame, out float dhAz, out float gain, out float reduction);

            var list = new List<DangerSector>(5)
            {
                new DangerSector(s.Butt, s.AzimuthDeg, cfg.MainFallHalfAngleDeg,
                    h * cfg.MainFallRadiusFactor, DangerRegionKind.MainFall,
                    dhAz, gain, reduction),
            };

            if (cfg.KickbackRadiusFactor > 0f && cfg.KickbackHalfAngleDeg > 0f)
            {
                list.Add(new DangerSector(s.Butt, Mathf.DeltaAngle(0f, s.AzimuthDeg + 180f),
                    cfg.KickbackHalfAngleDeg, h * cfg.KickbackRadiusFactor, DangerRegionKind.Kickback,
                    dhAz, gain, reduction));
            }

            if (cfg.SupportDebrisRadiusFactor > 0f)
            {
                list.Add(DangerSector.Circle(s.SupportBase,
                    h * cfg.SupportDebrisRadiusFactor, DangerRegionKind.SupportDebris,
                    dhAz, gain, reduction));
            }

            if (cfg.UseMinimumExclusion)
            {
                float r = h * cfg.MinimumExclusionRadiusFactor;
                list.Add(DangerSector.Circle(s.Butt, r, DangerRegionKind.MinimumExclusion,
                    dhAz, gain, reduction));
                list.Add(DangerSector.Circle(s.SupportBase, r, DangerRegionKind.MinimumExclusion,
                    dhAz, gain, reduction));
            }

            return new DangerZone(frame, list.ToArray(), s);
        }

        /// <summary>
        /// 地面の傾斜から、谷方向と半径の増減量を求める。
        ///
        /// 急傾斜地では、かかり木は谷側へ滑落・転動して樹高の2倍を超えて到達しうる一方、
        /// 山側は斜面が受け止めるため到達距離が短くなる。円形モデルはこの非対称性を
        /// 表現できず、谷側を過小評価する。
        /// </summary>
        private static void ComputeSlopeAsymmetry(
            DangerZoneSettings cfg, in GroundFrame frame,
            out float downhillAzimuthDeg, out float gain, out float reduction)
        {
            downhillAzimuthDeg = 0f;
            gain = 0f;
            reduction = 0f;

            if (!cfg.UseSlopeAsymmetry) return;

            float slope = frame.SlopeDeg;
            if (slope < 1f) return; // ほぼ水平なら谷方向が定まらない

            downhillAzimuthDeg = frame.DownhillAzimuthDeg;
            gain = Mathf.Min(cfg.DownhillGainPerDeg * slope, cfg.MaxDownhillGain);
            reduction = Mathf.Min(cfg.UphillReductionPerDeg * slope, cfg.MaxUphillReduction);
        }
    }
}
