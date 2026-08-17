using System;
using UnityEngine.UIElements;

/// <summary>
/// 作業計画カードを生成する共通ヘルパー (ReactでいうPlanCardコンポーネント)。
/// スタイルは Common.uss の .plan-card 系クラスを使う。
/// </summary>
public static class PlanCard
{
    /// <param name="plan">表示する計画</param>
    /// <param name="onEdit">編集アイコンが押されたとき</param>
    /// <param name="onDelete">削除アイコンが押されたとき</param>
    /// <param name="onClick">カード本体がタップされたとき (今日の計画の作業開始用。nullなら反応しない)</param>
    public static VisualElement Create(WorkPlan plan, Action<WorkPlan> onEdit, Action<WorkPlan> onDelete,
        Action<WorkPlan> onClick = null)
    {
        var card = new VisualElement();
        card.AddToClassList("plan-card");
        if (plan.Date.Date == DateTime.Today)
        {
            card.AddToClassList("plan-card--today");
        }

        if (onClick != null)
        {
            card.AddToClassList("plan-card--clickable");
            card.RegisterCallback<ClickEvent>(evt =>
            {
                // 編集/削除ボタン上のクリックはカードのタップとして扱わない
                if (IsInsideButton(evt.target as VisualElement, card)) return;
                onClick(plan);
            });
        }

        // 1行目: 日付 + 編集/削除アイコン
        var header = new VisualElement();
        header.AddToClassList("plan-card__header");
        card.Add(header);

        var date = new Label($"{plan.Date.Month}/{plan.Date.Day}");
        date.AddToClassList("plan-card__date");
        header.Add(date);

        if (!string.IsNullOrEmpty(plan.Species))
        {
            var species = new Label(plan.Species);
            species.AddToClassList("plan-card__species");
            header.Add(species);
        }

        var spacer = new VisualElement();
        spacer.AddToClassList("plan-card__spacer");
        header.Add(spacer);

        header.Add(CreateEditButton(() => onEdit(plan)));
        header.Add(CreateDeleteButton(() => onDelete(plan)));

        // 2行目: 間伐率 / 範囲
        var row1 = new VisualElement();
        row1.AddToClassList("plan-card__row");
        row1.Add(CreateField($"間伐率: {plan.ThinningRatePercent}%"));
        row1.Add(CreateField($"範囲: {plan.AreaHa:0.#}ha"));
        card.Add(row1);

        // 3行目: 伐採間隔
        var row2 = new VisualElement();
        row2.AddToClassList("plan-card__row");
        row2.Add(CreateField($"伐採間隔: {plan.FellingIntervalM:0.#}m"));
        card.Add(row2);

        // 4行目: 伐採基準
        var row3 = new VisualElement();
        row3.AddToClassList("plan-card__row");
        row3.Add(CreateField($"伐採基準: {plan.FellingStandardCm}cm未満"));
        card.Add(row3);

        // タップできるカードには作業開始の案内を出す
        if (onClick != null)
        {
            var hint = new Label("タップして作業開始 ▶");
            hint.AddToClassList("plan-card__start-hint");
            card.Add(hint);
        }

        return card;
    }

    /// <summary>クリックされた要素がカード内のButtonの中にあるかどうか</summary>
    static bool IsInsideButton(VisualElement target, VisualElement card)
    {
        for (var e = target; e != null && e != card; e = e.parent)
        {
            if (e is Button) return true;
        }
        return false;
    }

    static Label CreateField(string text)
    {
        var label = new Label(text);
        label.AddToClassList("plan-card__field");
        return label;
    }

    /// <summary>編集アイコンボタン (四角い枠+ペンをUSSで描画)</summary>
    static Button CreateEditButton(Action onClick)
    {
        var button = new Button(onClick);
        button.AddToClassList("plan-card__icon-button");

        var icon = new VisualElement();
        icon.AddToClassList("plan-card__edit-icon");

        var frame = new VisualElement();
        frame.AddToClassList("plan-card__edit-frame");
        icon.Add(frame);

        var pencil = new VisualElement();
        pencil.AddToClassList("plan-card__edit-pencil");
        icon.Add(pencil);

        button.Add(icon);
        return button;
    }

    /// <summary>削除(ゴミ箱)アイコンボタン</summary>
    static Button CreateDeleteButton(Action onClick)
    {
        var button = new Button(onClick);
        button.AddToClassList("plan-card__icon-button");

        var icon = new VisualElement();
        icon.AddToClassList("plan-card__trash-icon");

        var handle = new VisualElement();
        handle.AddToClassList("plan-card__trash-handle");
        icon.Add(handle);

        var lid = new VisualElement();
        lid.AddToClassList("plan-card__trash-lid");
        icon.Add(lid);

        var body = new VisualElement();
        body.AddToClassList("plan-card__trash-body");
        icon.Add(body);

        button.Add(icon);
        return button;
    }
}
