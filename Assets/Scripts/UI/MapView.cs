using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UIElements;

/// <summary>
/// 国土地理院タイルを使ったスライドマップ (GoogleマップのようなUI)。
///
/// 操作 (Googleマップ準拠):
///  - ドラッグ / 2本指ドラッグ: 移動
///  - ピンチ: ズーム (指の中心を基準)
///  - ダブルタップ / ホイール: ズーム (タップ/カーソル位置を基準)
///  - シングルタップ: 間伐範囲の頂点を追加
///
/// タイルのダウンロードにコルーチンを使うため MonoBehaviour (AppRouter) を受け取る。
/// 航空写真には地名が無いため、CARTOの透過地名タイル (OpenStreetMapベース) を上に重ねる。
/// 出典表示: 使用時は画面上に「国土地理院」と「© OpenStreetMap contributors © CARTO」のクレジットを出すこと。
/// </summary>
public class MapView : VisualElement
{
    public enum BaseLayer
    {
        Photo,    // 航空写真 (林の様子が分かる)
        Standard, // 標準地図 (等高線・地名)
    }

    /// <summary>表示専用の範囲ポリゴン (記録ページの予定/記録の色分け表示などに使う)</summary>
    public class DisplayPolygon
    {
        public List<Vector2> Points = new List<Vector2>(); // x=経度, y=緯度
        public Color Color = Color.green;

        /// <summary>タップされたときに呼び出し側が元データ (WorkPlan/WorkRecord) を特定するための入れ物</summary>
        public object UserData;
    }

    const int TileSize = 256;
    const int MinZoom = 5;
    // 最大ズームは18 (選木結果の確認は紙地図でいう1/2500相当まで寄りたい、という現場の要望。
    // 国土地理院タイルは標準地図/航空写真ともz18まで配信されている)
    const int MaxZoom = 18;

    // 小班の下敷きポリゴンを描く最小ズーム (これ未満は細かすぎて潰れるので描画もヒットもしない)
    const int MinForestZoom = 13;

    readonly MonoBehaviour _runner;

    // 地図の状態 (中心位置は現在ズームのワールドピクセル座標で持つ)
    double _centerX;
    double _centerY;
    int _zoom = 15;
    // 既定は標準地図 (等高線入り)。林業の計画用途では地形が読める方が見やすい (2026-09-13ユーザー決定)
    BaseLayer _layer = BaseLayer.Standard;

    // タイル管理
    readonly VisualElement _content;   // ピンチ中に拡大縮小をかける入れ物
    readonly VisualElement _tileLayer;
    readonly VisualElement _labelTileLayer; // 航空写真の上に重ねる地名タイル
    readonly VisualElement _overlay;
    readonly Dictionary<string, Image> _tiles = new Dictionary<string, Image>();
    readonly Dictionary<string, Texture2D> _textureCache = new Dictionary<string, Texture2D>();
    readonly HashSet<string> _loading = new HashSet<string>();

    // 範囲選択ポリゴン (緯度経度で保持するのでズームしてもずれない)
    readonly List<Vector2> _polygon = new List<Vector2>(); // x=経度, y=緯度

    // タップで追加した頂点の挿入位置の履歴。
    // 3点目以降は一番近い辺へ挿入するため末尾に入るとは限らず、「頂点を1つ戻す」で正しい頂点を消すのに使う
    readonly List<int> _addHistory = new List<int>();

    // 表示専用ポリゴン (タップ編集の対象外)
    readonly List<DisplayPolygon> _displayPolygons = new List<DisplayPolygon>();
    DisplayPolygon _selectedDisplayPolygon;
    Action _pendingFit; // レイアウト確定前にフィットが呼ばれたときの再実行用

    // 小班の下敷きポリゴン (森林簿の境界表示。表示専用ポリゴンよりさらに下に淡く描く)
    readonly List<DisplayPolygon> _forestPolygons = new List<DisplayPolygon>();

    // 点マーカー (選木結果の地図表示用。x=経度, y=緯度)
    readonly List<(Vector2 lonLat, Color color)> _pointMarkers = new List<(Vector2, Color)>();

    // スケールバー (紙地図の縮尺感覚に応える。左下に「━━ 100 m」)
    VisualElement _scaleLine;
    Label _scaleLabel;
    static readonly float[] ScaleSteps = { 10f, 20f, 50f, 100f, 200f, 500f, 1000f, 2000f, 5000f };

    /// <summary>表示専用ポリゴンがタップされたとき (何もない場所ならnull)。ポップアップ表示などに使う。
    /// 表示専用ポリゴンが無い場所では下敷きの小班ポリゴンも判定し、UserDataの型で受け側が区別する</summary>
    public event Action<DisplayPolygon> DisplayPolygonClicked;

    /// <summary>ユーザーがパン/ズーム操作をしたとき。現在地への自動センタリングを止める判定に使う</summary>
    public event Action UserInteracted;

    /// <summary>表示範囲が変わったとき (パン終了・ズーム・SetCenter後)。可視範囲のデータ読込に使う</summary>
    public event Action ViewChanged;

    // ポインタ管理 (マウス+マルチタッチ)
    readonly Dictionary<int, Vector2> _activePointers = new Dictionary<int, Vector2>();

    // 1本指ドラッグ
    bool _dragging;
    int _dragPointerId = -1;
    Vector2 _lastPointerPos;
    float _movedDistance;
    bool _pannedThisDrag; // このドラッグで実際に地図が動いたか (パン終了時のViewChanged発火用)

    // ピンチ
    bool _pinching;
    float _pinchStartDistance;
    Vector2 _pinchStartMid;
    Vector2 _pinchMid;
    float _pinchFactor = 1f;
    double _pinchStartCenterX;
    double _pinchStartCenterY;
    int _pinchBaseZoom;

    // ダブルタップ判定
    float _lastTapTime = -10f;
    Vector2 _lastTapPos;
    bool _lastTapAddedVertex;

    // 頂点の長押しドラッグ (Googleマップのピン移動と同じ操作感)
    const float VertexHitRadius = 26f;
    const long LongPressDelayMs = 450;
    int _candidateVertexIndex = -1;   // 押した位置にあった頂点
    int _draggingVertexIndex = -1;    // 長押し成立後、動かしている頂点
    IVisualElementScheduledItem _longPressTimer;

