using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Firebase.Firestore;
using UnityEngine;

/// <summary>
/// 作業記録のストア (Firestoreの work_records コレクションと同期)。
/// 作業中ページで「作業を終了」すると計画から記録が作られてここに入る。
/// 読み書きの方針は PlanStore と同じ (読み=メモリキャッシュ、書き=awaitせずFirestoreへ)。
/// </summary>
public static class RecordStore
{
    const string Collection = "work_records";

    static readonly List<WorkRecord> _records = new List<WorkRecord>();

    /// <summary>全記録 (読み取り専用)</summary>
    public static IReadOnlyList<WorkRecord> Records => _records;

    /// <summary>ログイン後に一度呼んでFirestoreから全記録を読み込む (圏外ならオフラインキャッシュから)</summary>
    public static async Task LoadAsync()
    {
        _records.Clear();
        try
        {
            var snap = await FirebaseService.Db.Collection(Collection).GetSnapshotAsync();
            foreach (var doc in snap.Documents)
            {
                _records.Add(FromSnapshot(doc));
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"作業記録の読み込みに失敗しました: {e.Message}");
        }
    }

    public static void Add(WorkRecord record)
    {
        var doc = FirebaseService.Db.Collection(Collection).Document();
        record.Id = doc.Id;
        _records.Add(record);

        var data = new Dictionary<string, object>
        {
            { "workDate", Timestamp.FromDateTime(record.Date.Date.ToUniversalTime()) },
            { "actualThinningRate", record.ThinningRatePercent / 100.0 }, // 0.3 = 30%
            { "areaHa", (double)record.AreaHa },
            { "areaPolygon", PlanStore.ToGeoPoints(record.RangePoints) },
            { "createdBy", FirebaseService.Uid },
            { "createdAt", FieldValue.ServerTimestamp },
        };
        if (!string.IsNullOrEmpty(record.PlanId))
        {
            data["planId"] = record.PlanId; // 計画との紐づけ (計画vs実績の比較用)
        }
        doc.SetAsync(data); // 圏外対策: awaitしない
    }

    public static void Remove(WorkRecord record)
    {
        _records.Remove(record);
        FirebaseService.Db.Collection(Collection).Document(record.Id).DeleteAsync();
    }

    /// <summary>指定した計画に紐づく記録を日付順に返す (継続作業の回数表示用)</summary>
    public static List<WorkRecord> GetByPlan(string planId)
    {
        if (string.IsNullOrEmpty(planId)) return new List<WorkRecord>();
        return _records
            .Where(r => r.PlanId == planId)
            .OrderBy(r => r.Date)
            .ToList();
    }

    /// <summary>指定した計画に作業記録が付いているか (=作業済みか)。記録ページの表示判定に使う</summary>
    public static bool HasRecordForPlan(string planId)
    {
        if (string.IsNullOrEmpty(planId)) return false;
        foreach (var r in _records)
        {
            if (r.PlanId == planId) return true;
        }
        return false;
    }

    static WorkRecord FromSnapshot(DocumentSnapshot d)
    {
        var r = new WorkRecord { Id = d.Id };
        if (d.TryGetValue("planId", out string planId)) r.PlanId = planId;
        if (d.TryGetValue("workDate", out Timestamp ts)) r.Date = ts.ToDateTime().ToLocalTime().Date;
        if (d.TryGetValue("actualThinningRate", out double rate)) r.ThinningRatePercent = (int)Math.Round(rate * 100);
        if (d.TryGetValue("areaHa", out double area)) r.AreaHa = (float)area;
        if (d.TryGetValue("areaPolygon", out List<GeoPoint> polygon)) r.RangePoints = PlanStore.FromGeoPoints(polygon);
        return r;
    }
}
