using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// 記録ページのController。
/// 作業計画の範囲(緑)と過去の作業記録の範囲(オレンジ)を地図上に色分け表示し、
/// 範囲をタップすると詳細ポップアップ (Googleマップの場所カード風) を出す。
/// 下敷きとして森林簿の小班境界を樹種色で表示し、タップで小班の情報も見られる。
/// TODO: 作業記録は今はRecordStoreのモックデータ。バックエンド完成後にSQLite同期へ差し替え
/// </summary>
public class RecordPageController
{
    static readonly Color PlanColor = new Color(27f / 255f, 152f / 255f, 60f / 255f);
    static readonly Color RecordColor = new Color(240f / 255f, 140f / 255f, 0f);

    /// <summary>info-cardの種別 (バッジ文言と削除ボタンの表示を切り替える)</summary>
    enum InfoKind { Plan, Record }

    readonly VisualElement _page;
    readonly MapView _map;
    readonly Image _layerThumbImage;
    readonly Label _layerThumbLabel;
    readonly Button _layerButton;
    readonly Button _locateButton;
    readonly VisualElement _infoCard;
    readonly Label _infoBadge;
    readonly Label _infoTitle;
    readonly VisualElement _infoRows;
    readonly Button _infoDeleteButton;
    readonly VisualElement _deleteDialog;

    WorkRecord _selectedRecord; // 詳細ポップアップで表示中の作業記録 (削除対象)
    string _shownTreesPlanId;   // 選木マーカーを表示中の計画 (非同期読込の取り違え防止)

    // 現在地への自動センタリング (最初のFixが来るまで、ユーザーが地図を触るまで)
    bool _userMovedMap;
    bool _autoCentered;
    bool _skipNextFit; // 現在地を初期表示にしたとき、直後の全体フィットで上書きしないためのフラグ

    public RecordPageController(VisualElement page, AppRouter router)
    {
        _page = page;

        // 現在地の取得を開始する (権限拒否やエディタではHasFix=falseのままフォールバックが効く)
        var location = router.GetComponent<LocationProvider>();
        if (location == null) location = router.gameObject.AddComponent<LocationProvider>();
        location.StartUpdates();

        // 表示専用の地図 (タップしても頂点は増えない)。Fix済みなら現在地を初期表示にする
        _map = new MapView(router) { AllowPointAdding = false };
        if (LocationProvider.HasFix)
        {
            _map.SetCenter(LocationProvider.Latitude, LocationProvider.Longitude, 14);
            _autoCentered = true;
            _skipNextFit = true;
        }
        else
        {
            _map.SetCenter(34.3766, 135.9058, 14);
        }
        page.Q<VisualElement>("map-container").Add(_map);
        _map.DisplayPolygonClicked += OnPolygonClicked;
        _map.UserInteracted += () => _userMovedMap = true;

        // 最初のFixが「まだ地図を触っていない間」に来たら一度だけ現在地へ寄せる
        if (!_autoCentered)
        {
            page.schedule.Execute(() =>
            {
                if (_autoCentered || _userMovedMap || !LocationProvider.HasFix) return;
                _autoCentered = true;
                _map.SetCenter(LocationProvider.Latitude, LocationProvider.Longitude, 14);
            }).Every(1000).Until(() => _autoCentered || _userMovedMap);
        }

        _layerThumbImage = page.Q<Image>("layer-thumb-image");
        _layerThumbLabel = page.Q<Label>("layer-thumb-label");
        _layerButton = page.Q<Button>("layer-button");
        _locateButton = page.Q<Button>("locate-button");
        _infoCard = page.Q<VisualElement>("info-card");
        _infoBadge = page.Q<Label>("info-badge");
        _infoTitle = page.Q<Label>("info-title");
        _infoRows = page.Q<VisualElement>("info-rows");
        _infoDeleteButton = page.Q<Button>("info-delete-button");
        _deleteDialog = page.Q<VisualElement>("delete-dialog");

        page.Q<Button>("zoom-in-button").clicked += _map.ZoomIn;
        page.Q<Button>("zoom-out-button").clicked += _map.ZoomOut;
        _layerButton.clicked += ToggleLayer;
        _locateButton.clicked += OnLocateClicked;
        page.Q<Button>("info-close-button").clicked += HideInfo;

        // 記録の削除フロー (削除ボタン → 確認ダイアログ → 削除)
        _infoDeleteButton.clicked += ShowDeleteDialog;
        page.Q<Button>("delete-cancel-button").clicked += HideDeleteDialog;
        page.Q<Button>("delete-confirm-button").clicked += DeleteSelectedRecord;
        _deleteDialog.RegisterCallback<ClickEvent>(evt =>
        {
            if (evt.target == _deleteDialog) HideDeleteDialog();
        });

        Refresh();
        UpdateLayerButton();
    }

