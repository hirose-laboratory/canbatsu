using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// 計画範囲の地図タイルをディスクキャッシュへ先読みする (山中は圏外なので事前に保存しておく)。
/// 計画の保存時に呼ばれ、範囲のbboxに10%の余白を付けてズーム12〜17を
/// 航空写真+標準地図+地名ラベルの3種でダウンロードする。保存先はMapViewのディスクキャッシュと同じ。
/// 総数が2500枚を超えるときは高ズーム側 (z17→z16...) から間引く。
/// ダウンロードはコルーチンで1枚ずつ直列に行う (タイルサーバーへの負荷と通信の輻輳を避けるため)。
/// </summary>
public static class TilePrefetcher
{
    const int PrefetchMinZoom = 12;
    const int PrefetchMaxZoom = 17;
    const int MaxTiles = 2500;

    /// <summary>進捗表示用テキスト。空文字なら非表示 (取り込み中だけ「地図を保存中 n/m」)</summary>
    public static string StatusText { get; private set; } = "";

    /// <summary>StatusTextが変わるたびに発火 (画面側の進捗表示の更新に使う)</summary>
    public static event Action ProgressChanged;

    // ダウンロード待ちの行列。実行中に別の計画が保存されたら同じコルーチンが続けて処理する
    static readonly Queue<(string url, string path)> _queue = new Queue<(string url, string path)>();
    static int _total; // 今回のバッチの総枚数 (完了で0に戻す)
    static int _done;
    static bool _running;

    /// <summary>
    /// 指定したポリゴン (x=経度, y=緯度) 周辺のタイルの先読みを開始する。
    /// runnerはコルーチンの実行に使う (ページ遷移後も生きているAppRouterを渡すこと)。
    /// </summary>
    public static void Prefetch(MonoBehaviour runner, IReadOnlyList<Vector2> lonLatPoints)
    {
        if (runner == null || lonLatPoints == null || lonLatPoints.Count == 0) return;

        // bboxを求めて10%の余白を付ける (範囲ちょうどだと端の作業中に隣が見えないため)
        double minLat = double.MaxValue, maxLat = double.MinValue;
        double minLng = double.MaxValue, maxLng = double.MinValue;
        foreach (var p in lonLatPoints)
        {
            minLat = Math.Min(minLat, p.y); maxLat = Math.Max(maxLat, p.y);
            minLng = Math.Min(minLng, p.x); maxLng = Math.Max(maxLng, p.x);
        }
        double latMargin = Math.Max((maxLat - minLat) * 0.1, 0.001); // 点1つでも約100mは確保する
        double lngMargin = Math.Max((maxLng - minLng) * 0.1, 0.001);
        minLat -= latMargin; maxLat += latMargin;
        minLng -= lngMargin; maxLng += lngMargin;

        // ズームごとの候補を集める (1タイル位置につき 航空写真+標準地図+ラベル の3枚)
        var perZoom = new List<List<(string url, string path)>>();
        int total = 0;
        for (int z = PrefetchMinZoom; z <= PrefetchMaxZoom; z++)
        {
            var tiles = CollectZoomTiles(z, minLat, maxLat, minLng, maxLng);
            perZoom.Add(tiles);
            total += tiles.Count;
        }

        // 2500枚を超えるなら高ズーム側から丸ごと捨てる (z17→z16の順)
        while (total > MaxTiles && perZoom.Count > 1)
        {
            total -= perZoom[perZoom.Count - 1].Count;
            perZoom.RemoveAt(perZoom.Count - 1);
        }

        // 既にディスクにある物はスキップして行列へ積む
        int added = 0;
        foreach (var tiles in perZoom)
        {
            foreach (var tile in tiles)
            {
                if (File.Exists(tile.path)) continue;
                _queue.Enqueue(tile);
                added++;
            }
        }
        if (added == 0) return;

        _total += added;
        if (_running)
        {
            UpdateStatus(); // 実行中の追加は分母だけ増やして表示を更新する
        }
        else
        {
            runner.StartCoroutine(DownloadLoop());
        }
    }

    /// <summary>1ズーム分のタイル (URL+保存先) を列挙する</summary>
    static List<(string url, string path)> CollectZoomTiles(
        int z, double minLat, double maxLat, double minLng, double maxLng)
    {
        var list = new List<(string url, string path)>();
        int n = 1 << z;
        int x0 = Mathf.Clamp((int)Math.Floor((minLng + 180.0) / 360.0 * n), 0, n - 1);
        int x1 = Mathf.Clamp((int)Math.Floor((maxLng + 180.0) / 360.0 * n), 0, n - 1);
        // 緯度は北ほどタイルYが小さい
        int y0 = Mathf.Clamp((int)Math.Floor(LatToTileY(maxLat, z)), 0, n - 1);
        int y1 = Mathf.Clamp((int)Math.Floor(LatToTileY(minLat, z)), 0, n - 1);

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                list.Add((MapView.TileUrl(MapView.BaseLayer.Photo, z, x, y),
                    MapView.TileCachePath(MapView.BaseLayer.Photo, z, x, y)));
                list.Add((MapView.TileUrl(MapView.BaseLayer.Standard, z, x, y),
                    MapView.TileCachePath(MapView.BaseLayer.Standard, z, x, y)));
                list.Add((MapView.LabelTileUrl(z, x, y),
                    MapView.LabelTileCachePath(z, x, y)));
            }
        }
        return list;
    }

    /// <summary>緯度→タイルY (Webメルカトル)。整数化前の値を返す</summary>
    static double LatToTileY(double lat, int z)
    {
        // メルカトルの定義域に収める (極端な値でlog/tanが壊れないように)
        lat = Math.Max(-85.0511, Math.Min(85.0511, lat));
        double rad = lat * Math.PI / 180.0;
        double v = Math.Log(Math.Tan(rad) + 1.0 / Math.Cos(rad));
        return (1.0 - v / Math.PI) / 2.0 * (1 << z);
    }

    static IEnumerator DownloadLoop()
    {
        _running = true;
        try
        {
            UpdateStatus();
            while (_queue.Count > 0)
            {
                var (url, path) = _queue.Dequeue();
                using (var request = UnityWebRequest.Get(url))
                {
                    yield return request.SendWebRequest();
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(path));
                            File.WriteAllBytes(path, request.downloadHandler.data);
                        }
                        catch (Exception e)
                        {
                            Debug.LogWarning($"タイルの保存に失敗: {e.Message}");
                        }
                    }
                    // 失敗 (圏外など) はスキップして続行。次回の保存時に再挑戦される
                }
                _done++;
                UpdateStatus();
            }
        }
        finally
        {
            // runnerが破棄された場合もここを通り、表示が「保存中」のまま残らないようにする
            _running = false;
            _total = 0;
            _done = 0;
            StatusText = "";
            ProgressChanged?.Invoke();
        }
    }

    static void UpdateStatus()
    {
        StatusText = _total > 0 ? $"地図を保存中 {_done}/{_total}" : "";
        ProgressChanged?.Invoke();
    }
}
