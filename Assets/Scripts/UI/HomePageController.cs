using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine.UIElements;

/// <summary>
/// 予定ページのController。
/// 計画が無ければ「新規作成」の案内、あれば「今日 / 今後の作業計画」の一覧を表示する。
/// 今日の計画カードをタップ → 作業開始の確認ダイアログ → ARグラス連携モード(作業中ページ)へ。
/// </summary>
public class HomePageController
{
    readonly VisualElement _page;
    readonly AppRouter _router;
    readonly VisualElement _startDialog;
    readonly VisualElement _editDialog;
    readonly PlanFormView _editForm;

    WorkPlan _dialogPlan;  // 作業開始ダイアログで表示中の計画
    WorkPlan _editingPlan; // 編集ダイアログで表示中の計画

    public HomePageController(VisualElement page, AppRouter router)
    {
        _page = page;
        _router = router;
        _startDialog = page.Q<VisualElement>("start-dialog");
        _editDialog = page.Q<VisualElement>("edit-dialog");
        _editForm = new PlanFormView(page.Q<VisualElement>("edit-form"), page);

        page.Q<Button>("create-plan-button").clicked += router.NavigateToPlanCreate;
        page.Q<Button>("add-plan-button").clicked += router.NavigateToPlanCreate;

        page.Q<Button>("dialog-cancel-button").clicked += HideStartDialog;
        page.Q<Button>("dialog-start-button").clicked += StartWork;
        page.Q<Button>("edit-cancel-button").clicked += HideEditDialog;
        page.Q<Button>("edit-save-button").clicked += SaveEdit;
        page.Q<Button>("edit-range-button").clicked += EditRange;
        // 暗い背景部分のタップでも閉じる (Googleマップ等の標準的な挙動)
        _startDialog.RegisterCallback<ClickEvent>(evt =>
        {
            if (evt.target == _startDialog) HideStartDialog();
        });
        _editDialog.RegisterCallback<ClickEvent>(evt =>
        {
            if (evt.target == _editDialog) HideEditDialog();
        });

        Refresh();
    }

    /// <summary>データからUIを作り直す (手動再レンダリング)</summary>
    public void Refresh()
    {
        var emptyState = _page.Q<VisualElement>("empty-state");
        var sections = _page.Q<ScrollView>("plan-sections");
        var addButton = _page.Q<Button>("add-plan-button");

        var today = DateTime.Today;
        // 作業記録が付いた計画は「作業済み」なので予定には出さない (記録ページのオレンジ側で見る)
        var todayPlans = WithoutRecords(PlanStore.GetByDate(today));
        var futurePlans = WithoutRecords(PlanStore.GetAfter(today));
        var hasPlans = todayPlans.Count > 0 || futurePlans.Count > 0;

        // 計画の有無で「空の状態」と「一覧」を切り替える
        emptyState.style.display = hasPlans ? DisplayStyle.None : DisplayStyle.Flex;
        sections.style.display = hasPlans ? DisplayStyle.Flex : DisplayStyle.None;
        addButton.style.display = hasPlans ? DisplayStyle.Flex : DisplayStyle.None;

        if (!hasPlans)
        {
            return;
        }

        // 今日の計画だけタップで作業開始できる
        RefreshSection("today-section-title", "today-plan-list", todayPlans, ShowStartDialog);
        RefreshSection("future-section-title", "future-plan-list", futurePlans, null);
    }

    static List<WorkPlan> WithoutRecords(List<WorkPlan> plans)
        => plans.Where(p => !RecordStore.HasRecordForPlan(p.Id)).ToList();

    /// <summary>セクション1つ分を作り直す。計画が無いセクションはタイトルごと隠す</summary>
    void RefreshSection(string titleName, string listName,
        System.Collections.Generic.List<WorkPlan> plans, Action<WorkPlan> onClick)
    {
        var title = _page.Q<Label>(titleName);
        var list = _page.Q<VisualElement>(listName);

        var display = plans.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
        title.style.display = display;
        list.style.display = display;

        list.Clear();
        foreach (var plan in plans)
        {
            list.Add(PlanCard.Create(plan, onEdit: EditPlan, onDelete: DeletePlan, onClick: onClick));
        }
    }

    // ---- 作業開始フロー ----

    void ShowStartDialog(WorkPlan plan)
    {
        _dialogPlan = plan;

        var rows = _page.Q<VisualElement>("dialog-rows");
        rows.Clear();
        AddDialogRow(rows, "実施日", $"{plan.Date.Month}/{plan.Date.Day}");
        AddDialogRow(rows, "樹種", plan.Species ?? "-");
        AddDialogRow(rows, "間伐率", $"{plan.ThinningRatePercent}%");
        AddDialogRow(rows, "対象面積", $"{plan.AreaHa:0.0#}ha");

        _startDialog.style.display = DisplayStyle.Flex;
    }

    static void AddDialogRow(VisualElement container, string key, string value)
    {
        var row = new VisualElement();
        row.AddToClassList("home__dialog-row");

        var keyLabel = new Label(key);
        keyLabel.AddToClassList("home__dialog-key");
        row.Add(keyLabel);

        var valueLabel = new Label(value);
        valueLabel.AddToClassList("home__dialog-value");
        row.Add(valueLabel);

        container.Add(row);
    }

    void HideStartDialog()
    {
        _startDialog.style.display = DisplayStyle.None;
        _dialogPlan = null;
    }

    void StartWork()
    {
        if (_dialogPlan == null) return;
        _router.NavigateToWorking(_dialogPlan);
    }

    // ---- 編集フロー ----

    void EditPlan(WorkPlan plan)
    {
        _editingPlan = plan;
        _editForm.SetValues(plan.Species, plan.ThinningRatePercent, plan.FellingStandardCm,
            plan.FellingIntervalM, plan.AreaHa, plan.Date);
        _editDialog.style.display = DisplayStyle.Flex;
    }

    void HideEditDialog()
    {
        _editDialog.style.display = DisplayStyle.None;
        _editingPlan = null;
    }

    /// <summary>範囲の再設定: 計画作成ページを編集モードで開く (面積もそこで再計算される)</summary>
    void EditRange()
    {
        if (_editingPlan == null) return;
        var plan = _editingPlan;
        HideEditDialog();
        _router.NavigateToPlanEdit(plan);
    }

    void SaveEdit()
    {
        if (_editingPlan == null) return;

        _editingPlan.Date = _editForm.Date;
        _editingPlan.Species = _editForm.Species;
        _editingPlan.ThinningRatePercent = _editForm.RatePercent;
        _editingPlan.FellingStandardCm = _editForm.StandardCm;
        _editingPlan.FellingIntervalM = _editForm.IntervalM;
        PlanStore.Update(_editingPlan);

        HideEditDialog();
        Refresh();
    }

    void DeletePlan(WorkPlan plan)
    {
        PlanStore.Remove(plan);
        Refresh();
    }
}
