using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Firebase.Firestore;
using UnityEngine;

/// <summary>
/// AR選木マップのストア。
/// 作業中に検出した木 (選木フラグ付き) を、計画ごとの基準点マップ座標で1ドキュメントに保存する。
/// パス: work_plans/{planId}/armap/state に {trees: [{x,y,z,widthCm,selected}], updatedAt}。
/// 配列まるごと上書き方式 (木の本数は数十本規模なので1ドキュメントで足りる。部分更新の整合を考えなくてよい)。
/// 圏外対策: 書き込みは await しない (Firestoreのオフラインキャッシュが電波復帰後に自動送信する)。
/// </summary>
public static class PlanTreeStore
{
    const string PlanCollection = "work_plans";
    const string MapCollection = "armap";
    const string MapDocument = "state";

    /// <summary>保存される木1本分。座標は基準点マップ座標 (基準点=原点、基準方向=+Z、y=基準点からの相対高さ)</summary>
    public class PlanTree
    {
        public Vector3 MapPos;   // マップ座標の幹の足元
        public int WidthCm;      // 幹の太さ概算 [cm] (不明なら0)
        public bool Selected;    // 選木 (伐採対象マーク) されているか
    }

    /// <summary>基準点の地理情報 (基準点セット時のGPSとコンパス)。選木結果を地図に載せる位置合わせに使う</summary>
    public class AnchorGeo
    {
        public double Lat;
        public double Lon;
        public float HeadingDeg;  // マップ座標+Z方向の真北からの方位 [度・時計回り]
        public bool HasHeading;   // コンパスが取れていたか (falseなら地図表示には使えない)
    }

    /// <summary>読み込み結果 (木リスト + 基準点の地理情報)</summary>
    public class PlanTreeMap
    {
        public List<PlanTree> Trees = new List<PlanTree>();
        public AnchorGeo Geo; // 無ければnull
    }

    /// <summary>この計画の木マップを丸ごと上書き保存する (awaitしない)。anchorGeoは取れたときだけ渡す</summary>
    public static void Save(string planId, IReadOnlyList<PlanTree> entries, AnchorGeo geo = null)
    {
        if (string.IsNullOrEmpty(planId) || entries == null) return;
        if (FirebaseService.Db == null)
        {
            Debug.LogWarning("PlanTreeStore: Firestore未初期化のため保存をスキップします");
            return;
        }

        var trees = new List<object>(entries.Count);
        foreach (var e in entries)
        {
            trees.Add(new Dictionary<string, object>
            {
                { "x", (double)e.MapPos.x },
                { "y", (double)e.MapPos.y },
                { "z", (double)e.MapPos.z },
                { "widthCm", e.WidthCm },
                { "selected", e.Selected },
            });
        }
        var data = new Dictionary<string, object>
        {
            { "trees", trees },
            { "updatedAt", FieldValue.ServerTimestamp },
        };
        if (geo != null)
        {
            data["anchorGeo"] = new Dictionary<string, object>
            {
                { "lat", geo.Lat },
                { "lon", geo.Lon },
                { "headingDeg", (double)geo.HeadingDeg },
                { "hasHeading", geo.HasHeading },
            };
        }
        // MergeAll: GPSが取れなかった回の保存で、以前のanchorGeoを消さないため (treesは配列ごと置き換わる)
        FirebaseService.Db.Collection(PlanCollection).Document(planId)
            .Collection(MapCollection).Document(MapDocument)
            .SetAsync(data, SetOptions.MergeAll); // 圏外対策: awaitしない
    }

    /// <summary>この計画の木マップを読み込む。未保存・失敗時は空 (Trees空リスト・Geo=null)</summary>
    public static async Task<PlanTreeMap> LoadAsync(string planId)
    {
        var result = new PlanTreeMap();
        if (string.IsNullOrEmpty(planId) || FirebaseService.Db == null) return result;
        try
        {
            var snap = await FirebaseService.Db.Collection(PlanCollection).Document(planId)
                .Collection(MapCollection).Document(MapDocument).GetSnapshotAsync();
            if (!snap.Exists) return result;
            if (snap.TryGetValue("trees", out List<object> raw))
            {
                foreach (var item in raw)
                {
                    if (!(item is Dictionary<string, object> d)) continue;
                    result.Trees.Add(new PlanTree
                    {
                        MapPos = new Vector3(ToFloat(d, "x"), ToFloat(d, "y"), ToFloat(d, "z")),
                        WidthCm = (int)Math.Round(ToFloat(d, "widthCm")),
                        Selected = d.TryGetValue("selected", out var s) && s is bool b && b,
                    });
                }
            }
            if (snap.TryGetValue("anchorGeo", out Dictionary<string, object> g))
            {
                result.Geo = new AnchorGeo
                {
                    Lat = ToDouble(g, "lat"),
                    Lon = ToDouble(g, "lon"),
                    HeadingDeg = ToFloat(g, "headingDeg"),
                    HasHeading = g.TryGetValue("hasHeading", out var hh) && hh is bool hb && hb,
                };
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"木マップの読み込みに失敗しました: {e.Message}");
        }
        return result;
    }

    static double ToDouble(Dictionary<string, object> d, string key)
    {
        if (d.TryGetValue(key, out var v))
        {
            if (v is double dv) return dv;
            if (v is long lv) return lv;
        }
        return 0;
    }

    /// <summary>Firestoreの数値はlong/doubleで返るので両方受ける</summary>
    static float ToFloat(Dictionary<string, object> d, string key)
    {
        if (d.TryGetValue(key, out var v))
        {
            if (v is double dv) return (float)dv;
            if (v is long lv) return lv;
        }
        return 0f;
    }
}
