using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Firebase.Firestore;
using UnityEngine;

/// <summary>
/// 森林簿 (Firestoreの forest_registry コレクション) の検索。
/// 1ドキュメント = 1小班 (国有林の区画)。今は三重県のみ入っている。
/// データの作り方は proconbackend/forestry/ を参照。
/// </summary>
public static class ForestRegistry
{
    const string Collection = "forest_registry";

    /// <summary>小班1つ分の森林簿データ</summary>
    public class ForestPatch
    {
        public string Id;
        public string Name;        // 林小班名称 (例: "78_林班_ほ")
        public string City;
        public string Species1;    // 主な樹種
        public int Age1;           // 林齢 (年)
        public double VolumeM3;
        public double AreaHa;
        public double CenterLat;
        public double CenterLng;
        public List<Vector2> Polygon = new List<Vector2>(); // x=経度, y=緯度 (アプリ内の他と同じ)
    }

    /// <summary>
    /// 選択範囲 (x=経度, y=緯度) に重なる小班を返す。
    /// Firestoreは地理検索ができないので、緯度の範囲で絞ってから残りは端末側で判定する。
    /// </summary>
    public static async Task<List<ForestPatch>> QueryAsync(IReadOnlyList<Vector2> selection)
    {
        var result = new List<ForestPatch>();
        if (selection == null || selection.Count < 3 || FirebaseService.Db == null) return result;

        double minLat = double.MaxValue, maxLat = double.MinValue;
        double minLng = double.MaxValue, maxLng = double.MinValue;
        foreach (var p in selection)
        {
            minLat = Math.Min(minLat, p.y); maxLat = Math.Max(maxLat, p.y);
            minLng = Math.Min(minLng, p.x); maxLng = Math.Max(maxLng, p.x);
        }

        // 小班の中心が選択範囲の少し外でもポリゴンは重なりうるので余白をとる (約2km)
        const double margin = 0.02;
        var snap = await FirebaseService.Db.Collection(Collection)
            .WhereGreaterThanOrEqualTo("centerLat", minLat - margin)
            .WhereLessThanOrEqualTo("centerLat", maxLat + margin)
            .GetSnapshotAsync();

        foreach (var doc in snap.Documents)
        {
            if (!doc.TryGetValue("centerLng", out double centerLng)) continue;
            if (centerLng < minLng - margin || centerLng > maxLng + margin) continue; // 経度も絞る

            var patch = FromSnapshot(doc);
            if (Intersects(selection, patch.Polygon))
            {
                result.Add(patch);
            }
        }
        return result;
    }

    /// <summary>複数の小班から代表を選ぶ (選択範囲の中心を含むもの優先、無ければ一番大きいもの)</summary>
    public static ForestPatch PickDominant(List<ForestPatch> patches, IReadOnlyList<Vector2> selection)
    {
        if (patches == null || patches.Count == 0) return null;

        var center = Centroid(selection);
        foreach (var patch in patches)
        {
            if (PointInPolygon(center.x, center.y, patch.Polygon)) return patch;
        }

        ForestPatch largest = patches[0];
        foreach (var patch in patches)
        {
            if (patch.AreaHa > largest.AreaHa) largest = patch;
        }
        return largest;
    }

    /// <summary>森林簿の樹種名をアプリの樹種チップに寄せる (無いものはそのまま返してチップに追加される)</summary>
    public static string ToAppSpecies(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "スギ";
        if (raw.Contains("スギ")) return "スギ";
        if (raw.Contains("ヒノキ")) return "ヒノキ";
        if (raw.Contains("カラマツ")) return "カラマツ";
        if (raw.Contains("マツ")) return "マツ"; // アカマツ・クロマツなど
        return raw;
    }

    static ForestPatch FromSnapshot(DocumentSnapshot d)
    {
        var patch = new ForestPatch { Id = d.Id };
        if (d.TryGetValue("name", out string name)) patch.Name = name;
        if (d.TryGetValue("city", out string city)) patch.City = city;
        if (d.TryGetValue("species1", out string species)) patch.Species1 = species;
        if (d.TryGetValue("age1", out int age)) patch.Age1 = age;
        if (d.TryGetValue("volumeM3", out double volume)) patch.VolumeM3 = volume;
        if (d.TryGetValue("areaHa", out double area)) patch.AreaHa = area;
        if (d.TryGetValue("centerLat", out double lat)) patch.CenterLat = lat;
        if (d.TryGetValue("centerLng", out double lng)) patch.CenterLng = lng;
        if (d.TryGetValue("areaPolygon", out List<GeoPoint> polygon))
        {
            foreach (var gp in polygon)
            {
                patch.Polygon.Add(new Vector2((float)gp.Longitude, (float)gp.Latitude));
            }
        }
        return patch;
    }

    // ---- 図形判定 (経度=x, 緯度=y) ----

    /// <summary>2つのポリゴンが重なっていそうか (お互いの頂点と中心が相手の中に入るかで簡易判定)</summary>
    static bool Intersects(IReadOnlyList<Vector2> a, IReadOnlyList<Vector2> b)
    {
        if (b == null || b.Count < 3) return false;

        foreach (var p in a)
        {
            if (PointInPolygon(p.x, p.y, b)) return true;
        }
        foreach (var p in b)
        {
            if (PointInPolygon(p.x, p.y, a)) return true;
        }
        var ca = Centroid(a);
        if (PointInPolygon(ca.x, ca.y, b)) return true;
        var cb = Centroid(b);
        return PointInPolygon(cb.x, cb.y, a);
    }

    static Vector2 Centroid(IReadOnlyList<Vector2> points)
    {
        var sum = Vector2.zero;
        foreach (var p in points) sum += p;
        return sum / points.Count;
    }

    static bool PointInPolygon(double x, double y, IReadOnlyList<Vector2> points)
    {
        bool inside = false;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
        {
            var a = points[i];
            var b = points[j];
            if ((a.y > y) != (b.y > y) &&
                x < (b.x - a.x) * (y - a.y) / (b.y - a.y) + a.x)
            {
                inside = !inside;
            }
        }
        return inside;
    }
}