    /// <summary>現在地ボタン: Fixがあれば現在地へ移動、無ければ一瞬無効化して取得中であることを伝える</summary>
    void OnLocateClicked()
    {
        if (LocationProvider.HasFix)
        {
            _userMovedMap = true; // 自分で現在地へ動かしたので以後の自動センタリングは不要
            _map.SetCenter(LocationProvider.Latitude, LocationProvider.Longitude, _map.CurrentZoom);
        }
        else
        {
            _locateButton.SetEnabled(false);
            _page.schedule.Execute(() => _locateButton.SetEnabled(true)).StartingIn(1200);
        }
    }

    /// <summary>予定と記録の範囲を地図に載せ直し、全体が見える位置に移動する</summary>
    public void Refresh()
    {
        var polygons = new List<MapView.DisplayPolygon>();
        int planCount = 0;

        foreach (var plan in PlanStore.Plans)
        {
            if (plan.RangePoints == null || plan.RangePoints.Count < 3) continue;
            // 完了した計画は記録(オレンジ)側で見せるので、予定(緑)としては描かない
            // (記録が付いていても進行中の計画は使い回すので緑のまま出し続ける)
            if (plan.IsCompleted) continue;
            planCount++;
            polygons.Add(new MapView.DisplayPolygon
            {
                Points = plan.RangePoints,
                Color = PlanColor,
                UserData = plan,
            });
        }

        // 凡例に件数も出す
        _page.Q<Label>("legend-plan-label").text = $"予定 ({planCount}件)";
        _page.Q<Label>("legend-record-label").text = $"作業記録 ({RecordStore.Records.Count}件)";

        foreach (var record in RecordStore.Records)
        {
            if (record.RangePoints == null || record.RangePoints.Count < 3) continue;
            polygons.Add(new MapView.DisplayPolygon
            {
                Points = record.RangePoints,
                Color = RecordColor,
                UserData = record,
            });
        }

        _map.SetDisplayPolygons(polygons);
        if (_skipNextFit)
        {
            _skipNextFit = false; // 現在地を初期表示にしているのでフィットで上書きしない
        }
        else
        {
            _map.FitToDisplayPolygons();
        }
    }

    // ---- 範囲タップの詳細ポップアップ ----

    void OnPolygonClicked(MapView.DisplayPolygon polygon)
    {
        if (polygon == null)
        {
            // 何もない場所のタップで閉じる (Googleマップと同じ)
            HideInfo();
            return;
        }

        if (polygon.UserData is WorkPlan plan)
        {
            _selectedRecord = null;
            // 計画の使い回し対応: この計画で何回作業したかも出す
            int recordCount = RecordStore.GetByPlan(plan.Id).Count;
            ShowInfo(
                kind: InfoKind.Plan,
                title: $"{plan.Date.Month}/{plan.Date.Day} の作業予定",
                rows: new[]
                {
                    ("樹種", plan.Species ?? "-"),
                    ("間伐率", $"{plan.ThinningRatePercent}%"),
                    ("伐採基準 (直径)", $"{plan.FellingStandardCm}cm未満"),
                    ("しきい値 (伐採間隔)", $"{plan.FellingIntervalM:0.0}m"),
                    ("対象面積", $"{plan.AreaHa:0.0#}ha"),
                    ("記録", recordCount > 0 ? $"{recordCount}回" : "まだ"),
                });
            // 選木結果 (ARで選んだ木) があれば赤い点で地図に載せる (紙地図でいう1/2500の確認用途)
            _ = ShowSelectedTreesAsync(plan.Id);
        }
        else if (polygon.UserData is WorkRecord record)
        {
            _selectedRecord = record;
            ShowInfo(
                kind: InfoKind.Record,
                title: $"{record.Date.Month}/{record.Date.Day} の作業記録",
                rows: new[]
                {
                    // 伐採本数はARグラス連携までは記録されない (0は未計測として扱う)
                    ("伐採本数", record.FelledCount > 0 ? $"{record.FelledCount}本" : "-"),
                    ("間伐率", $"{record.ThinningRatePercent}%"),
                    ("作業面積", $"{record.AreaHa:0.0#}ha"),
                });
        }
    }

