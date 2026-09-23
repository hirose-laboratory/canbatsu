using UnityEngine;

namespace HangUpTree.Core
{
    /// <summary>
    /// 無限直線（原点 + 正規化方向）。幹軸と視線の最近接計算に使う。
    /// </summary>
    public readonly struct Line3
    {
        public readonly Vector3 Origin;
        public readonly Vector3 Direction; // 正規化済み

        public Line3(Vector3 origin, Vector3 direction)
        {
            Origin = origin;
            Direction = direction.normalized;
        }

        public Line3(Ray ray) : this(ray.origin, ray.direction) { }

        public static Line3 FromPoints(Vector3 a, Vector3 b) => new Line3(a, b - a);

        public Vector3 PointAt(float t) => Origin + Direction * t;

        /// <summary>この直線上で world に最も近い点のパラメータ。</summary>
        public float Project(Vector3 world) => Vector3.Dot(world - Origin, Direction);

        /// <summary>
        /// 2直線のねじれの位置における最近接点対を求める。
        /// 平行に近い場合は false（このとき解は不定なので呼び出し側でフォールバックすること）。
        /// </summary>
        /// <param name="sa">a 上のパラメータ</param>
        /// <param name="sb">b 上のパラメータ</param>
        public static bool TryClosestPoints(
            Line3 a, Line3 b,
            out Vector3 pa, out Vector3 pb,
            out float sa, out float sb,
            float parallelEpsilon = 1e-4f)
        {
            // Ericson, Real-Time Collision Detection より。d1,d2 は正規化済みなので a=e=1。
            Vector3 r = a.Origin - b.Origin;
            float bDot = Vector3.Dot(a.Direction, b.Direction);
            float c = Vector3.Dot(a.Direction, r);
            float f = Vector3.Dot(b.Direction, r);

            float denom = 1f - bDot * bDot; // sin^2(なす角)
            if (denom < parallelEpsilon)
            {
                pa = pb = default;
                sa = sb = 0f;
                return false;
            }

            sa = (bDot * f - c) / denom;
            sb = (f - bDot * c) / denom;
            pa = a.PointAt(sa);
            pb = b.PointAt(sb);
            return true;
        }

        public static bool TryClosestPoints(Line3 a, Line3 b, out Vector3 pa, out Vector3 pb)
            => TryClosestPoints(a, b, out pa, out pb, out _, out _);
    }
}
