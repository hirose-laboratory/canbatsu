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
    readonly Button _locateButton;
    readonly Image _layerThumbImage;
    readonly Label _layerThumbLabel;
    readonly VisualElement _resultSheet;
    readonly Label _resultCaption;
    readonly PlanFormView _form;

    // 測量済み区画 (GPX)。表示は青緑の下敷き (計画の緑・記録のオレンジと被らない色)
    List<GpxImporter.GpxBoundary> _gpxBoundaries = new List<GpxImporter.GpxBoundary>();
    static readonly Color GpxParcelColor = new Color(0f, 0.42f, 0.55f);

    double _areaHa; // 面積はフォームではなくポリゴンから計算する
    int _fetchVersion; // 範囲を確定し直したとき、前の森林簿検索の結果を捨てるための番号

    // 現在地への自動センタリング (最初のFixが来るまで、ユーザーが地図を触るまで)
    bool _userMovedMap;
    bool _autoCentered;

    public PlanCreatePageController(VisualElement page, AppRouter router, WorkPlan editingPlan = null)
    {
        _page = page;
        _router = router;
        _editingPlan = editingPlan;

        // 現在地の取得を開始する (権限拒否やエディタではHasFix=falseのままフォールバックが効く)
        var location = router.GetComponent<LocationProvider>();
        if (location == null) location = router.gameObject.AddComponent<LocationProvider>();
        location.StartUpdates();

        _map = new MapView(router);

        // 測量済みの所有区画 (GPX) を小班として下敷き表示する。
        // 方針: GPXは区画の「表示」にだけ使い、間伐範囲はこの区画を見ながら指で描いて決める
        // (森林簿は境界表示には使わず、樹種・林齢などの属性データだけに使う)
        _gpxBoundaries = GpxImporter.LoadAll();
        if (_gpxBoundaries.Count > 0)
        {
            var underlay = new List<MapView.DisplayPolygon>();
            foreach (var b in _gpxBoundaries)
            {
                underlay.Add(new MapView.DisplayPolygon
                {
                    Points = b.Points,
                    Color = GpxParcelColor,
                    UserData = b,
                });
            }
            _map.SetForestPolygons(underlay);
        }
        else
        {
            Debug.Log($"GPX区画なし (置き場所: {GpxImporter.FolderPath})");
        }

        // 初期表示: Fix済みなら現在地 > 区画 (GPX) があればそこ > 吉野の山地 (吉野杉で有名)
        if (LocationProvider.HasFix)
        {
            _map.SetCenter(LocationProvider.Latitude, LocationProvider.Longitude, 15);
            _autoCentered = true;
        }
        else if (_gpxBoundaries.Count > 0)
        {
            var all = new List<Vector2>();
            foreach (var b in _gpxBoundaries) all.AddRange(b.Points);
            _map.FitToPoints(all);
        }
        else
        {
            _map.SetCenter(34.3766, 135.9058, 15);
        }
        page.Q<VisualElement>("map-container").Add(_map);

        // 編集モード: 既存の範囲を載せて、その範囲が見える位置へ移動する
        if (_editingPlan != null && _editingPlan.RangePoints != null && _editingPlan.RangePoints.Count >= 3)
        {
            _map.SetEditingPoints(_editingPlan.RangePoints);
            _map.FitToPoints(_editingPlan.RangePoints);
        }
        _map.PolygonChanged += OnPolygonChanged;
        _map.UserInteracted += () => _userMovedMap = true;

        // 最初のFixが「まだ地図を触っていない間」に来たら一度だけ現在地へ寄せる
        // (編集モードは既存範囲、区画があるときは区画を見せたままにするので寄せない。現在地ボタンはいつでも使える)
        if (!_autoCentered && _editingPlan == null && _gpxBoundaries.Count == 0)
        {
            page.schedule.Execute(() =>
            {
                if (_autoCentered || _userMovedMap || !LocationProvider.HasFix) return;
                _autoCentered = true;
                _map.SetCenter(LocationProvider.Latitude, LocationProvider.Longitude, 15);
            }).Every(1000).Until(() => _autoCentered || _userMovedMap);
        }

        _undoButton = page.Q<Button>("undo-button");
        _confirmButton = page.Q<Button>("confirm-button");
        _layerButton = page.Q<Button>("layer-button");
        _locateButton = page.Q<Button>("locate-button");
        _layerThumbImage = page.Q<Image>("layer-thumb-image");
        _layerThumbLabel = page.Q<Label>("layer-thumb-label");
        _resultSheet = page.Q<VisualElement>("result-sheet");
        _resultCaption = page.Q<Label>("result-caption");
        _form = new PlanFormView(page.Q<VisualElement>("plan-form"), page);

        page.Q<Button>("back-button").clicked += _router.NavigateToHome;
        page.Q<Button>("zoom-in-button").clicked += _map.ZoomIn;
        page.Q<Button>("zoom-out-button").clicked += _map.ZoomOut;
        _layerButton.clicked += ToggleLayer;
        _locateButton.clicked += OnLocateClicked;
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
        // 壊れた座標 (NaNなど) の頂点は保存しない
        var rangePoints = new List<UnityEngine.Vector2>();
        foreach (var point in _map.EditingPoints)
        {
            if (PlanStore.IsValidLonLat(point)) rangePoints.Add(point);
        }
        float areaHa = double.IsNaN(_areaHa) ? 0f : (float)_areaHa;

        try
        {
            if (_editingPlan != null)
            {
                // 編集モード: 既存計画を上書きする
                _editingPlan.Date = _form.Date;
                _editingPlan.Species = _form.Species;
                _editingPlan.ThinningRatePercent = _form.RatePercent;
                _editingPlan.AreaHa = areaHa;
                _editingPlan.FellingIntervalM = _form.IntervalM;
                _editingPlan.FellingStandardCm = _form.StandardCm;
                _editingPlan.RangePoints = rangePoints;
                PlanStore.Update(_editingPlan);
            }
            else
            {
                PlanStore.Add(new WorkPlan
                {
                    Date = _form.Date,
                    Species = _form.Species,
                    ThinningRatePercent = _form.RatePercent,
                    AreaHa = areaHa,
                    FellingIntervalM = _form.IntervalM,
                    FellingStandardCm = _form.StandardCm,
                    RangePoints = rangePoints,
                });
            }

            // 計画範囲の地図タイルを先読みする (山中の圏外でも地図が出るように。_routerは遷移後も生きている)
            TilePrefetcher.Prefetch(_router, rangePoints);
        }
        catch (Exception e)
        {
            // 途中で例外が出ても、下の予定ページへの移動まで止めない (止まると保存ボタンが無反応に見える)
            Debug.LogException(e);
        }

        // 保存したら必ず予定ページへ戻る
        _router.NavigateToHome();
    }
}
