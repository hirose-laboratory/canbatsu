using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Firebase.Firestore;
using UnityEngine;

/// <summary>
/// 作業計画のストア (Firestoreの work_plans コレクションと同期)。
/// 読み取りはメモリ上のキャッシュから同期的に行う (各Controllerは今までどおり Plans 等を使えばよい)。
/// 書き込みはメモリを先に更新してからFirestoreへ送る。
/// 圏外対策: 書き込みは await しない (Firestoreのオフラインキャッシュが電波復帰後に自動送信する)。
/// フィールド定義は docs/Firebaseフィールド一覧.md が正 (フィールド名はそこからコピペ)。
/// </summary>
public static class PlanStore
{
    const string Collection = "work_plans";

    static readonly List<WorkPlan> _plans = new List<WorkPlan>();

    /// <summary>全計画 (読み取り専用)</summary>
    public static IReadOnlyList<WorkPlan> Plans => _plans;

    /// <summary>ログイン後に一度呼んでFirestoreから全計画を読み込む (圏外ならオフラインキャッシュから)</summary>
    public static async Task LoadAsync()
    {
        _plans.Clear();
        try
        {
            var snap = await FirebaseService.Db.Collection(Collection).GetSnapshotAsync();
            foreach (var doc in snap.Documents)
            {
                _plans.Add(FromSnapshot(doc));
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"計画の読み込みに失敗しました: {e.Message}");
        }
    }

    public static void Add(WorkPlan plan)
    {
        // ドキュメントIDはFirestoreの自動生成を使う (圏外でもIDは即時発行される)
        var doc = FirebaseService.Db.Collection(Collection).Document();
        plan.Id = doc.Id;
        _plans.Add(plan);

        var data = ToData(plan);
        data["createdBy"] = FirebaseService.Uid;
        data["createdAt"] = FieldValue.ServerTimestamp;
        doc.SetAsync(data); // 圏外対策: awaitしない
    }

    /// <summary>既存計画のフィールドを書き換えたあとに呼ぶと保存される (編集フォーム用)</summary>
    public static void Update(WorkPlan plan)
    {
        // MergeAllで createdAt / createdBy を保ったまま上書きする
        FirebaseService.Db.Collection(Collection).Document(plan.Id)
            .SetAsync(ToData(plan), SetOptions.MergeAll);
    }

    public static void Remove(WorkPlan plan)
    {
        _plans.Remove(plan);
        FirebaseService.Db.Collection(Collection).Document(plan.Id).DeleteAsync();
    }

    /// <summary>指定した日の計画を返す (今日の作業計画セクション用)</summary>
    public static List<WorkPlan> GetByDate(DateTime date)
    {
        return _plans
            .Where(p => p.Date.Date == date.Date)
            .OrderBy(p => p.Date)
            .ToList();
    }

    /// <summary>指定した日より後の計画を日付順に返す (今後の作業計画セクション用)</summary>
    public static List<WorkPlan> GetAfter(DateTime date)
    {
        return _plans
            .Where(p => p.Date.Date > date.Date)
            .OrderBy(p => p.Date)
            .ToList();
    }

    // ---- Firestoreとの変換 (単位と座標順に注意) ----

    static Dictionary<string, object> ToData(WorkPlan p)
    {
        return new Dictionary<string, object>
        {
            { "scheduledDate", Timestamp.FromDateTime(p.Date.Date.ToUniversalTime()) },
            { "species", p.Species ?? "" },
            { "thinningRate", p.ThinningRatePercent / 100.0 },      // 30% は 0.3 で保存する決まり
            { "areaHa", (double)p.AreaHa },
            { "spacingThresholdM", (double)p.FellingIntervalM },
            { "diameterThresholdCm", (double)p.FellingStandardCm }, // この直径未満の木を伐る
            { "areaPolygon", ToGeoPoints(p.RangePoints) },
        };
    }

    static WorkPlan FromSnapshot(DocumentSnapshot d)
    {
        var p = new WorkPlan { Id = d.Id };
        if (d.TryGetValue("scheduledDate", out Timestamp ts)) p.Date = ts.ToDateTime().ToLocalTime().Date;
        if (d.TryGetValue("species", out string species)) p.Species = species;
        if (d.TryGetValue("thinningRate", out double rate)) p.ThinningRatePercent = (int)Math.Round(rate * 100);
        if (d.TryGetValue("areaHa", out double area)) p.AreaHa = (float)area;
        if (d.TryGetValue("spacingThresholdM", out double spacing)) p.FellingIntervalM = (float)spacing;
        if (d.TryGetValue("diameterThresholdCm", out double diameter)) p.FellingStandardCm = (int)Math.Round(diameter);
        if (d.TryGetValue("areaPolygon", out List<GeoPoint> polygon)) p.RangePoints = FromGeoPoints(polygon);
        return p;
    }

    /// <summary>Vector2(x=経度, y=緯度) → GeoPoint(緯度, 経度)。引数の順序が逆なので注意</summary>
    internal static List<object> ToGeoPoints(List<Vector2> points)
    {
        var list = new List<object>();
        if (points == null) return list;
        foreach (var p in points)
        {
            list.Add(new GeoPoint(p.y, p.x));
        }
        return list;
    }

    /// <summary>GeoPoint(緯度, 経度) → Vector2(x=経度, y=緯度)</summary>
    internal static List<Vector2> FromGeoPoints(List<GeoPoint> points)
    {
        var list = new List<Vector2>();
        if (points == null) return list;
        foreach (var gp in points)
        {
            list.Add(new Vector2((float)gp.Longitude, (float)gp.Latitude));
        }
        return list;
    }
}
