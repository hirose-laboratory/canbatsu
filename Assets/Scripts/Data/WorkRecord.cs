using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 作業記録1件分のデータ (PDFの「4.作業記録」に対応)。
/// 本来はARグラス側が自動記録したものを受け取る。
/// </summary>
public class WorkRecord
{
    public string Id = Guid.NewGuid().ToString();

    /// <summary>対応する作業計画のID (Firestore移行後はwork_plansのドキュメントID)。計画vs実績の比較用</summary>
    public string PlanId;

    /// <summary>作業した日</summary>
    public DateTime Date;

    /// <summary>作業した面積 (ha)</summary>
    public float AreaHa;

    /// <summary>実施した間伐率 (%)</summary>
    public int ThinningRatePercent;

    /// <summary>作業した範囲の頂点 (x=経度, y=緯度)</summary>
    public List<Vector2> RangePoints = new List<Vector2>();
}
