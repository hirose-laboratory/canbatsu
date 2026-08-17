using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// 作業計画の新規作成ページのController。
/// 地図で範囲を選択 → 確定で森林簿 (ForestRegistry) から樹種・林齢を取得し、
/// 間伐率・伐採間隔・伐採基準を自動算出 (ThinningCalculator) してフォームに表示 → 手動修正もできる → 保存。
/// 森林簿データが無い範囲 (三重県の国有林以外) はモック値のまま。
/// 既存計画を渡すと編集モードになり、範囲を再設定して同じ計画に上書き保存する。
/// TODO: 算出式は簡易版。バックエンドの機械学習による本算出に置き換える
/// </summary>
public class PlanCreatePageController
{
    readonly VisualElement _page;
    readonly AppRouter _router;
    readonly MapView _map;
    readonly WorkPlan _editingPlan; // nullなら新規作成、あれば範囲の再設定 (編集モード)

    readonly Button _undoButton;
    readonly Button _confirmButton;
    readonly Button _layerButton;
    readonly Image _layerThumbImage;
    readonly Label _layerThumbLabel;
    readonly VisualElement _resultSheet;
    readonly Label _resultCaption;
    readonly PlanFormView _form;

    double _areaHa; // 面積はフォームではなくポリゴンから計算する
    int _fetchVersion; // 範囲を確定し直したとき、前の森林簿検索の結果を捨てるための番号

    public PlanCreatePageController(VisualElement page, AppRouter router, WorkPlan editingPlan = null)
    {
        _page = page;
        _router = router;
        _editingPlan = editingPlan;

        // 地図を生成して差し込む (吉野杉で有名な奈良県吉野の山地を初期表示)
        _map = new MapView(router);
        _map.SetCenter(34.3766, 135.9058, 15);
        page.Q<VisualElement>("map-container").Add(_map);

        // 編集モード: 既存の範囲を載せて、その範囲が見える位置へ移動する
        if (_editingPlan != null && _editingPlan.RangePoints != null && _editingPlan.RangePoints.Count >= 3)
        {
            _map.SetEditingPoints(_editingPlan.RangePoints);
            _map.FitToPoints(_editingPlan.RangePoints);
        }
        _map.PolygonChanged += OnPolygonChanged;

        _undoButton = page.Q<Button>("undo-button");
        _confirmButton = page.Q<Button>("confirm-button");
        _layerButton = page.Q<Button>("layer-button");
        _layerThumbImage = page.Q<Image>("layer-thumb-image");
        _layerThumbLabel = page.Q<Label>("layer-thumb-label");
        _resultSheet = page.Q<VisualElement>("result-sheet");
        _resultCaption = page.Q<Label>("result-caption");
        _form = new PlanFormView(page.Q<VisualElement>("plan-form"), page);

        page.Q<Button>("back-button").clicked += _router.NavigateToHome;
        page.Q<Button>("zoom-in-button").clicked += _map.ZoomIn;
        page.Q<Button>("zoom-out-button").clicked += _map.ZoomOut;
        _layerButton.clicked += ToggleLayer;
        _undoButton.clicked += _map.RemoveLastPoint;
        _confirmButton.clicked += ConfirmSelection;
        page.Q<Button>("cancel-button").clicked += ReopenSelection;
        page.Q<Button>("save-button").clicked += SavePlan;

        if (_editingPlan != null)
        {
            _confirmButton.text = "この範囲で計画を更新";
        }

        UpdateLayerButton();
        OnPolygonChanged();
    }

    /// <summary>航空写真 ⇔ 標準地図 の切替</summary>
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

    void OnPolygonChanged()
    {
        int count = _map.PointCount;
        _undoButton.style.display = count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        _confirmButton.style.display = count >= 3 ? DisplayStyle.Flex : DisplayStyle.None;

        var sub = _page.Q<Label>("instruction-sub");
        sub.text = count == 0
            ? "頂点を3つ以上置くと確定できます"
            : $"頂点: {count}個 ・ 長押しで頂点を移動できます";
    }

