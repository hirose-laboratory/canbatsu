using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// 作業計画の入力フォーム (計画作成の結果シートと編集ダイアログで共用するコンポーネント)。
/// AIの自動算出値を初期値に入れつつ、人の手でも修正できるようにする。
///  - 樹種: チップから選択 (文字入力をなくしてIME/全角の問題を避ける)
///  - 数値: 半角のみ受け付け (全角数字は自動で半角に変換、それ以外は捨てる)
///  - 実施日: タップでカレンダー (CalendarPopup) から選択
/// スタイルは Common.uss の .plan-form 系クラス。
/// </summary>
public class PlanFormView
{
    static readonly string[] DefaultSpecies = { "スギ", "ヒノキ", "マツ", "カラマツ" };

    readonly VisualElement _pageRoot;   // カレンダーを重ねる先 (ページのルート)
    readonly VisualElement _speciesChips;
    readonly Label _areaValue;
    readonly TextField _rateField;
    readonly TextField _standardField;
    readonly TextField _intervalField;
    readonly Button _dateButton;

    string _species = DefaultSpecies[0];
    DateTime _date = DateTime.Today;

    // 入力欄が空や不正な値のまま保存されたときに使う値 (SetValuesで入れた初期値)
    int _fallbackRate;
    int _fallbackStandard;
    float _fallbackInterval;

    public PlanFormView(VisualElement container, VisualElement pageRoot)
    {
        _pageRoot = pageRoot;
        container.Clear();

        // 樹種 (チップから選択)
        var speciesRow = AddRow(container, "樹種");
        _speciesChips = new VisualElement();
        _speciesChips.AddToClassList("plan-form__chips");
        speciesRow.Add(_speciesChips);

        // 対象面積 (地図の範囲から計算するので表示のみ)
        var areaRow = AddRow(container, "対象面積");
        _areaValue = new Label("-");
        _areaValue.AddToClassList("plan-form__value");
        areaRow.Add(_areaValue);

        _rateField = AddNumberRow(container, "間伐率", "%", allowDecimal: false);
        // 伐採基準は「この直径未満の木を伐る」(DB/AI側と同じ定義)
        _standardField = AddNumberRow(container, "伐採基準 (直径)", "cm未満", allowDecimal: false);
        _intervalField = AddNumberRow(container, "しきい値 (伐採間隔)", "m", allowDecimal: true);

        // 実施日 (タップでカレンダー)
        var dateRow = AddRow(container, "実施日");
        _dateButton = new Button(OpenCalendar);
        _dateButton.AddToClassList("plan-form__date-button");
        dateRow.Add(_dateButton);

        RebuildSpeciesChips(DefaultSpecies);
        UpdateDateButton();
    }

    /// <summary>初期値 (AIの算出結果や既存計画の値) をフォームに入れる</summary>
    public void SetValues(string species, int ratePercent, int standardCm, float intervalM,
        double areaHa, DateTime date)
    {
        _species = string.IsNullOrEmpty(species) ? DefaultSpecies[0] : species;
        _fallbackRate = ratePercent;
        _fallbackStandard = standardCm;
        _fallbackInterval = intervalM;
        _date = date.Date;

        // 一覧に無い樹種 (AIが独自の値を返した場合など) は先頭にチップとして足す
        var options = new List<string>(DefaultSpecies);
        if (!options.Contains(_species)) options.Insert(0, _species);
        RebuildSpeciesChips(options);

        _areaValue.text = $"{areaHa:0.0#}ha";
        _rateField.SetValueWithoutNotify(ratePercent.ToString());
        _standardField.SetValueWithoutNotify(standardCm.ToString());
        _intervalField.SetValueWithoutNotify(intervalM.ToString("0.0#", CultureInfo.InvariantCulture));
        UpdateDateButton();
    }

    // ---- 入力値の取り出し (不正な値は初期値に戻す) ----

