using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using UnityEngine;

/// <summary>
/// 森林組合の測量データ (GPXファイル) の読み込み。
/// 現場では山林の境界を「測量→座標化→GPX」で管理しており (起点に凡その座標を割当て)、
/// そのルート (rte) やトラック (trk) をそのまま間伐範囲のポリゴンとして取り込む。
///
/// ファイルの置き場所: persistentDataPath/gpx/ (アプリ専用フォルダ)。
/// PCからUSB接続で Android/data/(パッケージ名)/files/gpx/ にコピーするか、adb pushで入れる。
/// </summary>
public static class GpxImporter
{
    /// <summary>GPX1本分の境界 (ルートまたはトラック)</summary>
    public class GpxBoundary
    {
        public string FileName;                  // 元ファイル名 (表示用)
        public string Name;                      // GPX内の名前 (例: ㈱ZTV23.84ha)
        public List<Vector2> Points = new List<Vector2>(); // x=経度, y=緯度 (アプリ共通の並び)
    }

    /// <summary>GPXフォルダのパス (無ければ作る。ダイアログの案内表示にも使う)</summary>
    public static string FolderPath
    {
        get
        {
            string dir = Path.Combine(Application.persistentDataPath, "gpx");
            try { Directory.CreateDirectory(dir); } catch (Exception) { }
            return dir;
        }
    }

    /// <summary>gpxフォルダの全ファイルから境界を読み込む (壊れたファイルは読み飛ばす)</summary>
    public static List<GpxBoundary> LoadAll()
    {
        var result = new List<GpxBoundary>();
        string[] files;
        try
        {
            files = Directory.GetFiles(FolderPath, "*.gpx");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"GPXフォルダを読めません: {e.Message}");
            return result;
        }
        foreach (var file in files)
        {
            try
            {
                result.AddRange(ParseFile(file));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"GPXの読み込みに失敗 ({Path.GetFileName(file)}): {e.Message}");
            }
        }
        return result;
    }

    /// <summary>1ファイルから境界を取り出す。rte(ルート) と trk(トラック) の両対応</summary>
    public static List<GpxBoundary> ParseFile(string path)
    {
        var doc = new XmlDocument();
        doc.Load(path);
        // xmlns宣言に左右されないよう、名前空間を無視してローカル名で辿る
        var result = new List<GpxBoundary>();
        string fileName = Path.GetFileNameWithoutExtension(path);

        foreach (XmlNode rte in doc.GetElementsByTagName("rte"))
        {
            var b = ReadBoundary(rte, "rtept", fileName);
            if (b != null) result.Add(b);
        }
        foreach (XmlNode trk in doc.GetElementsByTagName("trk"))
        {
            // トラックは trkseg の下に trkpt (セグメントはまとめて1本の境界として扱う)
            var b = ReadBoundary(trk, "trkpt", fileName);
            if (b != null) result.Add(b);
        }
        return result;
    }

    static GpxBoundary ReadBoundary(XmlNode container, string pointTag, string fileName)
    {
        var b = new GpxBoundary { FileName = fileName, Name = fileName };
        foreach (XmlNode child in container.ChildNodes)
        {
            if (child.LocalName == "name" && !string.IsNullOrWhiteSpace(child.InnerText))
            {
                b.Name = child.InnerText.Trim();
                break;
            }
        }

        CollectPoints(container, pointTag, b.Points);
        if (b.Points.Count < 3) return null; // 範囲にならない (計測線など) は対象外

        // 測量の境界は始点=終点の閉じたリングで来ることが多い。閉じ点は落とす (アプリ側は開いた頂点列で持つ)
        var first = b.Points[0];
        var last = b.Points[b.Points.Count - 1];
        if (Mathf.Abs(first.x - last.x) < 1e-7f && Mathf.Abs(first.y - last.y) < 1e-7f)
        {
            b.Points.RemoveAt(b.Points.Count - 1);
        }
        return b.Points.Count >= 3 ? b : null;
    }

    static void CollectPoints(XmlNode node, string pointTag, List<Vector2> into)
    {
        foreach (XmlNode child in node.ChildNodes)
        {
            if (child.LocalName == pointTag && child.Attributes != null)
            {
                var latAttr = child.Attributes["lat"];
                var lonAttr = child.Attributes["lon"];
                if (latAttr == null || lonAttr == null) continue;
                if (double.TryParse(latAttr.Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double lat)
                    && double.TryParse(lonAttr.Value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double lon))
                {
                    into.Add(new Vector2((float)lon, (float)lat)); // x=経度, y=緯度
                }
            }
            else if (child.HasChildNodes)
            {
                CollectPoints(child, pointTag, into); // trkseg などの入れ子を辿る
            }
        }
    }

    /// <summary>面積 [ha] (正距円筒近似+靴ひも公式。MapView.AreaHaと同じ考え方。一覧の表示用)</summary>
    public static double AreaHa(List<Vector2> points)
    {
        if (points == null || points.Count < 3) return 0;
        double latSum = 0;
        foreach (var p in points) latSum += p.y;
        double lat0 = latSum / points.Count;
        double mx = 111320.0 * Math.Cos(lat0 * Math.PI / 180.0);
        double my = 111320.0;
        double s = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            s += (a.x * mx) * (b.y * my) - (b.x * mx) * (a.y * my);
        }
        return Math.Abs(s) / 2.0 / 10000.0;
    }
}