    /// <summary>頂点が増減したときに通知 (確定ボタンの表示切替などに使う)</summary>
    public event Action PolygonChanged;

    /// <summary>タップで頂点を追加できるか (結果表示中はfalseにする)</summary>
    public bool AllowPointAdding { get; set; } = true;

    public int PointCount => _polygon.Count;
    public BaseLayer CurrentLayer => _layer;
    public int CurrentZoom => _zoom;

    /// <summary>編集中ポリゴンの頂点一覧 (x=経度, y=緯度)。保存時に使う</summary>
    public IReadOnlyList<Vector2> EditingPoints => _polygon;

    public MapView(MonoBehaviour runner)
    {
        _runner = runner;

        style.flexGrow = 1;
        style.overflow = Overflow.Hidden;
        style.backgroundColor = new Color(0.85f, 0.88f, 0.85f);

        _content = new VisualElement { pickingMode = PickingMode.Ignore };
        SetAbsoluteFill(_content);
        Add(_content);

        _tileLayer = new VisualElement { pickingMode = PickingMode.Ignore };
        SetAbsoluteFill(_tileLayer);
        _content.Add(_tileLayer);

        _labelTileLayer = new VisualElement { pickingMode = PickingMode.Ignore };
        SetAbsoluteFill(_labelTileLayer);
        _content.Add(_labelTileLayer);

        _overlay = new VisualElement { pickingMode = PickingMode.Ignore };
        SetAbsoluteFill(_overlay);
        _overlay.generateVisualContent += OnGenerateOverlay;
        _content.Add(_overlay);

        RegisterCallback<GeometryChangedEvent>(_ =>
        {
            RefreshTiles();
            if (_pendingFit != null)
            {
                var fit = _pendingFit;
                _pendingFit = null;
                fit();
            }
        });
        RegisterCallback<PointerDownEvent>(OnPointerDown);
        RegisterCallback<PointerMoveEvent>(OnPointerMove);
        RegisterCallback<PointerUpEvent>(OnPointerUp);
        RegisterCallback<PointerCancelEvent>(evt => RemovePointer(evt.pointerId));
        RegisterCallback<WheelEvent>(OnWheel);

        BuildScaleBar();

        // 現在地の青ドットを追従させる (地図は操作時しか再描画しないので、位置が変わる分は定期的に描き直す)
        schedule.Execute(() =>
        {
            if (LocationProvider.HasFix) _overlay.MarkDirtyRepaint();
        }).Every(1000);
    }

    static void SetAbsoluteFill(VisualElement element)
    {
        element.style.position = Position.Absolute;
        element.style.left = 0;
        element.style.top = 0;
        element.style.right = 0;
        element.style.bottom = 0;
    }

    // ---- 公開API ----

    public void SetCenter(double latitude, double longitude, int zoom)
    {
        _zoom = Mathf.Clamp(zoom, MinZoom, MaxZoom);
        _centerX = LonToWorldX(longitude, _zoom);
        _centerY = LatToWorldY(latitude, _zoom);
        ClearTileElements();
        ClampCenter();
        RefreshTiles();
        _overlay.MarkDirtyRepaint();
        ViewChanged?.Invoke();
    }

    /// <summary>可視範囲の緯度経度 (森林簿の範囲読込などに使う)。レイアウト前は中心1点の範囲を返す</summary>
    public (double minLat, double maxLat, double minLng, double maxLng) VisibleBounds()
    {
        float w = resolvedStyle.width;
        float h = resolvedStyle.height;
        if (w <= 0 || h <= 0 || float.IsNaN(w) || float.IsNaN(h)) { w = 0; h = 0; }

        double topLeftX = _centerX - w / 2.0;
        double topLeftY = _centerY - h / 2.0;
        return (
            minLat: WorldYToLat(topLeftY + h, _zoom), // 緯度は北ほどYが小さい
            maxLat: WorldYToLat(topLeftY, _zoom),
            minLng: WorldXToLon(topLeftX, _zoom),
            maxLng: WorldXToLon(topLeftX + w, _zoom));
    }

    /// <summary>画面中央を基準に1段ズームイン (+ボタン用)</summary>
    public void ZoomIn() => ZoomAt(HalfSize(), 1);

    /// <summary>画面中央を基準に1段ズームアウト (-ボタン用)</summary>
    public void ZoomOut() => ZoomAt(HalfSize(), -1);

    Vector2 HalfSize() => new Vector2(resolvedStyle.width / 2f, resolvedStyle.height / 2f);

    /// <summary>
    /// 指定した画面位置の地点を動かさずにズームする (Googleマップと同じ挙動)。
    /// カーソルの真下の場所がそのまま拡大される。
    /// </summary>
    public void ZoomAt(Vector2 localPos, int steps)
    {
        int newZoom = Mathf.Clamp(_zoom + steps, MinZoom, MaxZoom);
        if (newZoom == _zoom) return;

        // ZoomAtに来るのはボタン/ホイール/ダブルタップ、いずれもユーザー操作
        UserInteracted?.Invoke();

        double scale = Math.Pow(2, newZoom - _zoom);

        // カーソル下の地点のワールド座標 → 新ズームで同じ画面位置に来るよう中心を再計算
        double anchorX = _centerX - resolvedStyle.width / 2.0 + localPos.x;
        double anchorY = _centerY - resolvedStyle.height / 2.0 + localPos.y;
        _zoom = newZoom;
        _centerX = anchorX * scale - localPos.x + resolvedStyle.width / 2.0;
        _centerY = anchorY * scale - localPos.y + resolvedStyle.height / 2.0;

        ClearTileElements();
        ClampCenter();
        RefreshTiles();
        _overlay.MarkDirtyRepaint();
        ViewChanged?.Invoke();
    }

    public void SetLayer(BaseLayer layer)
    {
        if (_layer == layer) return;
        _layer = layer;
        ClearTileElements();
        RefreshTiles();
    }

    /// <summary>表示専用ポリゴンを差し替える (記録ページの予定/記録の色分け表示用)</summary>
    public void SetDisplayPolygons(List<DisplayPolygon> polygons)
    {
        _displayPolygons.Clear();
        if (polygons != null) _displayPolygons.AddRange(polygons);
        _selectedDisplayPolygon = null;
        _overlay.MarkDirtyRepaint();
    }

