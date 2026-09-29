using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// アプリ全体のページ切替を担当する (Next.jsのルーター相当)。
/// AppShell.uxml の #content に各ページのUXMLを Instantiate() して差し込む。
/// シーン上の UIDocument と同じGameObjectに付ける。
/// </summary>
[RequireComponent(typeof(UIDocument))]
public class AppRouter : MonoBehaviour
{
    // 各ページのUXML。InspectorでVisualTreeAssetを割り当てる
    [SerializeField] VisualTreeAsset loginPage;
    [SerializeField] VisualTreeAsset homePage;
    [SerializeField] VisualTreeAsset recordPage;
    [SerializeField] VisualTreeAsset planCreatePage;
    [SerializeField] VisualTreeAsset workingPage;

    VisualElement _content;
    VisualElement _header;
    VisualElement _tabBar;
    Button _tabSchedule;
    Button _tabRecord;
    Button _menuButton;
    Button _logoutButton;
    Button _voiceButton;
    VisualElement _menuOverlay;
    Label _voiceToast;
    IVisualElementScheduledItem _toastHider;
    VoiceCommandService _voice;
    bool _onWorkingPage; // 作業中ページを開いているか (音声コマンドの解釈を選木用に切り替える)

    const string ActiveTabClass = "app-shell__tab--active";
    const string VoicePrefKey = "voice_commands_enabled";

    void OnEnable()
    {
        var root = GetComponent<UIDocument>().rootVisualElement;
        _content = root.Q<VisualElement>("content");
        _header = root.Q<VisualElement>("header");
        _tabBar = root.Q<VisualElement>("tab-bar");
        _tabSchedule = root.Q<Button>("tab-schedule");
        _tabRecord = root.Q<Button>("tab-record");
        _menuButton = root.Q<Button>("menu-button");
        _logoutButton = root.Q<Button>("menu-logout-button");
        _menuOverlay = root.Q<VisualElement>("menu-overlay");

        _voiceButton = root.Q<Button>("menu-voice-button");
        _voiceToast = root.Q<Label>("voice-toast");

        _tabSchedule.clicked += NavigateToHome;
        _tabRecord.clicked += NavigateToRecord;
        _menuButton.clicked += ToggleMenu;
        _logoutButton.clicked += Logout;
        _voiceButton.clicked += ToggleVoice;
        // メニューの外側タップで閉じる
        _menuOverlay.RegisterCallback<ClickEvent>(evt =>
        {
            if (evt.target == _menuOverlay) HideMenu();
        });

        // 音声コマンド (聞き取りはVoiceCommandService、解釈はOnVoicePhrase)
        _voice = GetComponent<VoiceCommandService>();
        if (_voice == null) _voice = gameObject.AddComponent<VoiceCommandService>();
        _voice.PhraseRecognized += OnVoicePhrase;
        _voice.StatusChanged += ShowToast;
        UpdateVoiceLabel();
        if (VoiceEnabled) _voice.StartListening();

        NavigateToLogin();
    }

    void OnDisable()
    {
        _tabSchedule.clicked -= NavigateToHome;
        _tabRecord.clicked -= NavigateToRecord;
        _menuButton.clicked -= ToggleMenu;
        _logoutButton.clicked -= Logout;
        _voiceButton.clicked -= ToggleVoice;
        _voice.PhraseRecognized -= OnVoicePhrase;
        _voice.StatusChanged -= ShowToast;
    }

    // ---- 三本線メニュー ----

    void ToggleMenu()
    {
        bool isOpen = _menuOverlay.resolvedStyle.display != DisplayStyle.None;
        _menuOverlay.style.display = isOpen ? DisplayStyle.None : DisplayStyle.Flex;
    }

    void HideMenu() => _menuOverlay.style.display = DisplayStyle.None;

    void Logout()
    {
        HideMenu();
        FirebaseService.Auth?.SignOut();
        NavigateToLogin();
    }

    // ---- 音声コマンド ----

    bool VoiceEnabled => PlayerPrefs.GetInt(VoicePrefKey, 0) == 1;

    /// <summary>メニューの「音声認識: オン/オフ」。設定は端末に保存され次回起動でも維持される</summary>
    void ToggleVoice()
    {
        bool enable = !VoiceEnabled;
        PlayerPrefs.SetInt(VoicePrefKey, enable ? 1 : 0);
        PlayerPrefs.Save();

        if (enable) _voice.StartListening();
        else _voice.StopListening();

        UpdateVoiceLabel();
        HideMenu();
        ShowToast(enable
            ? "音声認識オン:「よてい」「きろく」でページを切り替えられます"
            : "音声認識をオフにしました");
    }

    void UpdateVoiceLabel()
    {
        _voiceButton.text = VoiceEnabled ? "音声認識: オン" : "音声認識: オフ";
    }

