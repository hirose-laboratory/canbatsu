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

    /// <summary>
    /// 表示中のMapViewの数 (MapViewが付け外しで増減させる)。
    /// 1以上の間はダウンロードを一時停止し、地図表示用のタイル取得と回線を取り合わないようにする
    /// </summary>
    public static int MapVisibleCount;

    // ダウンロード待ちの行列。実行中に別の計画が保存されたら同じコルーチンが続けて処理する
    static readonly Queue<(string url, string path, bool isBase)> _queue =
        new Queue<(string url, string path, bool isBase)>();
    static int _total; // 今回のバッチの総枚数 (完了で0に戻す)
    static int _done;
    static int _baseRemaining; // 日本全域ベースの残り枚数 (0になったら完了マーカーを書く)
    static bool _baseHadFailure; // ベース保存で失敗タイルがあったか (あれば完了扱いにせず次回再開)
    static bool _baseDone;       // ベース保存が完了済みと確認できた (以後は何もしない)
    static bool _baseScanning;   // 未保存タイルの洗い出し中
    static bool _running;

    // ---- 日本全域のベース地図 (山奥のオフラインでも地図が開けるように) ----
    //
    // 全国を詳細ズームまで保存するのは数十GB級で不可能なので、ベースはz10まで
    // (全国どこでも開ける粗い地図)。詳細は「計画保存時の範囲先読み(z12-17)」と
    // 「一度見た場所の自動キャッシュ」が担う三層構成。
    const int JapanMinZoom = 5;
    const int JapanMaxZoom = 10;
    const double JapanMinLat = 24.0, JapanMaxLat = 45.8;   // 沖縄〜北海道
    const double JapanMinLng = 122.5, JapanMaxLng = 146.5; // 与那国〜択捉

    /// <summary>ベース保存完了の目印ファイル (これがあれば二度目以降は何もしない)</summary>
    static string JapanDoneMarker =>
        Path.Combine(Application.persistentDataPath, "tiles", "japan_base_v1.done");

    /// <summary>
    /// 日本全域のベース地図 (標準地図 z5〜10、約90〜150MB) をWi-Fi接続時に一括保存する。
    /// 完了済みなら何もしない。途中で落ちても保存済みタイルはスキップされるので再開できる。
    /// ホーム表示のたびに呼んでよい (冪等)。
    /// </summary>
    public static void PrefetchJapanBase(MonoBehaviour runner)
    {
        if (runner == null) return;
        if (_baseDone || _baseScanning || _baseRemaining > 0) return; // 完了済み / 調査中 / すでに積んである
        if (File.Exists(JapanDoneMarker))
        {
            _baseDone = true; // 以後はファイルの確認もしない
            return;
        }
        // 全国分は量があるのでWi-Fiのときだけ (計画範囲の先読みは従来どおり回線を問わない)
        if (Application.internetReachability != NetworkReachability.ReachableViaLocalAreaNetwork) return;

        runner.StartCoroutine(ScanJapanBase(runner));
    }

    /// <summary>
    /// 未保存のベースタイルを洗い出して行列に積む。約8,000枚ぶんのファイル確認になるので、
    /// 予定ページを開くたびに一気にやると画面が一瞬止まる。数フレームに分けて行う
    /// </summary>
    static IEnumerator ScanJapanBase(MonoBehaviour runner)
    {
        _baseScanning = true;
        var missing = new List<(string url, string path)>();
        try
        {
            int checkedCount = 0;
            for (int z = JapanMinZoom; z <= JapanMaxZoom; z++)
            {
                int n = 1 << z;
                int x0 = Mathf.Clamp((int)Math.Floor((JapanMinLng + 180.0) / 360.0 * n), 0, n - 1);
                int x1 = Mathf.Clamp((int)Math.Floor((JapanMaxLng + 180.0) / 360.0 * n), 0, n - 1);
                int y0 = Mathf.Clamp((int)Math.Floor(LatToTileY(JapanMaxLat, z)), 0, n - 1);
                int y1 = Mathf.Clamp((int)Math.Floor(LatToTileY(JapanMinLat, z)), 0, n - 1);
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        // ベースは標準地図のみ (既定レイヤー)
                        string path = MapView.TileCachePath(MapView.BaseLayer.Standard, z, x, y);
                        if (!File.Exists(path))
                        {
                            missing.Add((MapView.TileUrl(MapView.BaseLayer.Standard, z, x, y), path));
                        }
                        if (++checkedCount % 400 == 0) yield return null;
                    }
                }
            }
        }
        finally
        {
            _baseScanning = false;
        }

        if (missing.Count == 0)
        {
            WriteJapanDoneMarker(); // 全部保存済みだった
            yield break;
        }
        foreach (var tile in missing) _queue.Enqueue((tile.url, tile.path, true));
        _baseRemaining = missing.Count;
        _total += missing.Count;
        if (_running) UpdateStatus();
        else runner.StartCoroutine(DownloadLoop());
    }

    static void WriteJapanDoneMarker()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(JapanDoneMarker));
            File.WriteAllText(JapanDoneMarker, DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            _baseDone = true;
            Debug.Log("[TilePrefetcher] 日本全域ベース地図の保存が完了");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"完了マーカーの書き込みに失敗: {e.Message}");
        }
    }

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
                _queue.Enqueue((tile.url, tile.path, false));
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
                // 地図を開いている間は先読みを止めて表示を優先する (閉じたら再開)
                while (MapVisibleCount > 0) yield return null;

                var (url, path, isBase) = _queue.Dequeue();
                using (var request = UnityWebRequest.Get(url))
                {
                    request.timeout = 20; // 固まった通信で行列全体が止まらないよう打ち切る
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
                            if (isBase) _baseHadFailure = true;
                        }
                    }
                    else if (isBase)
                    {
                        _baseHadFailure = true; // 穴あきのまま完了扱いにしない (次回ホーム表示で再開)
                    }
                    // 失敗 (圏外など) はスキップして続行。次回の保存時に再挑戦される
                }
                _done++;
                if (isBase && --_baseRemaining == 0)
                {
                    if (_baseHadFailure)
                    {
                        Debug.Log("[TilePrefetcher] 日本地図の保存に失敗分あり。次回Wi-Fi時に残りを再開する");
                    }
                    else
                    {
                        WriteJapanDoneMarker();
                    }
                    _baseHadFailure = false;
                }
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
        StatusText = _total <= 0 ? ""
            : _baseRemaining > 0 ? $"日本地図を保存中 {_done}/{_total} (Wi-Fi推奨)"
            : $"地図を保存中 {_done}/{_total}";
        ProgressChanged?.Invoke();
    }
}