    /// <summary>小班の下敷きポリゴンを差し替える (表示専用ポリゴンより下に細く淡く描く)</summary>
    public void SetForestPolygons(List<DisplayPolygon> polygons)
    {
        _forestPolygons.Clear();
        if (polygons != null) _forestPolygons.AddRange(polygons);
        _overlay.MarkDirtyRepaint();
    }

    /// <summary>ポリゴンの選択ハイライトを解除する (ポップアップを閉じたときに呼ぶ)</summary>
    public void ClearDisplaySelection()
    {
        _selectedDisplayPolygon = null;
        _overlay.MarkDirtyRepaint();
    }

    /// <summary>表示専用ポリゴンが全部収まるように中心とズームを合わせる</summary>
    public void FitToDisplayPolygons(float paddingPx = 60f)
    {
        if (_displayPolygons.Count == 0) return;

        var all = new List<Vector2>();
        foreach (var poly in _displayPolygons) all.AddRange(poly.Points);
        FitToPoints(all, paddingPx);
    }

    /// <summary>指定した頂点群 (x=経度, y=緯度) が全部収まるように中心とズームを合わせる</summary>
    public void FitToPoints(IReadOnlyList<Vector2> points, float paddingPx = 60f)
    {
        if (points == null || points.Count == 0) return;

        float w = resolvedStyle.width;
        float h = resolvedStyle.height;
        if (w <= 0 || h <= 0 || float.IsNaN(w) || float.IsNaN(h))
        {
            // レイアウト前に呼ばれたら、サイズ確定後にもう一度実行する
            var captured = new List<Vector2>(points);
            _pendingFit = () => FitToPoints(captured, paddingPx);
            return;
        }

        double minLon = double.MaxValue, maxLon = double.MinValue;
        double minLat = double.MaxValue, maxLat = double.MinValue;
        foreach (var p in points)
        {
            minLon = Math.Min(minLon, p.x); maxLon = Math.Max(maxLon, p.x);
            minLat = Math.Min(minLat, p.y); maxLat = Math.Max(maxLat, p.y);
        }
        if (minLon > maxLon) return;

        // 全体が収まる一番大きいズームを探す
        for (int z = MaxZoom; z >= MinZoom; z--)
        {
            double bw = LonToWorldX(maxLon, z) - LonToWorldX(minLon, z);
            double bh = LatToWorldY(minLat, z) - LatToWorldY(maxLat, z); // 緯度は北ほどYが小さい
            if (bw <= w - paddingPx * 2 && bh <= h - paddingPx * 2)
            {
                _zoom = z;
                _centerX = (LonToWorldX(minLon, z) + LonToWorldX(maxLon, z)) / 2.0;
                _centerY = (LatToWorldY(minLat, z) + LatToWorldY(maxLat, z)) / 2.0;
                ClearTileElements();
                ClampCenter();
                RefreshTiles();
                _overlay.MarkDirtyRepaint();
                ViewChanged?.Invoke();
                return;
            }
        }
    }

    /// <summary>編集用ポリゴンを外から設定する (既存計画の範囲を再編集するとき用)</summary>
    public void SetEditingPoints(IEnumerable<Vector2> points)
    {
        _polygon.Clear();
        _addHistory.Clear();
        if (points != null) _polygon.AddRange(points);
        _overlay.MarkDirtyRepaint();
        PolygonChanged?.Invoke();
    }

    /// <summary>
    /// レイヤー切替ボタン用のサムネイル画像 (指定レイヤーの現在地周辺タイル) を読み込む。
    /// Googleマップの左下にある小さな地図プレビューを再現するためのもの。
    /// </summary>
    public void LoadLayerThumbnail(BaseLayer layer, Image target, int thumbnailZoom = 11)
    {
        if (_runner == null) return;

        double lat = WorldYToLat(_centerY, _zoom);
        double lon = WorldXToLon(_centerX, _zoom);
        int n = 1 << thumbnailZoom;
        int x = Mathf.Clamp((int)((lon + 180.0) / 360.0 * n), 0, n - 1);
        int y = Mathf.Clamp((int)(LatToWorldY(lat, thumbnailZoom) / TileSize), 0, n - 1);
        _runner.StartCoroutine(LoadThumbnailCoroutine(TileUrl(layer, thumbnailZoom, x, y), target));
    }

    IEnumerator LoadThumbnailCoroutine(string url, Image target)
    {
        using (var request = UnityWebRequestTexture.GetTexture(url))
        {
            yield return request.SendWebRequest();
            if (request.result == UnityWebRequest.Result.Success)
            {
                target.image = DownloadHandlerTexture.GetContent(request);
            }
        }
    }

    public void RemoveLastPoint()
    {
        if (_polygon.Count == 0) return;

        // 最後に「追加」した頂点を消す (辺への挿入があるので末尾とは限らない)
        int index = _polygon.Count - 1;
        if (_addHistory.Count > 0)
        {
            index = Mathf.Clamp(_addHistory[_addHistory.Count - 1], 0, _polygon.Count - 1);
            _addHistory.RemoveAt(_addHistory.Count - 1);
            for (int i = 0; i < _addHistory.Count; i++)
            {
                if (_addHistory[i] > index) _addHistory[i]--;
            }
        }

        _polygon.RemoveAt(index);
        _overlay.MarkDirtyRepaint();
        PolygonChanged?.Invoke();
    }

    public void ClearPolygon()
    {
        _polygon.Clear();
        _addHistory.Clear();
        _overlay.MarkDirtyRepaint();
        PolygonChanged?.Invoke();
    }

    /// <summary>選択範囲の面積 (ヘクタール)。頂点3つ未満は0</summary>
    public double AreaHa()
    {
        if (_polygon.Count < 3) return 0;

        // 中心緯度基準の正距円筒近似でメートルに直し、靴ひも公式で面積を出す
        const double EarthRadius = 6378137.0;
        double lat0 = 0;
        foreach (var p in _polygon) lat0 += p.y;
        lat0 = lat0 / _polygon.Count * Math.PI / 180.0;

        double area = 0;
        for (int i = 0; i < _polygon.Count; i++)
        {
            var a = _polygon[i];
            var b = _polygon[(i + 1) % _polygon.Count];
            double ax = a.x * Math.PI / 180.0 * EarthRadius * Math.Cos(lat0);
            double ay = a.y * Math.PI / 180.0 * EarthRadius;
            double bx = b.x * Math.PI / 180.0 * EarthRadius * Math.Cos(lat0);
            double by = b.y * Math.PI / 180.0 * EarthRadius;
            area += ax * by - bx * ay;
        }
        return Math.Abs(area) / 2.0 / 10000.0;
    }

