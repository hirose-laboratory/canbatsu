using UnityEngine;

namespace HangUpTree.Core
{
    /// <summary>
    /// 地面平面に張り付いた正規直交フレーム。
    /// 方位角・危険域はすべてこのフレーム基準で扱うので、斜面でも一貫して動く。
    /// Forward = 方位 0°、Right = 方位 +90°、Up = 地面法線。
    /// </summary>
    public readonly struct GroundFrame
    {
        public readonly Vector3 Origin;
        public readonly Vector3 Up;
        public readonly Vector3 Forward;
        public readonly Vector3 Right;

        public GroundFrame(Vector3 origin, Vector3 up, Vector3 forward)
        {
            Up = up.normalized;
            Vector3 f = Vector3.ProjectOnPlane(forward, Up);
            if (f.sqrMagnitude < 1e-8f) f = Vector3.ProjectOnPlane(Vector3.forward, Up);
            if (f.sqrMagnitude < 1e-8f) f = Vector3.ProjectOnPlane(Vector3.right, Up);
            Forward = f.normalized;
            Right = Vector3.Cross(Up, Forward);
            Origin = origin;
        }

        /// <summary>
        /// 平面と、その上に置きたい原点のヒントからフレームを作る。
        /// Forward はワールド +Z を地面へ投影した向き（＝水平面ではそのまま Unity の北）。
        /// </summary>
        public static GroundFrame FromPlane(Plane plane, Vector3 originHint)
            => new GroundFrame(plane.ClosestPointOnPlane(originHint), plane.normal, Vector3.forward);

        public static GroundFrame Default => new GroundFrame(Vector3.zero, Vector3.up, Vector3.forward);

        public Plane AsPlane() => new Plane(Up, Origin);

        /// <summary>ワールド方向ベクトルの方位角（度）。Forward が 0、Right が +90。</summary>
        public float AzimuthDeg(Vector3 worldDirection)
        {
            Vector3 v = Vector3.ProjectOnPlane(worldDirection, Up);
            return Mathf.Atan2(Vector3.Dot(v, Right), Vector3.Dot(v, Forward)) * Mathf.Rad2Deg;
        }

        /// <summary>地面の傾斜角（度）。0 = 水平。</summary>
        public float SlopeDeg => Vector3.Angle(Up, Vector3.up);

        /// <summary>
        /// 谷方向（最急降下方向）。重力を地面へ投影して求める。水平面では Vector3.zero。
        /// かかり木は谷側へ滑落・転動するため、危険域の非対称化はこの向きを基準にする。
        /// </summary>
        public Vector3 DownhillDirection
        {
            get
            {
                Vector3 d = Vector3.ProjectOnPlane(Vector3.down, Up);
                return d.sqrMagnitude < 1e-8f ? Vector3.zero : d.normalized;
            }
        }

        /// <summary>谷方向の方位角（度）。水平面では 0 を返すが、その場合は意味を持たない。</summary>
        public float DownhillAzimuthDeg
        {
            get
            {
                Vector3 d = DownhillDirection;
                return d == Vector3.zero ? 0f : AzimuthDeg(d);
            }
        }

        /// <summary>方位角（度）からワールド方向ベクトルへ。</summary>
        public Vector3 DirectionFromAzimuth(float azimuthDeg)
        {
            float r = azimuthDeg * Mathf.Deg2Rad;
            return Forward * Mathf.Cos(r) + Right * Mathf.Sin(r);
        }

        /// <summary>ワールド点を地面平面へ投影したうえでのフレーム内 2D 座標 (右, 前)。</summary>
        public Vector2 ToPlanar(Vector3 world)
        {
            Vector3 v = world - Origin;
            return new Vector2(Vector3.Dot(v, Right), Vector3.Dot(v, Forward));
        }

        /// <summary>フレーム内 2D 座標をワールドへ戻す（地面高さ）。</summary>
        public Vector3 FromPlanar(Vector2 planar)
            => Origin + Right * planar.x + Forward * planar.y;

        /// <summary>地面からの符号付き高さ。</summary>
        public float HeightOf(Vector3 world) => Vector3.Dot(world - Origin, Up);

        /// <summary>可視化オブジェクトをこのフレームに合わせるための回転。</summary>
        public Quaternion Rotation => Quaternion.LookRotation(Forward, Up);
    }
}