    public string Species => _species;
    public DateTime Date => _date;
    public int RatePercent => ParseInt(_rateField.value, _fallbackRate, max: 100);
    public int StandardCm => ParseInt(_standardField.value, _fallbackStandard, max: 999);

    public float IntervalM
    {
        get
        {
            if (float.TryParse(_intervalField.value, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var v) && v > 0)
            {
                return v;
            }
            return _fallbackInterval;
        }
    }

    static int ParseInt(string text, int fallback, int max)
    {
        if (int.TryParse(text, out var v) && v > 0 && v <= max) return v;
        return fallback;
    }

    // ---- フォームの組み立て ----

    /// <summary>「キー + 入力」の1行を作る (入力側は呼び出し元がAddする)</summary>
    static VisualElement AddRow(VisualElement container, string key)
    {
        var row = new VisualElement();
        row.AddToClassList("plan-form__row");
        container.Add(row);

        var keyLabel = new Label(key);
        keyLabel.AddToClassList("plan-form__key");
        row.Add(keyLabel);
        return row;
    }

    TextField AddNumberRow(VisualElement container, string key, string suffix, bool allowDecimal)
    {
        var row = AddRow(container, key);

        var group = new VisualElement();
        group.AddToClassList("plan-form__input-group");
        row.Add(group);

        var field = new TextField { maxLength = allowDecimal ? 5 : 3 };
        field.AddToClassList("plan-form__input");
        // スマホでは数値キーボードを出す
        field.keyboardType = allowDecimal
            ? TouchScreenKeyboardType.NumbersAndPunctuation
            : TouchScreenKeyboardType.NumberPad;
        AttachHalfWidthNumericFilter(field, allowDecimal);
        group.Add(field);

        var suffixLabel = new Label(suffix);
        suffixLabel.AddToClassList("plan-form__suffix");
        group.Add(suffixLabel);

        return field;
    }

    void RebuildSpeciesChips(IReadOnlyList<string> options)
    {
        _speciesChips.Clear();
        foreach (var option in options)
        {
            var name = option;
            var chip = new Button(() =>
            {
                _species = name;
                RefreshChipSelection();
            }) { text = name, userData = name };
            chip.AddToClassList("plan-form__chip");
            _speciesChips.Add(chip);
        }
        RefreshChipSelection();
    }

    void RefreshChipSelection()
    {
        foreach (var child in _speciesChips.Children())
        {
            child.EnableInClassList("plan-form__chip--selected", (string)child.userData == _species);
        }
    }

    void OpenCalendar()
    {
        CalendarPopup.Show(_pageRoot, _date, picked =>
        {
            _date = picked.Date;
            UpdateDateButton();
        });
    }

    void UpdateDateButton()
    {
        _dateButton.text = _date.ToString("yyyy/MM/dd");
    }

    // ---- 半角数字フィルタ ----

    /// <summary>半角数字だけ受け付ける。全角数字は半角へ変換し、それ以外の文字は捨てる</summary>
    static void AttachHalfWidthNumericFilter(TextField field, bool allowDecimal)
    {
        field.RegisterValueChangedCallback(evt =>
        {
            string filtered = FilterNumeric(evt.newValue, allowDecimal);
            if (filtered != evt.newValue)
            {
                field.SetValueWithoutNotify(filtered);
            }
        });
    }

    static string FilterNumeric(string input, bool allowDecimal)
    {
        if (string.IsNullOrEmpty(input)) return "";

        var sb = new StringBuilder(input.Length);
        bool hasDot = false;
        foreach (var raw in input)
        {
            char c = raw;
            if (c >= '０' && c <= '９') c = (char)('0' + (c - '０')); // 全角→半角
            if (c == '．') c = '.';

            if (c >= '0' && c <= '9')
            {
                sb.Append(c);
            }
            else if (allowDecimal && c == '.' && !hasDot && sb.Length > 0)
            {
                sb.Append(c);
                hasDot = true;
            }
        }
        return sb.ToString();
    }
}