    /// <summary>範囲確定 → フォームに初期値を表示し、森林簿データを取りに行く (手動修正できる)</summary>
    void ConfirmSelection()
    {
        _areaHa = _map.AreaHa();

        if (_editingPlan != null)
        {
            // 編集モード: 既存計画の値を初期値にする (面積だけ新しい範囲から計算し直す)
            _form.SetValues(_editingPlan.Species, _editingPlan.ThinningRatePercent,
                _editingPlan.FellingStandardCm, _editingPlan.FellingIntervalM,
                _areaHa, _editingPlan.Date);
            _resultCaption.text = "既存計画の値です。手動で修正できます";
        }
        else
        {
            // まず仮の初期値を出し、裏で森林簿を検索して届いたら差し替える
            _form.SetValues(
                species: "スギ",
                ratePercent: _areaHa < 1.0 ? 20 : 30,
                standardCm: 18,
                intervalM: 3.0f,
                areaHa: _areaHa,
                date: DateTime.Today);
            _resultCaption.text = "森林簿データを取得中...";
            _ = FetchForestDataAsync(new List<Vector2>(_map.EditingPoints), ++_fetchVersion);
        }

        _map.AllowPointAdding = false;
        _resultSheet.style.display = DisplayStyle.Flex;
        _confirmButton.style.display = DisplayStyle.None;
        _undoButton.style.display = DisplayStyle.None;
    }

    /// <summary>
    /// 森林簿 (forest_registry) から選択範囲に重なる小班を取り、
    /// 樹種・林齢と、林齢から算出した間伐率・伐採基準・伐採間隔をフォームに反映する
    /// </summary>
    async Task FetchForestDataAsync(List<Vector2> selection, int version)
    {
        List<ForestRegistry.ForestPatch> patches = null;
        try
        {
            patches = await ForestRegistry.QueryAsync(selection);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"森林簿の取得に失敗: {e.Message}");
        }

        // 「範囲を修正」して確定し直していたら、この結果はもう古いので捨てる
        if (version != _fetchVersion) return;

        if (patches == null || patches.Count == 0)
        {
            _resultCaption.text = "この範囲の森林簿データはありません (仮の推定値)。手動で修正できます";
            return;
        }

        var main = ForestRegistry.PickDominant(patches, selection);
        int age = main.Age1;
        int rate = ThinningCalculator.RecommendRatePercent(age);

        _form.SetValues(
            species: ForestRegistry.ToAppSpecies(main.Species1),
            ratePercent: rate,
            standardCm: ThinningCalculator.DiameterThresholdCm(age),
            intervalM: ThinningCalculator.SpacingAfterThinningM(age, rate),
            areaHa: _areaHa,
            date: _form.Date); // 選択済みの実施日は変えない

        _resultCaption.text =
            $"森林簿から推定: {main.Name} ({main.Species1} {age}年生・小班{patches.Count}件)。手動で修正できます";
    }

    /// <summary>「範囲を修正」でシートを閉じて選択に戻る</summary>
    void ReopenSelection()
    {
        _fetchVersion++; // 取得中の森林簿検索があれば結果を捨てる
        _map.AllowPointAdding = true;
        _resultSheet.style.display = DisplayStyle.None;
        OnPolygonChanged();
    }

    void SavePlan()
    {
        if (_editingPlan != null)
        {
            // 編集モード: 既存計画を上書きする
            _editingPlan.Date = _form.Date;
            _editingPlan.Species = _form.Species;
            _editingPlan.ThinningRatePercent = _form.RatePercent;
            _editingPlan.AreaHa = (float)_areaHa;
            _editingPlan.FellingIntervalM = _form.IntervalM;
            _editingPlan.FellingStandardCm = _form.StandardCm;
            _editingPlan.RangePoints = new List<UnityEngine.Vector2>(_map.EditingPoints);
            PlanStore.Update(_editingPlan);
        }
        else
        {
            PlanStore.Add(new WorkPlan
            {
                Date = _form.Date,
                Species = _form.Species,
                ThinningRatePercent = _form.RatePercent,
                AreaHa = (float)_areaHa,
                FellingIntervalM = _form.IntervalM,
                FellingStandardCm = _form.StandardCm,
                RangePoints = new List<UnityEngine.Vector2>(_map.EditingPoints),
            });
        }

        _router.NavigateToHome();
    }
}
