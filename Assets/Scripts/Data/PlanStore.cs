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

        // 送るデータを先に組み立てる。ここで失敗したら一覧 (メモリ) にも入れない
        // (先に一覧へ入れると「画面には出るのに保存されていない計画」が残るため)
        var data = ToData(plan);
        data["createdBy"] = FirebaseService.Uid;
        data["createdAt"] = FieldValue.ServerTimestamp;

        _plans.Add(plan);
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

    /// <summary>計画を完了にして保存する (作業終了の「記録して完了」用)。完了した計画は予定一覧に出なくなる</summary>
    public static void Complete(WorkPlan plan)
    {
        plan.Status = WorkPlan.StatusCompleted;
        Update(plan);
    }

    /// <summary>
    /// 進行中で期日が来ている計画を日付順に返す (今日の作業計画セクション用)。
    /// 期日を過ぎても完了していない計画は継続作業として今日も出す (計画の使い回し)。
    /// </summary>
    public static List<WorkPlan> GetActiveDue(DateTime date)
    {
        return _plans
            .Where(p => !p.IsCompleted && p.Date.Date <= date.Date)
            .OrderBy(p => p.Date)
            .ToList();
    }

    /// <summary>進行中で指定した日より後の計画を日付順に返す (今後の作業計画セクション用)</summary>
    public static List<WorkPlan> GetActiveAfter(DateTime date)
    {
        return _plans
            .Where(p => !p.IsCompleted && p.Date.Date > date.Date)
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
            { "status", p.Status ?? WorkPlan.StatusActive }, // active=進行中 / completed=完了
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
        // statusフィールドが無い旧データは進行中として扱う (フィールド初期値のまま)
        if (d.TryGetValue("status", out string status) && !string.IsNullOrEmpty(status)) p.Status = status;
        return p;
    }

    /// <summary>Vector2(x=経度, y=緯度) → GeoPoint(緯度, 経度)。引数の順序が逆なので注意</summary>
    internal static List<object> ToGeoPoints(List<Vector2> points)
    {
        var list = new List<object>();
        if (points == null) return list;
        foreach (var p in points)
        {
            // GeoPointは範囲外やNaNの座標で例外を投げ、保存処理ごと止めてしまう。壊れた点は捨てる
            if (!IsValidLonLat(p)) continue;
            list.Add(new GeoPoint(p.y, p.x));
        }
        return list;
    }

    /// <summary>緯度経度として正しい値か (x=経度, y=緯度)</summary>
    internal static bool IsValidLonLat(Vector2 p)
    {
        return !float.IsNaN(p.x) && !float.IsNaN(p.y)
            && p.x >= -180f && p.x <= 180f && p.y >= -90f && p.y <= 90f;
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
