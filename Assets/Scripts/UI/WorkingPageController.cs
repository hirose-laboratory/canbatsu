using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// 作業中ページ (ARグラス連携モード) のController。
/// 動作中を示すパルスアニメーションを行うだけの質素な画面。
/// 「作業を終了する」→ 確認ダイアログ → 計画を作業記録に変えてホームへ戻る。
/// TODO: 本来はここでARグラスとの通信・エッジAI処理を回す (バックエンド待ち)
/// </summary>
public class WorkingPageController
{
    readonly DateTime _startTime = DateTime.Now;
    readonly AppRouter _router;
    readonly WorkPlan _plan;
    readonly VisualElement _finishDialog;

    public WorkingPageController(VisualElement page, AppRouter router, WorkPlan plan)
    {
        _router = router;
        _plan = plan;
        _finishDialog = page.Q<VisualElement>("finish-dialog");

        // 実施中の計画の概要
        var summary = page.Q<VisualElement>("plan-summary");
        AddRow(summary, "実施日", $"{plan.Date.Month}/{plan.Date.Day}");
        AddRow(summary, "樹種", plan.Species ?? "-");
        AddRow(summary, "間伐率", $"{plan.ThinningRatePercent}%");
        AddRow(summary, "しきい値 (伐採間隔)", $"{plan.FellingIntervalM:0.0}m");
        AddRow(summary, "対象面積", $"{plan.AreaHa:0.0#}ha");

        // 誤タップ防止のため確認ダイアログを挟む
        page.Q<Button>("finish-button").clicked += ShowFinishDialog;
        page.Q<Button>("finish-cancel-button").clicked += HideFinishDialog;
        page.Q<Button>("finish-confirm-button").clicked += CompleteWork;
        _finishDialog.RegisterCallback<ClickEvent>(evt =>
        {
            if (evt.target == _finishDialog) HideFinishDialog();
        });

        // パルスアニメーション (緑の輪がゆっくり広がって薄くなる)
        var ring = page.Q<VisualElement>("pulse-ring");
        page.schedule.Execute(() =>
        {
            float phase = (float)((DateTime.Now - _startTime).TotalSeconds % 1.6) / 1.6f;
            float scale = 1f + phase * 0.5f;
            ring.style.scale = new Scale(new Vector3(scale, scale, 1f));
            ring.style.opacity = 1f - phase;
        }).Every(33);
    }

    void ShowFinishDialog() => _finishDialog.style.display = DisplayStyle.Flex;

    void HideFinishDialog() => _finishDialog.style.display = DisplayStyle.None;

    /// <summary>
    /// 作業終了: 計画から作業記録を作って保存する。
    /// 計画自体は削除せずPlanIdで紐づけて残す (計画vs実績の比較用)。
    /// 記録が付いた計画は予定一覧に出なくなる (HomePageControllerで除外)。
    /// </summary>
    void CompleteWork()
    {
        RecordStore.Add(new WorkRecord
        {
            PlanId = _plan.Id,
            Date = DateTime.Today,
            FelledCount = 0, // TODO: ARグラスの自動カウントを受け取る (バックエンド待ち)
            AreaHa = _plan.AreaHa,
            ThinningRatePercent = _plan.ThinningRatePercent,
            RangePoints = new List<Vector2>(_plan.RangePoints ?? new List<Vector2>()),
        });

        _router.NavigateToHome();
    }

    static void AddRow(VisualElement container, string key, string value)
    {
        var row = new VisualElement();
        row.AddToClassList("working__card-row");

        var keyLabel = new Label(key);
        keyLabel.AddToClassList("working__card-key");
        row.Add(keyLabel);

        var valueLabel = new Label(value);
        valueLabel.AddToClassList("working__card-value");
        row.Add(valueLabel);

        container.Add(row);
    }
}
