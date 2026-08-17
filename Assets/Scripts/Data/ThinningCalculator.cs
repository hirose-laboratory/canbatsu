using System;

/// <summary>
/// 森林簿の林齢から間伐の目安値を出す簡易計算。
/// スギ・ヒノキ人工林の一般的な収穫予想表を1本の式に単純化したもので、あくまで初期値。
/// ユーザーがフォームで手動修正できる前提。
/// TODO: バックエンドの機械学習による本算出に置き換える (proconbackend)
/// </summary>
public static class ThinningCalculator
{
    /// <summary>林齢からの立木密度の目安 (本/ha)。例: 10年生≈2800、40年生≈1000、80年生≈620</summary>
    public static double EstimateDensityPerHa(int ageYears)
    {
        int age = Math.Max(1, ageYears);
        return 25000.0 / Math.Pow(age + 3, 0.85);
    }

    /// <summary>林齢からの推奨間伐率 (%)。若い林ほど強めに間引く一般則</summary>
    public static int RecommendRatePercent(int ageYears)
    {
        if (ageYears <= 30) return 30;
        if (ageYears <= 50) return 25;
        return 20;
    }

    /// <summary>
    /// 間伐後に確保したい木の間隔 (m)。
    /// 今の密度から間伐率のぶん間引いた残存密度を均等配置とみなして逆算する:
    /// 間隔 = √(10000 / 残存本数)
    /// </summary>
    public static float SpacingAfterThinningM(int ageYears, int ratePercent)
    {
        double remaining = EstimateDensityPerHa(ageYears) * (1.0 - ratePercent / 100.0);
        if (remaining < 1) remaining = 1;
        double spacing = Math.Sqrt(10000.0 / remaining);
        return (float)Math.Round(spacing, 1);
    }

    /// <summary>
    /// 伐採基準 (この直径cm未満の木を伐る)。
    /// 林齢から平均胸高直径を推定し、その8割 (=平均より細い木が対象) を基準にする
    /// </summary>
    public static int DiameterThresholdCm(int ageYears)
    {
        int age = Math.Max(1, ageYears);
        double meanDiameter = 2.2 * Math.Pow(age, 0.65);
        return Math.Max(6, (int)Math.Round(meanDiameter * 0.8));
    }
}
