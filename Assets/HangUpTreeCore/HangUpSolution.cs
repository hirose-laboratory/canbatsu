using UnityEngine;

namespace HangUpTree.Core
{
    /// <summary>末口（支持木との接触点）を 3D 復元した手法。</summary>
    public enum TopResolveMethod
    {
        /// <summary>未解決。</summary>
        None = 0,

        /// <summary>手法A: 3D 復元済みの支持木幹軸と、末口への視線の最近接点。1 視点で閉じる。</summary>
        SupportAxis = 1,

        /// <summary>手法B: 2 視点の 6DoF ポーズ差による三角測量。最も高精度。</summary>
        Triangulation = 2,

        /// <summary>手法C: 接触点高さを事前値で仮定。あくまで概算。</summary>
        AssumedHeight = 3,
    }

    /// <summary>
    /// かかり木 1 本分の 3D 解。危険域生成の入力になる。
    /// </summary>
    public readonly struct HangUpSolution
    {
        /// <summary>元口（根元）のワールド座標。地面平面上。</summary>
        public readonly Vector3 Butt;

        /// <summary>末口側の接触点のワールド座標。</summary>
        public readonly Vector3 Top;

        /// <summary>支持木（かかられている木）の根元。</summary>
        public readonly Vector3 SupportBase;

        /// <summary>主落下方位（度、GroundFrame 基準）。</summary>
        public readonly float AzimuthDeg;

        /// <summary>地面法線からの傾斜角（度）。0 = 直立、90 = 水平。</summary>
        public readonly float LeanDeg;

        /// <summary>元口〜接触点の実測長。</summary>
        public readonly float VisibleLength;

        /// <summary>危険域半径の基準となる樹高（幹全長）の推定値。</summary>
        public readonly float EstimatedTreeHeight;

        /// <summary>末口の復元手法。</summary>
        public readonly TopResolveMethod Method;

        /// <summary>0〜1。低いほど表示を「概算」に落として提示すること。</summary>
        public readonly float Confidence;

        public bool IsValid => Method != TopResolveMethod.None;

        public HangUpSolution(
            Vector3 butt, Vector3 top, Vector3 supportBase,
            float azimuthDeg, float leanDeg,
            float visibleLength, float estimatedTreeHeight,
            TopResolveMethod method, float confidence)
        {
            Butt = butt;
            Top = top;
            SupportBase = supportBase;
            AzimuthDeg = azimuthDeg;
            LeanDeg = leanDeg;
            VisibleLength = visibleLength;
            EstimatedTreeHeight = estimatedTreeHeight;
            Method = method;
            Confidence = confidence;
        }

        public static HangUpSolution Invalid => default;

        public override string ToString()
            => $"HangUp[方位 {AzimuthDeg:F0}°, 傾斜 {LeanDeg:F0}°, 樹高 {EstimatedTreeHeight:F1}m, " +
               $"{Method}, 信頼度 {Confidence:P0}]";
    }
}
