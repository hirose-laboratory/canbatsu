using System;
using UnityEngine;

namespace HangUpTree.Core
{
    /// <summary>
    /// 危険域と復元処理のパラメータ。
    ///
    /// ⚠ 数値はすべて「暫定の初期値」です。
    ///    立入禁止区域の半径は、厚生労働省「かかり木の処理の作業に関する安全ガイドライン」
    ///    および労働安全衛生規則の該当条文を原典で確認し、
    ///    現場・事業体の基準に合わせて必ず設定し直してください。
    ///    コード中の既定値を根拠として運用しないこと。
    /// </summary>
    [Serializable]
    public class DangerZoneSettings
    {
        [Header("主落下セクタ")]
        [Tooltip("幹軸方位を中心とした片側の開き角（度）。ねじれ・転がりの不確かさを含む。")]
        [Range(5f, 90f)] public float MainFallHalfAngleDeg = 45f;

        [Tooltip("半径 = 推定樹高 × この係数。")]
        [Range(0.5f, 4f)] public float MainFallRadiusFactor = 2.0f;

        [Header("元口の跳ね返り（キックバック）セクタ")]
        [Tooltip("主落下方位の反対側。片側の開き角（度）。")]
        [Range(0f, 90f)] public float KickbackHalfAngleDeg = 30f;

        [Range(0f, 3f)] public float KickbackRadiusFactor = 1.0f;

        [Header("支持木からの落下物")]
        [Tooltip("支持木を中心とした円。折れ枝・樹冠の落下範囲。")]
        [Range(0f, 2f)] public float SupportDebrisRadiusFactor = 0.5f;

        [Header("最低保証の立入禁止円")]
        [Tooltip("かかり木と支持木のそれぞれを中心とした円。法令・ガイドライン準拠の下限。")]
        public bool UseMinimumExclusion = true;

        [Range(0.5f, 4f)] public float MinimumExclusionRadiusFactor = 2.0f;

        [Header("斜面での非対称性")]
        [Tooltip("斜面では谷側へ伸ばし、山側を縮める。水平地では影響しない。")]
        public bool UseSlopeAsymmetry = true;

        [Tooltip("傾斜1度あたり、谷側の半径を何割増やすか。\n" +
                 "⚠ この係数に確立された出典はありません。滑落・転動の到達距離は\n" +
                 "  樹種・地表状態・積雪で大きく変わります。必ず現場基準で置き換えてください。")]
        [Range(0f, 0.1f)] public float DownhillGainPerDeg = 0.02f;

        [Tooltip("谷側の増加分の上限（1.0 = 半径2倍まで）。")]
        [Range(0f, 3f)] public float MaxDownhillGain = 1.0f;

        [Tooltip("傾斜1度あたり、山側の半径を何割減らすか。\n" +
                 "既定は 0（縮めない）。斜面推定が外れたときに過小警告となるため、\n" +
                 "縮める側は安全側に倒して既定では無効にしてある。")]
        [Range(0f, 0.05f)] public float UphillReductionPerDeg = 0f;

        [Tooltip("山側の減少分の上限（0.5 = 半径半分まで）。")]
        [Range(0f, 0.8f)] public float MaxUphillReduction = 0.5f;

        [Header("樹高の推定")]
        [Tooltip("見えている元口〜接触点の長さに掛けて幹全長を外挿する係数（接触点より先が見えないぶんの補正）。")]
        [Range(1f, 2f)] public float LengthExtrapolationFactor = 1.3f;

        [Tooltip("林分の代表樹高（m）。推定値がこれを下回る場合はこちらを採用（安全側）。")]
        public float StandTreeHeight = 20f;

        [Tooltip("手法C（高さ仮定）で使う接触点の想定高さ（m）。")]
        public float AssumedContactHeight = 12f;

        [Header("復元の許容誤差")]
        [Tooltip("視線と支持木幹軸のねじれ距離がこれを超えたら手法Aを棄却（m）。")]
        public float MaxReprojectionGap = 0.8f;

        [Tooltip("三角測量に必要な最小の視点移動量（m）。これ未満だと基線が短すぎて解が暴れる。")]
        public float MinTriangulationBaseline = 1.0f;

        [Header("退避方向の探索")]
        [Tooltip("退避方向を探すときの方位サンプル数。")]
        [Range(8, 180)] public int EscapeDirectionSamples = 72;

        [Tooltip("退避方向を探すときの前進ステップ（m）。")]
        public float EscapeMarchStep = 0.5f;

        public DangerZoneSettings Clone() => (DangerZoneSettings)MemberwiseClone();
    }
}
