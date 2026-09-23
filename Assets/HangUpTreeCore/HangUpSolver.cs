using UnityEngine;

namespace HangUpTree.Core
{
    /// <summary>
    /// かかり木 1 本分の観測。ピクセル指定でも手動レイでも、ここまで来れば同じ扱い。
    /// レイはすべてワールド座標系。
    /// </summary>
    public struct HangUpObservation
    {
        /// <summary>元口（根元）方向への視線。地面平面との交点で 3D 確定する。</summary>
        public Ray ButtRay;

        /// <summary>末口側の接触点方向への視線。</summary>
        public Ray TopRay;

        /// <summary>支持木の根元方向への視線。</summary>
        public Ray SupportBaseRay;

        /// <summary>支持木の幹の任意の高い点への視線。支持木幹軸の復元に使う（手法A）。</summary>
        public Ray? SupportUpperRay;

        /// <summary>別視点から見た接触点への視線（手法B）。</summary>
        public Ray? SecondViewTopRay;
    }

    /// <summary>
    /// 観測レイ + 地面平面から、かかり木の 3D 幾何を復元する。
    /// Unity のシーンにも NRSDK にも依存しないので、そのままテストできる。
    /// </summary>
    public static class HangUpSolver
    {
        /// <summary>
        /// 接地拘束で 3D 位置を確定する。単眼のスケール不定性を潰す要。
        /// </summary>
        public static bool TryResolveOnGround(Ray ray, Plane ground, out Vector3 world)
        {
            if (ground.Raycast(ray, out float t) && t > 0f)
            {
                world = ray.GetPoint(t);
                return true;
            }
            world = default;
            return false;
        }

        /// <summary>
        /// 手法A: 支持木の幹軸と、末口への視線との最近接点を接触点とする。
        /// ねじれ距離が maxGap を超えたら棄却。
        /// </summary>
        public static bool TryResolveTopBySupportAxis(
            Ray topRay, Line3 supportAxis, float maxGap,
            out Vector3 top, out float gap)
        {
            top = default;
            gap = float.PositiveInfinity;

            if (!Line3.TryClosestPoints(new Line3(topRay), supportAxis,
                    out Vector3 pa, out Vector3 pb, out float sa, out _))
                return false;

            if (sa <= 0f) return false; // カメラ後方の解は棄却

            gap = Vector3.Distance(pa, pb);
            top = (pa + pb) * 0.5f;
            return gap <= maxGap;
        }

        /// <summary>
        /// 手法B: 2 視点の視線の最近接点。基線長が足りない場合は棄却。
        /// </summary>
        public static bool TryResolveTopByTriangulation(
            Ray viewA, Ray viewB, float maxGap, float minBaseline,
            out Vector3 top, out float gap)
        {
            top = default;
            gap = float.PositiveInfinity;

            if (Vector3.Distance(viewA.origin, viewB.origin) < minBaseline) return false;

            if (!Line3.TryClosestPoints(new Line3(viewA), new Line3(viewB),
                    out Vector3 pa, out Vector3 pb, out float sa, out float sb))
                return false;

            if (sa <= 0f || sb <= 0f) return false;

            gap = Vector3.Distance(pa, pb);
            top = (pa + pb) * 0.5f;
            return gap <= maxGap;
        }

        /// <summary>
        /// 手法C: 地面平面を法線方向に height だけ持ち上げた面と視線の交点。最終フォールバック。
        /// </summary>
        public static bool TryResolveTopByAssumedHeight(
            Ray topRay, Plane ground, float height, out Vector3 top)
        {
            // 平面 dot(n,p) + d = 0 を法線方向に +h 平行移動すると d' = d - h。
            var lifted = new Plane(ground.normal, ground.distance - height);
            return TryResolveOnGround(topRay, lifted, out top);
        }