    void ShowInfo(InfoKind kind, string title, (string key, string value)[] rows)
    {
        // 削除できるのは作業記録だけ
        _infoDeleteButton.style.display = kind == InfoKind.Record ? DisplayStyle.Flex : DisplayStyle.None;

        _infoBadge.text = kind == InfoKind.Plan ? "予定" : "記録";
        _infoBadge.EnableInClassList("record__info-badge--plan", kind == InfoKind.Plan);
        _infoBadge.EnableInClassList("record__info-badge--record", kind == InfoKind.Record);
        _infoTitle.text = title;

        _infoRows.Clear();
        foreach (var (key, value) in rows)
        {
            var row = new VisualElement();
            row.AddToClassList("record__info-row");

            var keyLabel = new Label(key);
            keyLabel.AddToClassList("record__info-key");
            row.Add(keyLabel);

            var valueLabel = new Label(value);
            valueLabel.AddToClassList("record__info-value");
            row.Add(valueLabel);

            _infoRows.Add(row);
        }

        _infoCard.style.display = DisplayStyle.Flex;
        _layerButton.style.display = DisplayStyle.None; // カードと重なるので隠す
    }

    void HideInfo()
    {
        _infoCard.style.display = DisplayStyle.None;
        _layerButton.style.display = DisplayStyle.Flex;
        _selectedRecord = null;
        _shownTreesPlanId = null;
        _map.ClearPointMarkers();
        _map.ClearDisplaySelection();
    }

    /// <summary>
    /// 計画の選木結果 (armap) を読み、基準点のGPS+方位で緯度経度に変換して赤い点で表示する。
    /// 基準点の地理情報が無い計画 (GPS圏外・古いデータ) では何も出さない。
    /// 精度の目安: GPS±10m+コンパス±10〜30°なので「どのあたりを選んだか」の確認用
    /// (1本単位の正確な位置はARの再訪復元側が担当)。
    /// </summary>
    async Task ShowSelectedTreesAsync(string planId)
    {
        _shownTreesPlanId = planId;
        _map.ClearPointMarkers();

        PlanTreeStore.PlanTreeMap treeMap;
        try
        {
            treeMap = await PlanTreeStore.LoadAsync(planId);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"選木マップの読み込みに失敗: {e.Message}");
            return;
        }
        // 読み込み中に別の計画/閉じる操作をしていたら捨てる
        if (_shownTreesPlanId != planId) return;
        if (treeMap.Geo == null || !treeMap.Geo.HasHeading) return;

        double latRad = treeMap.Geo.Lat * Math.PI / 180.0;
        double theta = treeMap.Geo.HeadingDeg * Math.PI / 180.0; // マップ+Zの真北からの方位 (時計回り)
        var markers = new List<(Vector2 lonLat, Color color)>();
        var red = new Color(0.9f, 0.24f, 0.2f); // ARの選木マーカーと同じ赤
        foreach (var tree in treeMap.Trees)
        {
            if (!tree.Selected) continue;
            // マップ座標 (基準点原点・+Z=基準方向) → 北/東のメートルオフセット → 緯度経度
            double north = tree.MapPos.z * Math.Cos(theta) - tree.MapPos.x * Math.Sin(theta);
            double east = tree.MapPos.z * Math.Sin(theta) + tree.MapPos.x * Math.Cos(theta);
            double lat = treeMap.Geo.Lat + north / 111320.0;
            double lon = treeMap.Geo.Lon + east / (111320.0 * Math.Cos(latRad));
            markers.Add((new Vector2((float)lon, (float)lat), red));
        }
        if (markers.Count > 0) _map.SetPointMarkers(markers);
    }

    // ---- 記録の削除 ----

    void ShowDeleteDialog()
    {
        if (_selectedRecord == null) return;
        _deleteDialog.style.display = DisplayStyle.Flex;
    }

    void HideDeleteDialog() => _deleteDialog.style.display = DisplayStyle.None;

    void DeleteSelectedRecord()
    {
        if (_selectedRecord == null) return;

        RecordStore.Remove(_selectedRecord);
        HideDeleteDialog();
        HideInfo();
        Refresh();
    }

    // ---- レイヤー切替 ----

    void ToggleLayer()
    {
        _map.SetLayer(_map.CurrentLayer == MapView.BaseLayer.Photo
            ? MapView.BaseLayer.Standard
            : MapView.BaseLayer.Photo);
        UpdateLayerButton();
    }

    /// <summary>ボタンに「次に切り替わるレイヤー」のプレビューと名前を表示 (Googleマップと同じ)</summary>
    void UpdateLayerButton()
    {
        var next = _map.CurrentLayer == MapView.BaseLayer.Photo
            ? MapView.BaseLayer.Standard
            : MapView.BaseLayer.Photo;
        _layerThumbLabel.text = next == MapView.BaseLayer.Photo ? "航空写真" : "地図";
        _map.LoadLayerThumbnail(next, _layerThumbImage);
    }
}