    // ---- 入力処理 ----

    void OnPointerDown(PointerDownEvent evt)
    {
        var pos = new Vector2(evt.localPosition.x, evt.localPosition.y);
        _activePointers[evt.pointerId] = pos;
        this.CapturePointer(evt.pointerId);

        if (_activePointers.Count == 1)
        {
            _dragging = true;
            _dragPointerId = evt.pointerId;
            _lastPointerPos = pos;
            _movedDistance = 0;
            _pannedThisDrag = false;

            // 頂点の上で押したなら長押しタイマーを開始 (450ms動かず押し続けたらドラッグモード)
            _candidateVertexIndex = AllowPointAdding ? FindVertexAt(pos) : -1;
            if (_candidateVertexIndex >= 0)
            {
                int captured = _candidateVertexIndex;
                _longPressTimer?.Pause();
                _longPressTimer = schedule.Execute(() =>
                {
                    if (_dragging && !_pinching && _candidateVertexIndex == captured && _movedDistance < 12f)
                    {
                        _draggingVertexIndex = captured;
                        _overlay.MarkDirtyRepaint();
                    }
                }).StartingIn(LongPressDelayMs);
            }
        }
        else if (_activePointers.Count == 2)
        {
            CancelVertexDrag();
            StartPinch();
        }
        // 3本目以降は無視
    }

    /// <summary>画面位置の近くにある頂点の番号を返す (無ければ-1)</summary>
    int FindVertexAt(Vector2 localPos)
    {
        for (int i = _polygon.Count - 1; i >= 0; i--)
        {
            var p = _polygon[i];
            var sp = new Vector2(
                (float)(LonToWorldX(p.x, _zoom) - (_centerX - resolvedStyle.width / 2.0)),
                (float)(LatToWorldY(p.y, _zoom) - (_centerY - resolvedStyle.height / 2.0)));
            if ((sp - localPos).magnitude <= VertexHitRadius) return i;
        }
        return -1;
    }

    void CancelVertexDrag()
    {
        _longPressTimer?.Pause();
        _candidateVertexIndex = -1;
        if (_draggingVertexIndex >= 0)
        {
            _draggingVertexIndex = -1;
            _overlay.MarkDirtyRepaint();
        }
    }

    void OnPointerMove(PointerMoveEvent evt)
    {
        if (!_activePointers.ContainsKey(evt.pointerId)) return;
        var pos = new Vector2(evt.localPosition.x, evt.localPosition.y);
        _activePointers[evt.pointerId] = pos;

        if (_pinching)
        {
            UpdatePinch();
        }
        else if (_dragging && evt.pointerId == _dragPointerId)
        {
            var delta = pos - _lastPointerPos;
            _lastPointerPos = pos;
            _movedDistance += delta.magnitude;

            if (_draggingVertexIndex >= 0)
            {
                // 長押し成立後は地図を動かさず頂点を動かす
                var (lon, lat) = ScreenToLonLat(pos);
                _polygon[_draggingVertexIndex] = new Vector2((float)lon, (float)lat);
                _overlay.MarkDirtyRepaint();
            }
            else
            {
                if (!_pannedThisDrag)
                {
                    // このドラッグで初めて地図が動いた瞬間だけ通知する
                    _pannedThisDrag = true;
                    UserInteracted?.Invoke();
                }
                _centerX -= delta.x;
                _centerY -= delta.y;
                ClampCenter();
                RefreshTiles();
                _overlay.MarkDirtyRepaint();
            }
        }
    }

    void OnPointerUp(PointerUpEvent evt)
    {
        if (!_activePointers.ContainsKey(evt.pointerId)) return;

        // 頂点ドラッグ中に指を離したら移動確定 (タップ扱いにはしない)
        if (_draggingVertexIndex >= 0 && evt.pointerId == _dragPointerId)
        {
            CancelVertexDrag();
            RemovePointer(evt.pointerId);
            PolygonChanged?.Invoke();
            return;
        }

        bool wasDragTap = _dragging && evt.pointerId == _dragPointerId && _movedDistance < 10f && !_pinching;
        bool tappedOnVertex = _candidateVertexIndex >= 0;
        var tapPos = new Vector2(evt.localPosition.x, evt.localPosition.y);

        CancelVertexDrag();
        RemovePointer(evt.pointerId);

        if (wasDragTap)
        {
            HandleTap(tapPos, tappedOnVertex);
        }
    }

    void RemovePointer(int pointerId)
    {
        _activePointers.Remove(pointerId);
        if (this.HasPointerCapture(pointerId))
        {
            this.ReleasePointer(pointerId);
        }

        if (_pinching && _activePointers.Count < 2)
        {
            EndPinch();
        }
        if (pointerId == _dragPointerId)
        {
            _dragging = false;
            _dragPointerId = -1;
            if (_pannedThisDrag && !_pinching)
            {
                // パン終了 (ピンチへ移行した場合はEndPinch側で発火する)
                _pannedThisDrag = false;
                ViewChanged?.Invoke();
            }
        }
    }