        /// <summary>
        /// 支持木の根元へのレイと接地拘束から、支持木の幹軸を復元する。
        ///
        /// 幹の向きは trunkUp で与える。ここには<b>地面法線ではなく重力方向（Vector3.up）</b>を
        /// 渡すこと。立木は斜面でも屈地性により鉛直に育つため。
        /// 地面法線を渡すと、傾斜 20°・高さ 9m で幹軸が横に約 3.1m ずれ、
        /// 接触点の復元が許容誤差を超えて必ず破綻する。
        /// </summary>
        /// <param name="trunkUp">幹の伸びる向き。通常は Vector3.up（重力の逆向き）。</param>
        public static bool TryResolveSupportAxis(
            Ray supportBaseRay, Plane ground, Vector3 trunkUp,
            out Line3 axis, out Vector3 basePoint)
        {
            axis = default;
            if (!TryResolveOnGround(supportBaseRay, ground, out basePoint)) return false;
            axis = new Line3(basePoint, trunkUp);
            return true;
        }

        /// <summary>
        /// 観測一式から解を組み立てる。手法 A → B → C の順に試す。
        /// </summary>
        public static HangUpSolution Solve(
            in HangUpObservation obs, Plane ground, GroundFrame frame, DangerZoneSettings cfg)
        {
            if (!TryResolveOnGround(obs.ButtRay, ground, out Vector3 butt))
                return HangUpSolution.Invalid;

            // 支持木は立木なので鉛直と仮定する。地面法線ではないので注意。
            if (!TryResolveSupportAxis(obs.SupportBaseRay, ground, Vector3.up,
                    out Line3 supportAxis, out Vector3 supportBase))
                return HangUpSolution.Invalid;

            Vector3 top;
            TopResolveMethod method;
            float confidence;

            if (obs.SecondViewTopRay.HasValue &&
                TryResolveTopByTriangulation(obs.TopRay, obs.SecondViewTopRay.Value,
                    cfg.MaxReprojectionGap, cfg.MinTriangulationBaseline, out top, out float triGap))
            {
                method = TopResolveMethod.Triangulation;
                confidence = 0.90f * GapQuality(triGap, cfg.MaxReprojectionGap);
            }
            else if (TryResolveTopBySupportAxis(obs.TopRay, supportAxis,
                         cfg.MaxReprojectionGap, out top, out float axGap))
            {
                method = TopResolveMethod.SupportAxis;
                confidence = 0.75f * GapQuality(axGap, cfg.MaxReprojectionGap);
            }
            else if (TryResolveTopByAssumedHeight(obs.TopRay, ground, cfg.AssumedContactHeight, out top))
            {
                method = TopResolveMethod.AssumedHeight;
                confidence = 0.30f;
            }
            else
            {
                return HangUpSolution.Invalid;
            }

            return Compose(butt, top, supportBase, frame, ground, cfg, method, confidence);
        }

        /// <summary>復元済みの 3 点から解を組み立てる。手動プロトタイプからも使う。</summary>
        public static HangUpSolution Compose(
            Vector3 butt, Vector3 top, Vector3 supportBase,
            GroundFrame frame, Plane ground, DangerZoneSettings cfg,
            TopResolveMethod method, float confidence)
        {
            Vector3 axis = top - butt;
            if (axis.sqrMagnitude < 1e-6f) return HangUpSolution.Invalid;

            float azimuth = frame.AzimuthDeg(axis);
            float lean = Vector3.Angle(axis, ground.normal);
            float visible = axis.magnitude;

            // 接触点より先の幹は見えないので外挿し、林分の代表樹高を下限として安全側に寄せる。
            float height = Mathf.Max(visible * cfg.LengthExtrapolationFactor, cfg.StandTreeHeight);

            return new HangUpSolution(
                butt, top, supportBase,
                azimuth, lean, visible, height,
                method, Mathf.Clamp01(confidence));
        }

        private static float GapQuality(float gap, float maxGap)
            => maxGap <= 0f ? 1f : Mathf.Clamp01(1f - gap / maxGap);
    }
}
