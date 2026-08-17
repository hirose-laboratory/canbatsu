using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// 記録ページのController。
/// 作業計画の範囲(緑)と過去の作業記録の範囲(オレンジ)を地図上に色分け表示し、
/// 範囲をタップすると詳細ポップアップ (Googleマップの場所カード風) を出す。
/// TODO: 作業記録は今はRecordStoreのモックデータ。バックエンド完成後にSQLite同期へ差し替え
/// </summary>
public class RecordPageController
{
    static readonly Color PlanColor = new Color(27f / 255f, 152f / 255f, 60f / 255f);
    static readonly Color RecordColor = new Color(240f / 255f, 140f / 255f, 0f);

    readonly VisualElement _page;
    readonly MapView _map;
    readonly Image _layerThumbImage;
    readonly Label _layerThumbLabel;
    readonly Button _layerButton;
    readonly VisualElement _infoCard;
    readonly Label _infoBadge;
    readonly Label _infoTitle;
    readonly VisualElement _infoRows;
    readonly Button _infoDeleteButton;
    readonly VisualElement _deleteDialog;

    WorkRecord _selectedRecord; // 詳細ポップアップで表示中の作業記録 (削除対象)

    public RecordPageController(VisualElement page, AppRouter router)
    {
        _page = page;
        // 表示専用の地図 (タップしても頂点は増えない)
        _map = new MapView(router) { AllowPointAdding = false };
        _map.SetCenter(34.3766, 135.9058, 14);
        page.Q<VisualElement>("map-container").Add(_map);
        _map.DisplayPolygonClicked += OnPolygonClicked;

        _layerThumbImage = page.Q<Image>("layer-thumb-image");
        _layerThumbLabel = page.Q<Label>("layer-thumb-label");
        _layerButton = page.Q<Button>("layer-button");
        _infoCard = page.Q<VisualElement>("info-card");
        _infoBadge = page.Q<Label>("info-badge");
        _infoTitle = page.Q<Label>("info-title");
        _infoRows = page.Q<VisualElement>("info-rows");
        _infoDeleteButton = page.Q<Button>("info-delete-button");
        _deleteDialog = page.Q<VisualElement>("delete-dialog");

        page.Q<Button>("zoom-in-button").clicked += _map.ZoomIn;
        page.Q<Button>("zoom-out-button").clicked += _map.ZoomOut;
        _layerButton.clicked += ToggleLayer;
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

    /// <summary>予定と記録の範囲を地図に載せ直し、全体が見える位置に移動する</summary>
    public void Refresh()
    {
        var polygons = new List<MapView.DisplayPolygon>();
        int planCount = 0;

        foreach (var plan in PlanStore.Plans)
        {
            if (plan.RangePoints == null || plan.RangePoints.Count < 3) continue;
            // 作業済みの計画は記録(オレンジ)側で見せるので、予定(緑)としては描かない
            if (RecordStore.HasRecordForPlan(plan.Id)) continue;
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
        _map.FitToDisplayPolygons();
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
            ShowInfo(
                isPlan: true,
                title: $"{plan.Date.Month}/{plan.Date.Day} の作業予定",
                rows: new[]
                {
                    ("樹種", plan.Species ?? "-"),
                    ("間伐率", $"{plan.ThinningRatePercent}%"),
                    ("伐採基準 (直径)", $"{plan.FellingStandardCm}cm未満"),
                    ("しきい値 (伐採間隔)", $"{plan.FellingIntervalM:0.0}m"),
                    ("対象面積", $"{plan.AreaHa:0.0#}ha"),
                });
        }
        else if (polygon.UserData is WorkRecord record)
        {
            _selectedRecord = record;
            ShowInfo(
                isPlan: false,
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

    void ShowInfo(bool isPlan, string title, (string key, string value)[] rows)
    {
        // 削除できるのは作業記録だけ
        _infoDeleteButton.style.display = isPlan ? DisplayStyle.None : DisplayStyle.Flex;

        _infoBadge.text = isPlan ? "予定" : "記録";
        _infoBadge.EnableInClassList("record__info-badge--plan", isPlan);
        _infoBadge.EnableInClassList("record__info-badge--record", !isPlan);
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
        _map.ClearDisplaySelection();
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