    /// <summary>タップ: 1回なら頂点追加、素早く2回なら位置基準ズーム (Googleマップ準拠)</summary>
    void HandleTap(Vector2 tapPos, bool tappedOnVertex = false)
    {
        float now = Time.realtimeSinceStartup;
        bool isDoubleTap = now - _lastTapTime < 0.3f && (tapPos - _lastTapPos).magnitude < 40f;

        if (isDoubleTap)
        {
            // 1回目のタップで追加された頂点は取り消してからズーム
            if (_lastTapAddedVertex)
            {
                RemoveLastPoint();
            }
            ZoomAt(tapPos, 1);
            _lastTapTime = -10f;
            _lastTapAddedVertex = false;
            return;
        }

        _lastTapTime = now;
        _lastTapPos = tapPos;
        _lastTapAddedVertex = false;

        if (AllowPointAdding)
        {
            // 既存の頂点の上をタップしたときは追加しない (長押し移動の誤操作防止)
            if (!tappedOnVertex)
            {
                AddPointAt(tapPos);
                _lastTapAddedVertex = true;
            }
        }
        else if (_displayPolygons.Count > 0 || _forestPolygons.Count > 0)
        {
            // 表示専用ポリゴンのタップ判定 (上に描かれているものを優先)
            var (lon, lat) = ScreenToLonLat(tapPos);
            DisplayPolygon hit = null;
            for (int i = _displayPolygons.Count - 1; i >= 0; i--)
            {
                var poly = _displayPolygons[i];
                if (poly.Points.Count >= 3 && PointInPolygon(lon, lat, poly.Points))
                {
                    hit = poly;
                    break;
                }
            }

            // 表示専用に当たらなければ下敷きの小班を判定する (描画しないズームではヒットもしない)
            if (hit == null && _zoom >= MinForestZoom)
            {
                for (int i = _forestPolygons.Count - 1; i >= 0; i--)
                {
                    var poly = _forestPolygons[i];
                    if (poly.Points.Count >= 3 && PointInPolygon(lon, lat, poly.Points))
                    {
                        hit = poly;
                        break;
                    }
                }
            }

            _selectedDisplayPolygon = hit;
            _overlay.MarkDirtyRepaint();
            DisplayPolygonClicked?.Invoke(hit);
        }
    }

    /// <summary>画面ローカル座標 → 緯度経度</summary>
    (double lon, double lat) ScreenToLonLat(Vector2 localPos)
    {
        double worldX = _centerX - resolvedStyle.width / 2.0 + localPos.x;
        double worldY = _centerY - resolvedStyle.height / 2.0 + localPos.y;
        return (WorldXToLon(worldX, _zoom), WorldYToLat(worldY, _zoom));
    }

    /// <summary>点がポリゴンの内側かどうか (交差数判定)</summary>
    static bool PointInPolygon(double lon, double lat, List<Vector2> points)
    {
        bool inside = false;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
        {
            var a = points[i];
            var b = points[j];
            if ((a.y > lat) != (b.y > lat) &&
                lon < (b.x - a.x) * (lat - a.y) / (b.y - a.y) + a.x)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    void OnWheel(WheelEvent evt)
    {
        // カーソル位置を基準にズーム (Googleマップと同じ)
        // WheelEventはマウス系イベントなので localMousePosition を使う
        var pos = evt.localMousePosition;
        ZoomAt(pos, evt.delta.y < 0 ? 1 : -1);
        evt.StopPropagation();
    }

    // ---- ピンチズーム ----

    void StartPinch()
    {
        _dragging = false;
        _pinching = true;
        _pannedThisDrag = false;
        UserInteracted?.Invoke();

        var pts = GetTwoPointerPositions();
        _pinchStartDistance = Mathf.Max(1f, (pts.a - pts.b).magnitude);
        _pinchStartMid = (pts.a + pts.b) / 2f;
        _pinchMid = _pinchStartMid;
        _pinchFactor = 1f;
        _pinchStartCenterX = _centerX;
        _pinchStartCenterY = _centerY;
        _pinchBaseZoom = _zoom;
    }

    void UpdatePinch()
    {
        var pts = GetTwoPointerPositions();
        float distance = Mathf.Max(1f, (pts.a - pts.b).magnitude);
        _pinchMid = (pts.a + pts.b) / 2f;
        _pinchFactor = Mathf.Clamp(distance / _pinchStartDistance, 0.25f, 4f);

        // ピンチ中はタイルを読み直さず、見た目だけ拡大縮小して追従させる (軽くてなめらか)
        _content.style.transformOrigin = new TransformOrigin(_pinchStartMid.x, _pinchStartMid.y);
        _content.style.scale = new Scale(new Vector3(_pinchFactor, _pinchFactor, 1f));
        _content.style.translate = new Translate(_pinchMid.x - _pinchStartMid.x, _pinchMid.y - _pinchStartMid.y);
    }

    (Vector2 a, Vector2 b) GetTwoPointerPositions()
    {
        Vector2 a = Vector2.zero, b = Vector2.zero;
        int i = 0;
        foreach (var pos in _activePointers.Values)
        {
            if (i == 0) a = pos;
            else if (i == 1) { b = pos; break; }
            i++;
        }
        return (a, b);
    }

    /// <summary>ピンチ終了: 倍率を一番近いズーム段に丸めて本適用する</summary>
    void EndPinch()
    {
        _pinching = false;

        // 見た目だけの変形をリセット
        _content.style.transformOrigin = new TransformOrigin(0, 0);
        _content.style.scale = new Scale(Vector3.one);
        _content.style.translate = new Translate(0, 0);

        int zoomDelta = Mathf.RoundToInt(Mathf.Log(_pinchFactor, 2f));
        int newZoom = Mathf.Clamp(_pinchBaseZoom + zoomDelta, MinZoom, MaxZoom);
        double scale = Math.Pow(2, newZoom - _pinchBaseZoom);

        // ピンチ開始時に指の中心にあった地点が、終了時の指の中心に来るように中心を再計算
        double anchorX = _pinchStartCenterX - resolvedStyle.width / 2.0 + _pinchStartMid.x;
        double anchorY = _pinchStartCenterY - resolvedStyle.height / 2.0 + _pinchStartMid.y;
        _zoom = newZoom;
        _centerX = anchorX * scale - _pinchMid.x + resolvedStyle.width / 2.0;
        _centerY = anchorY * scale - _pinchMid.y + resolvedStyle.height / 2.0;

        ClearTileElements();
        ClampCenter();
        RefreshTiles();
        _overlay.MarkDirtyRepaint();
        ViewChanged?.Invoke();
    }

    void AddPointAt(Vector2 localPos)
    {
        var (lon, lat) = ScreenToLonLat(localPos);
        var point = new Vector2((float)lon, (float)lat);

        // 3点以上あるときは一番近い辺 (隣り合う頂点2つの間) に挿入する。
        // 末尾に足すと離れた場所をタップしたとき線が交差した形になりやすいため
        int insertIndex = _polygon.Count >= 3 ? NearestEdgeInsertIndex(localPos) : _polygon.Count;
        _polygon.Insert(insertIndex, point);

        for (int i = 0; i < _addHistory.Count; i++)
        {
            if (_addHistory[i] >= insertIndex) _addHistory[i]++;
        }
        _addHistory.Add(insertIndex);

        _overlay.MarkDirtyRepaint();
        PolygonChanged?.Invoke();
    }

    /// <summary>タップ位置に一番近い辺を探し、その間に入る挿入先インデックスを返す</summary>
    int NearestEdgeInsertIndex(Vector2 localPos)
    {
        double topLeftX = _centerX - resolvedStyle.width / 2.0;
        double topLeftY = _centerY - resolvedStyle.height / 2.0;

        var screen = new Vector2[_polygon.Count];
        for (int i = 0; i < _polygon.Count; i++)
        {
            screen[i] = new Vector2(
                (float)(LonToWorldX(_polygon[i].x, _zoom) - topLeftX),
                (float)(LatToWorldY(_polygon[i].y, _zoom) - topLeftY));
        }

        int best = _polygon.Count;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < _polygon.Count; i++)
        {
            float d = DistanceToSegment(localPos, screen[i], screen[(i + 1) % _polygon.Count]);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i + 1;
            }
        }
        return best;
    }

