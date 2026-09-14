using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 作業計画1件分のデータ。
/// </summary>
public class WorkPlan
{
    /// <summary>状態: 進行中。予定一覧に出て、記録を付けても完了するまで使い回せる</summary>
    public const string StatusActive = "active";

    /// <summary>状態: 完了。予定一覧に出ない (作業終了時に「記録して完了」を選ぶとこうなる)</summary>
    public const string StatusCompleted = "completed";

    public string Id = Guid.NewGuid().ToString();

    /// <summary>計画の状態 (active / completed)</summary>
    public string Status = StatusActive;

    /// <summary>完了済みかどうか</summary>
    public bool IsCompleted => Status == StatusCompleted;

    /// <summary>作業予定日</summary>
    public DateTime Date;

    /// <summary>樹種 (例: スギ、ヒノキ)</summary>
    public string Species;

    /// <summary>間伐率 (%)</summary>
    public int ThinningRatePercent;

    /// <summary>範囲 (ha)</summary>
    public float AreaHa;

    /// <summary>伐採間隔 (m)</summary>
    public float FellingIntervalM;

    /// <summary>伐採基準 (cm) : この直径未満の木を伐る (DBのdiameterThresholdCmと同じ定義)</summary>
    public int FellingStandardCm;

    /// <summary>地図で選択した範囲の頂点 (x=経度, y=緯度)</summary>
    public List<Vector2> RangePoints = new List<Vector2>();
}