    /// <summary>聞き取ったテキストをコマンドとして解釈する</summary>
    void OnVoicePhrase(string text)
    {
        // 作業中ページでは選木コマンドだけを受け付ける (誤操作防止のためページ遷移コマンドは無効のまま。
        // 操作結果のフィードバックはグラス側HUDに出るのでトーストは出さない)
        if (_onWorkingPage)
        {
            if (ContainsAny(text, "マーク", "まーく")) ArDemoController.MarkTreeAtGaze();
            else if (ContainsAny(text, "とりけし", "取り消し")) ArDemoController.UnmarkTreeAtGaze();
            else if (ContainsAny(text, "きじゅん", "基準")) ArDemoController.SetAnchorHere();
            // かかり木モード: 根元を見て「かかり」→先端までなぞって「てっぺん」→危険域。「かいじょ」で終了
            else if (ContainsAny(text, "かかり", "掛かり", "カカリ")) ArDemoController.StartKakarigiAtGaze();
            else if (ContainsAny(text, "てっぺん", "テッペン", "先端", "せんたん", "頂上", "ちょうじょう"))
                ArDemoController.ConfirmKakarigiTop();
            else if (ContainsAny(text, "かいじょ", "解除", "カイジョ")) ArDemoController.ClearKakarigi();
            return;
        }

        // ログイン前など (ヘッダー非表示の画面) では誤操作防止のため反応しない
        if (_header.resolvedStyle.display == DisplayStyle.None) return;

        if (ContainsAny(text, "記録", "きろく", "キロク"))
        {
            ShowToast("音声コマンド: 記録ページへ");
            NavigateToRecord();
        }
        else if (ContainsAny(text, "予定", "よてい", "ヨテイ", "計画", "けいかく", "ホーム"))
        {
            ShowToast("音声コマンド: 予定ページへ");
            NavigateToHome();
        }
    }

    static bool ContainsAny(string text, params string[] words)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var word in words)
        {
            if (text.Contains(word)) return true;
        }
        return false;
    }

    /// <summary>画面下に一時メッセージを出す (2.5秒で自動で消える)</summary>
    void ShowToast(string message)
    {
        _voiceToast.text = message;
        _voiceToast.style.display = DisplayStyle.Flex;
        _toastHider?.Pause();
        _toastHider = _voiceToast.schedule
            .Execute(() => _voiceToast.style.display = DisplayStyle.None)
            .StartingIn(2500);
    }

    public void NavigateToLogin()
    {
        _onWorkingPage = false;
        SetChromeVisible(false);
        var page = ShowPage(loginPage);
        if (page == null) return;
        _ = new LoginPageController(page, onLoginSuccess: NavigateToHome);
    }

    /// <summary>予定ページ (ホーム)</summary>
    public void NavigateToHome()
    {
        _onWorkingPage = false;
        SetChromeVisible(true);
        SetActiveTab(_tabSchedule);
        var page = ShowPage(homePage);
        if (page == null) return;
        _ = new HomePageController(page, this);
    }

    /// <summary>作業計画の新規作成 (地図で範囲選択)。Googleマップ風に全画面表示</summary>
    public void NavigateToPlanCreate()
    {
        _onWorkingPage = false;
        SetChromeVisible(false);
        var page = ShowPage(planCreatePage);
        if (page == null) return;
        _ = new PlanCreatePageController(page, this);
    }

    /// <summary>既存計画の範囲を地図で再設定する (計画作成ページを編集モードで開く)</summary>
    public void NavigateToPlanEdit(WorkPlan plan)
    {
        _onWorkingPage = false;
        SetChromeVisible(false);
        var page = ShowPage(planCreatePage);
        if (page == null) return;
        _ = new PlanCreatePageController(page, this, plan);
    }

    /// <summary>作業中ページ (ARグラス連携モード)。タブバーも隠して全画面表示</summary>
    public void NavigateToWorking(WorkPlan plan)
    {
        _onWorkingPage = false;
        SetChromeVisible(false);
        var page = ShowPage(workingPage);
        if (page == null) return;
        _onWorkingPage = true;
        // AR側が保存済みマップの読み込み・選木の保存に使う計画IDを、AR起動より先に渡す
        ArDemoController.CurrentPlanId = plan?.Id;
        _ = new WorkingPageController(page, this, plan);
    }

    public void NavigateToRecord()
    {
        _onWorkingPage = false;
        SetChromeVisible(true);
        SetActiveTab(_tabRecord);
        var page = ShowPage(recordPage);
        if (page == null) return;
        _ = new RecordPageController(page, this);
    }

    /// <summary>#content の中身を差し替えてページのルート要素を返す。未割り当てならnull</summary>
    VisualElement ShowPage(VisualTreeAsset asset)
    {
        if (asset == null)
        {
            Debug.LogError(
                "AppRouterにページのUXMLが割り当てられていません。" +
                "HierarchyのUIDocumentを選択し、InspectorのApp Routerにある None (Visual Tree Asset) の欄に" +
                "該当ページのUXMLを◎ボタンから割り当ててください。");
            return null;
        }

        _content.Clear();
        var page = asset.Instantiate();
        page.style.flexGrow = 1;
        // flexの子の「最小=中身の高さ」を切る。これが無いとページの中身が多いとき
        // #contentごと伸びてヘッダー/タブバーを押し潰す (スクロールは各ページのScrollViewが担当)
        page.style.minHeight = 0;
        _content.Add(page);
        return page;
    }

    /// <summary>ヘッダーとタブバーの表示/非表示 (ログイン画面では隠す)</summary>
    void SetChromeVisible(bool visible)
    {
        var display = visible ? DisplayStyle.Flex : DisplayStyle.None;
        _header.style.display = display;
        _tabBar.style.display = display;
        HideMenu(); // ページ遷移でメニューは閉じる
    }

    void SetActiveTab(Button active)
    {
        _tabSchedule.EnableInClassList(ActiveTabClass, active == _tabSchedule);
        _tabRecord.EnableInClassList(ActiveTabClass, active == _tabRecord);
    }
}