    /// <summary>点pから線分abまでの距離</summary>
    static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float lengthSq = ab.sqrMagnitude;
        if (lengthSq < 1e-6f) return (p - a).magnitude;
        float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / lengthSq);
        return (p - (a + ab * t)).magnitude;
    }

    void ClampCenter()
    {
        double worldSize = TileSize * (double)(1 << _zoom);
        double halfH = resolvedStyle.height / 2.0;
        _centerY = Math.Max(halfH, Math.Min(worldSize - halfH, _centerY));
    }

    // ---- タイルの表示 ----

    void ClearTileElements()
    {
        _tileLayer.Clear();
        _labelTileLayer.Clear();
        _tiles.Clear();
    }

    /// <summary>点マーカーを差し替える (選木結果の表示用。x=経度, y=緯度)</summary>
    public void SetPointMarkers(List<(Vector2 lonLat, Color color)> markers)
    {
        _pointMarkers.Clear();
        if (markers != null) _pointMarkers.AddRange(markers);
        _overlay.MarkDirtyRepaint();
    }

    public void ClearPointMarkers() => SetPointMarkers(null);

    // ---- スケールバー (左下。紙地図で縮尺を見て使う現場の感覚に合わせる) ----

    void BuildScaleBar()
    {
        var box = new VisualElement { pickingMode = PickingMode.Ignore };
        box.style.position = Position.Absolute;
        box.style.left = 8;
        box.style.bottom = 6;
        box.style.backgroundColor = new Color(1f, 1f, 1f, 0.75f);
        box.style.borderTopLeftRadius = 6;
        box.style.borderTopRightRadius = 6;
        box.style.borderBottomLeftRadius = 6;
        box.style.borderBottomRightRadius = 6;
        box.style.paddingLeft = 6;
        box.style.paddingRight = 6;
        box.style.paddingTop = 2;
        box.style.paddingBottom = 4;
        Add(box);

        _scaleLabel = new Label("100 m") { pickingMode = PickingMode.Ignore };
        _scaleLabel.style.fontSize = 10;
        _scaleLabel.style.color = new Color(0.2f, 0.2f, 0.2f);
        _scaleLabel.style.unityTextAlign = TextAnchor.MiddleCenter;
        _scaleLabel.style.marginBottom = 1;
        box.Add(_scaleLabel);

        _scaleLine = new VisualElement { pickingMode = PickingMode.Ignore };
        _scaleLine.style.height = 3;
        _scaleLine.style.width = 60;
        _scaleLine.style.backgroundColor = new Color(0.2f, 0.2f, 0.2f);
        box.Add(_scaleLine);
    }

    /// <summary>ズーム/緯度に応じてスケールバーの長さと数字を更新する (RefreshTilesから毎回呼ばれる)</summary>
    void UpdateScaleBar()
    {
        if (_scaleLine == null) return;
        double lat = WorldYToLat(_centerY, _zoom);
        // 1画面ピクセルあたりの実距離 [m] (Webメルカトル)
        double metersPerPx = 156543.03392 * Math.Cos(lat * Math.PI / 180.0) / (1 << _zoom);
        if (metersPerPx <= 0) return;
        // 120px以内に収まる一番大きいキリの良い距離を選ぶ
        float meters = ScaleSteps[0];
        foreach (var step in ScaleSteps)
        {
            if (step / metersPerPx <= 120.0) meters = step;
        }
        _scaleLine.style.width = (float)(meters / metersPerPx);
        _scaleLabel.text = meters >= 1000f ? $"{meters / 1000f:0.#} km" : $"{meters:0} m";
    }

    void RefreshTiles()
    {
        float w = resolvedStyle.width;
        float h = resolvedStyle.height;
        if (w <= 0 || h <= 0 || float.IsNaN(w) || float.IsNaN(h)) return;

        UpdateScaleBar();

        double topLeftX = _centerX - w / 2.0;
        double topLeftY = _centerY - h / 2.0;

        int tileCount = 1 << _zoom;
        int x0 = (int)Math.Floor(topLeftX / TileSize);
        int y0 = (int)Math.Floor(topLeftY / TileSize);
        int x1 = (int)Math.Floor((topLeftX + w) / TileSize);
        int y1 = (int)Math.Floor((topLeftY + h) / TileSize);

        // 航空写真には地名が焼き込まれていないので、透過の地名タイルを上に重ねる
        bool wantLabels = _layer == BaseLayer.Photo;

        var needed = new HashSet<string>();
        for (int ty = y0; ty <= y1; ty++)
        {
            if (ty < 0 || ty >= tileCount) continue;
            for (int tx = x0; tx <= x1; tx++)
            {
                int wrappedX = ((tx % tileCount) + tileCount) % tileCount;
                float left = (float)(tx * (double)TileSize - topLeftX);
                float top = (float)(ty * (double)TileSize - topLeftY);

                string key = TileKey(_layer, _zoom, wrappedX, ty);
                needed.Add(key);
                var image = EnsureTileImage(key, TileUrl(_layer, _zoom, wrappedX, ty),
                    TileCachePath(_layer, _zoom, wrappedX, ty), _tileLayer, placeholder: true);
                image.style.left = left;
                image.style.top = top;

                if (wantLabels)
                {
                    string labelKey = $"Labels/{_zoom}/{wrappedX}/{ty}";
                    needed.Add(labelKey);
                    var labelImage = EnsureTileImage(labelKey, LabelTileUrl(_zoom, wrappedX, ty),
                        LabelTileCachePath(_zoom, wrappedX, ty), _labelTileLayer, placeholder: false);
                    labelImage.style.left = left;
                    labelImage.style.top = top;
                }
            }
        }

        // 画面外に出たタイルを外す
        var toRemove = new List<string>();
        foreach (var pair in _tiles)
        {
            if (!needed.Contains(pair.Key)) toRemove.Add(pair.Key);
        }
        foreach (var key in toRemove)
        {
            _tiles[key].RemoveFromHierarchy();
            _tiles.Remove(key);
        }
    }

    /// <summary>タイルのImage要素を用意する (メモリ→ディスク→ネットの順で読む)</summary>
    Image EnsureTileImage(string key, string url, string cachePath, VisualElement parent, bool placeholder)
    {
        if (_tiles.TryGetValue(key, out var image)) return image;

        image = new Image { pickingMode = PickingMode.Ignore };
        image.style.position = Position.Absolute;
        image.style.width = TileSize;
        image.style.height = TileSize;
        if (placeholder)
        {
            // 読み込み中の下地色 (透過タイルには付けない)
            image.style.backgroundColor = new Color(0.9f, 0.9f, 0.88f);
        }
        _tiles[key] = image;
        parent.Add(image);

        if (_textureCache.TryGetValue(key, out var cached))
        {
            image.image = cached;
        }
        else
        {
            // ディスクキャッシュ (タイルは数十KBなので同期読みでよい)。圏外でも保存済みなら表示できる
            var diskTexture = LoadTileFromDisk(cachePath);
            if (diskTexture != null)
            {
                _textureCache[key] = diskTexture;
                image.image = diskTexture;
            }
            else if (_runner != null && !_loading.Contains(key))
            {
                _loading.Add(key);
                _runner.StartCoroutine(LoadTile(key, url, cachePath));
            }
        }
        return image;
    }

    static string TileKey(BaseLayer layer, int z, int x, int y) => $"{layer}/{z}/{x}/{y}";

    /// <summary>ベースタイルのURL (TilePrefetcherの先読みと共用)</summary>
    public static string TileUrl(BaseLayer layer, int z, int x, int y)
    {
        return layer == BaseLayer.Photo
            ? $"https://cyberjapandata.gsi.go.jp/xyz/seamlessphoto/{z}/{x}/{y}.jpg"
            : $"https://cyberjapandata.gsi.go.jp/xyz/std/{z}/{x}/{y}.png";
    }

    /// <summary>透過の地名タイル (白文字で暗い航空写真の上でも読める)。出典表示が必須</summary>
    public static string LabelTileUrl(int z, int x, int y)
    {
        return $"https://basemaps.cartocdn.com/dark_only_labels/{z}/{x}/{y}.png";
    }

    // ---- タイルのディスクキャッシュ (圏外対策。TilePrefetcherの先読みと同じ場所を使う) ----

    /// <summary>ベースタイルのディスクキャッシュ先 (TilePrefetcherと共用)</summary>
    public static string TileCachePath(BaseLayer layer, int z, int x, int y)
    {
        string ext = layer == BaseLayer.Photo ? "jpg" : "png";
        return Path.Combine(Application.persistentDataPath, "tiles", layer.ToString(),
            z.ToString(), x.ToString(), $"{y}.{ext}");
    }

    /// <summary>地名ラベルタイルのディスクキャッシュ先 (TilePrefetcherと共用)</summary>
    public static string LabelTileCachePath(int z, int x, int y)
    {
        return Path.Combine(Application.persistentDataPath, "tiles", "labels",
            z.ToString(), x.ToString(), $"{y}.png");
    }

    /// <summary>ディスクキャッシュから読む。無い・壊れているときはnull (ネットへフォールバック)</summary>
    static Texture2D LoadTileFromDisk(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var texture = new Texture2D(2, 2);
            if (!texture.LoadImage(File.ReadAllBytes(path)))
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }
            return texture;
        }
        catch
        {
            return null; // 読み損ねてもネットから取り直せばよい
        }
    }

    /// <summary>ダウンロードしたタイルをディスクへ保存する (容量不足などで失敗しても表示は続ける)</summary>
    static void SaveTileToDisk(string path, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, bytes);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"タイルの保存に失敗: {e.Message}");
        }
    }

    IEnumerator LoadTile(string key, string url, string cachePath)
    {
        using (var request = UnityWebRequestTexture.GetTexture(url))
        {
            yield return request.SendWebRequest();
            _loading.Remove(key);

            if (request.result == UnityWebRequest.Result.Success)
            {
                var texture = DownloadHandlerTexture.GetContent(request);
                _textureCache[key] = texture;
                if (_tiles.TryGetValue(key, out var image))
                {
                    image.image = texture;
                }
                SaveTileToDisk(cachePath, request.downloadHandler.data);
            }
        }
    }

    // ---- 範囲ポリゴンの描画 ----

    void OnGenerateOverlay(MeshGenerationContext ctx)
    {
        // 描くものが何もない (現在地ドット・点マーカーも含めて) ときだけ抜ける
        if (_polygon.Count == 0 && _displayPolygons.Count == 0 && _forestPolygons.Count == 0
            && _pointMarkers.Count == 0 && !LocationProvider.HasFix) return;

        float w = resolvedStyle.width;
        float h = resolvedStyle.height;
        double topLeftX = _centerX - w / 2.0;
        double topLeftY = _centerY - h / 2.0;

        var painter = ctx.painter2D;

        // 小班の下敷きポリゴン (森林簿の境界。一番下に細い線で淡く描く。浅いズームでは省く)
        if (_zoom >= MinForestZoom)
        {
            foreach (var poly in _forestPolygons)
            {
                if (poly.Points.Count < 3) continue;
                bool selected = poly == _selectedDisplayPolygon;

                painter.BeginPath();
                for (int i = 0; i < poly.Points.Count; i++)
                {
                    var p = poly.Points[i];
                    var sp = new Vector2(
                        (float)(LonToWorldX(p.x, _zoom) - topLeftX),
                        (float)(LatToWorldY(p.y, _zoom) - topLeftY));
                    if (i == 0) painter.MoveTo(sp);
                    else painter.LineTo(sp);
                }
                painter.ClosePath();
                painter.fillColor = new Color(poly.Color.r, poly.Color.g, poly.Color.b, selected ? 0.2f : 0.1f);
                painter.Fill();
                painter.strokeColor = new Color(poly.Color.r, poly.Color.g, poly.Color.b, selected ? 1f : 0.8f);
                painter.lineWidth = selected ? 2.5f : 1.5f;
                painter.Stroke();
            }
        }

        // 表示専用ポリゴン (記録ページなどの色分け表示。頂点マーカーは付けない)
        foreach (var poly in _displayPolygons)
        {
            if (poly.Points.Count < 3) continue;
            bool selected = poly == _selectedDisplayPolygon;

            painter.BeginPath();
            for (int i = 0; i < poly.Points.Count; i++)
            {
                var p = poly.Points[i];
                var sp = new Vector2(
                    (float)(LonToWorldX(p.x, _zoom) - topLeftX),
                    (float)(LatToWorldY(p.y, _zoom) - topLeftY));
                if (i == 0) painter.MoveTo(sp);
                else painter.LineTo(sp);
            }
            painter.ClosePath();
            painter.fillColor = new Color(poly.Color.r, poly.Color.g, poly.Color.b, selected ? 0.45f : 0.3f);
            painter.Fill();
            painter.strokeColor = poly.Color;
            painter.lineWidth = selected ? 5f : 3f;
            painter.Stroke();
        }

        if (_polygon.Count == 0) return;

        var screenPoints = new List<Vector2>(_polygon.Count);
        foreach (var p in _polygon)
        {
            float sx = (float)(LonToWorldX(p.x, _zoom) - topLeftX);
            float sy = (float)(LatToWorldY(p.y, _zoom) - topLeftY);
            screenPoints.Add(new Vector2(sx, sy));
        }

        var green = new Color(27f / 255f, 152f / 255f, 60f / 255f);

        if (screenPoints.Count >= 2)
        {
            painter.BeginPath();
            painter.MoveTo(screenPoints[0]);
            for (int i = 1; i < screenPoints.Count; i++)
            {
                painter.LineTo(screenPoints[i]);
            }
            if (screenPoints.Count >= 3)
            {
                painter.ClosePath();
                painter.fillColor = new Color(green.r, green.g, green.b, 0.25f);
                painter.Fill();
            }
            painter.strokeColor = green;
            painter.lineWidth = 3f;
            painter.Stroke();
        }

        // 頂点マーカー (白丸+緑枠)。長押しドラッグ中の頂点は大きく表示
        for (int i = 0; i < screenPoints.Count; i++)
        {
            bool dragging = i == _draggingVertexIndex;
            painter.BeginPath();
            painter.Arc(screenPoints[i], dragging ? 12f : 7f, 0f, 360f);
            painter.fillColor = dragging ? new Color(0.92f, 1f, 0.94f) : Color.white;
            painter.Fill();
            painter.strokeColor = green;
            painter.lineWidth = dragging ? 4f : 3f;
            painter.Stroke();
        }

        // 点マーカー (選木結果など)。白フチ+塗りの小さな丸
        foreach (var (lonLat, color) in _pointMarkers)
        {
            var sp = new Vector2(
                (float)(LonToWorldX(lonLat.x, _zoom) - topLeftX),
                (float)(LatToWorldY(lonLat.y, _zoom) - topLeftY));
            if (sp.x < -10f || sp.x > w + 10f || sp.y < -10f || sp.y > h + 10f) continue;
            painter.BeginPath();
            painter.Arc(sp, 5.5f, 0f, 360f);
            painter.fillColor = Color.white;
            painter.Fill();
            painter.BeginPath();
            painter.Arc(sp, 4f, 0f, 360f);
            painter.fillColor = color;
            painter.Fill();
        }

        // 現在地の青ドット (一番上に描く。自分がどの位置にいるかを見るため)
        if (LocationProvider.HasFix)
        {
            var dot = new Vector2(
                (float)(LonToWorldX(LocationProvider.Longitude, _zoom) - topLeftX),
                (float)(LatToWorldY(LocationProvider.Latitude, _zoom) - topLeftY));
            // 画面のすこし外までは描く (端で欠けても自然に見えるように)
            if (dot.x > -24f && dot.x < w + 24f && dot.y > -24f && dot.y < h + 24f)
            {
                var blue = new Color(0.20f, 0.47f, 0.96f);
                // 精度の雰囲気を伝える淡い円 → 白フチ → 青ドット の順に重ねる (Googleマップと同じ見た目)
                painter.BeginPath();
                painter.Arc(dot, 22f, 0f, 360f);
                painter.fillColor = new Color(blue.r, blue.g, blue.b, 0.15f);
                painter.Fill();

                painter.BeginPath();
                painter.Arc(dot, 9f, 0f, 360f);
                painter.fillColor = Color.white;
                painter.Fill();

                painter.BeginPath();
                painter.Arc(dot, 6.5f, 0f, 360f);
                painter.fillColor = blue;
                painter.Fill();
            }
        }
    }

    // ---- Webメルカトル座標変換 ----

    static double LonToWorldX(double lon, int zoom)
    {
        return (lon + 180.0) / 360.0 * TileSize * (1 << zoom);
    }

    static double LatToWorldY(double lat, int zoom)
    {
        double rad = lat * Math.PI / 180.0;
        double n = Math.Log(Math.Tan(rad) + 1.0 / Math.Cos(rad));
        return (1.0 - n / Math.PI) / 2.0 * TileSize * (1 << zoom);
    }

    static double WorldXToLon(double x, int zoom)
    {
        return x / (TileSize * (double)(1 << zoom)) * 360.0 - 180.0;
    }

    static double WorldYToLat(double y, int zoom)
    {
        double n = Math.PI - 2.0 * Math.PI * y / (TileSize * (double)(1 << zoom));
        return 180.0 / Math.PI * Math.Atan(0.5 * (Math.Exp(n) - Math.Exp(-n)));
    }
}
