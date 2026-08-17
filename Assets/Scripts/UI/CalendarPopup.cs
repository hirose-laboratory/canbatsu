using System;
using UnityEngine.UIElements;

/// <summary>
/// 日付選択のカレンダーポップアップ (実施日の入力用)。
/// Show() でページ全体を覆って表示し、日をタップすると onSelect を呼んで閉じる。
/// スタイルは Common.uss の .calendar 系クラス。
/// </summary>
public static class CalendarPopup
{
    static readonly string[] WeekdayLabels = { "日", "月", "火", "水", "木", "金", "土" };

    public static void Show(VisualElement pageRoot, DateTime selected, Action<DateTime> onSelect)
    {
        var scrim = new VisualElement();
        scrim.AddToClassList("calendar__scrim");
        // 暗い背景部分のタップで閉じる (他のダイアログと同じ挙動)
        scrim.RegisterCallback<ClickEvent>(evt =>
        {
            if (evt.target == scrim) scrim.RemoveFromHierarchy();
        });

        var card = new VisualElement();
        card.AddToClassList("calendar");
        scrim.Add(card);

        // ヘッダー: ◀ 2026年8月 ▶
        var header = new VisualElement();
        header.AddToClassList("calendar__header");
        card.Add(header);

        var prevButton = CreateNavButton(toRight: false);
        var title = new Label();
        title.AddToClassList("calendar__title");
        var nextButton = CreateNavButton(toRight: true);
        header.Add(prevButton);
        header.Add(title);
        header.Add(nextButton);

        // 曜日の行
        var weekdayRow = new VisualElement();
        weekdayRow.AddToClassList("calendar__week");
        foreach (var w in WeekdayLabels)
        {
            var label = new Label(w);
            label.AddToClassList("calendar__weekday");
            weekdayRow.Add(label);
        }
        card.Add(weekdayRow);

        var grid = new VisualElement();
        card.Add(grid);

        var displayMonth = new DateTime(selected.Year, selected.Month, 1);

        void Rebuild()
        {
            title.text = $"{displayMonth.Year}年{displayMonth.Month}月";
            grid.Clear();

            // 月初の週の日曜から6週分並べる (一般的なカレンダーと同じ形)
            var day = displayMonth.AddDays(-(int)displayMonth.DayOfWeek);
            for (int week = 0; week < 6; week++)
            {
                var row = new VisualElement();
                row.AddToClassList("calendar__week");
                grid.Add(row);

                for (int i = 0; i < 7; i++)
                {
                    var date = day;
                    var button = new Button(() =>
                    {
                        scrim.RemoveFromHierarchy();
                        onSelect(date);
                    }) { text = date.Day.ToString() };
                    button.AddToClassList("calendar__day");
                    if (date.Month != displayMonth.Month) button.AddToClassList("calendar__day--other");
                    if (date == DateTime.Today) button.AddToClassList("calendar__day--today");
                    if (date == selected.Date) button.AddToClassList("calendar__day--selected");
                    row.Add(button);

                    day = day.AddDays(1);
                }
            }
        }

        prevButton.clicked += () => { displayMonth = displayMonth.AddMonths(-1); Rebuild(); };
        nextButton.clicked += () => { displayMonth = displayMonth.AddMonths(1); Rebuild(); };

        Rebuild();
        pageRoot.Add(scrim);
    }

    /// <summary>前月/次月ボタン (くの字アイコンをVisualElement+USSで描画する規約)</summary>
    static Button CreateNavButton(bool toRight)
    {
        var button = new Button();
        button.AddToClassList("calendar__nav");

        var icon = new VisualElement();
        icon.AddToClassList("calendar__nav-icon");
        if (toRight) icon.AddToClassList("calendar__nav-icon--right");

        var up = new VisualElement();
        up.AddToClassList("calendar__nav-bar");
        up.AddToClassList("calendar__nav-bar--up");
        icon.Add(up);

        var down = new VisualElement();
        down.AddToClassList("calendar__nav-bar");
        down.AddToClassList("calendar__nav-bar--down");
        icon.Add(down);

        button.Add(icon);
        return button;
    }
}
