using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Firebase.Firestore;
using UnityEngine;
using UnityEngine.UIElements;

/// <summary>
/// ログインページのController。
/// 仮ログイン方式: ユーザー名+パスワードを入力し、内部で「ユーザー名@can-batsu.local」の
/// メールアドレスに変換してFirebase Auth (メール/パスワード方式) に登録・照合する。
/// ログイン状態は端末に残るため、起動時に残っていれば自動ログインする (山で再ログイン不要)。
/// ログイン成功時にPlanStore/RecordStoreの読み込みまで済ませてからホームへ遷移する。
/// </summary>
public class LoginPageController
{
    const string EmailDomain = "@can-batsu.local";

    readonly Action _onLoginSuccess;
    readonly TextField _userField;
    readonly TextField _passwordField;
    readonly Button _loginButton;
    readonly Button _registerButton;
    readonly Label _status;

    public LoginPageController(VisualElement page, Action onLoginSuccess)
    {
        _onLoginSuccess = onLoginSuccess;
        _userField = page.Q<TextField>("user-id-field");
        _passwordField = page.Q<TextField>("password-field");
        _loginButton = page.Q<Button>("login-button");
        _registerButton = page.Q<Button>("register-button");
        _status = page.Q<Label>("status-label");

        _loginButton.clicked += () => _ = LoginAsync();
        _registerButton.clicked += () => _ = RegisterAsync();

        _ = TryAutoLoginAsync();
    }

    /// <summary>前回のログイン状態が端末に残っていればそのままホームへ</summary>
    async Task TryAutoLoginAsync()
    {
        SetBusy(true, "");
        if (!await FirebaseService.InitAsync())
        {
            SetBusy(false, "Firebaseを初期化できませんでした");
            return;
        }

        if (FirebaseService.Auth.CurrentUser != null)
        {
            SetBusy(true, "前回のログイン情報で開始しています...");
            await LoadStoresAsync();
            _onLoginSuccess();
            return;
        }

        SetBusy(false, "");
    }

    async Task LoginAsync()
    {
        var (username, password) = ReadInput();
        if (username == null) return;

        SetBusy(true, "ログインしています...");
        if (!await FirebaseService.InitAsync())
        {
            SetBusy(false, "Firebaseを初期化できませんでした");
            return;
        }

        try
        {
            await FirebaseService.Auth.SignInWithEmailAndPasswordAsync(username + EmailDomain, password);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"ログイン失敗: {e.Message}");
            SetBusy(false, "ログインできませんでした。ユーザー名・パスワード・通信状況を確認してください");
            return;
        }

        SetBusy(true, "データを読み込んでいます...");
        await LoadStoresAsync();
        _onLoginSuccess();
    }

    async Task RegisterAsync()
    {
        var (username, password) = ReadInput();
        if (username == null) return;

        // メールアドレスの形に変換するため、ユーザー名は半角英数字に限定する
        if (!Regex.IsMatch(username, "^[a-zA-Z0-9._-]+$"))
        {
            SetStatus("ユーザー名は半角英数字で入力してください");
            return;
        }
        if (password.Length < 6)
        {
            SetStatus("パスワードは6文字以上にしてください (Firebaseの決まり)");
            return;
        }

        SetBusy(true, "新規登録しています...");
        if (!await FirebaseService.InitAsync())
        {
            SetBusy(false, "Firebaseを初期化できませんでした");
            return;
        }

        try
        {
            // 1. Authに登録 (パスワードはFirebaseが預かる。Firestoreには保存しない)
            var result = await FirebaseService.Auth
                .CreateUserWithEmailAndPasswordAsync(username + EmailDomain, password);

            // 2. 表示用ユーザー名を users コレクションに保存 (ドキュメントID = UID)
            await FirebaseService.Db.Collection("users").Document(result.User.UserId)
                .SetAsync(new Dictionary<string, object>
                {
                    { "username", username },
                    { "createdAt", FieldValue.ServerTimestamp },
                });
        }
        catch (Exception e)
        {
            Debug.LogWarning($"新規登録失敗: {e.Message}");
            SetBusy(false, "登録できませんでした。同じユーザー名が既に使われているか、通信状況を確認してください");
            return;
        }

        SetBusy(true, "データを読み込んでいます...");
        await LoadStoresAsync();
        _onLoginSuccess();
    }

    /// <summary>入力欄を読む。空ならエラーメッセージを出して (null, null) を返す</summary>
    (string username, string password) ReadInput()
    {
        var username = (_userField.value ?? "").Trim();
        var password = _passwordField.value ?? "";
        if (username.Length == 0 || password.Length == 0)
        {
            SetStatus("ユーザー名とパスワードを入力してください");
            return (null, null);
        }
        return (username, password);
    }

    static async Task LoadStoresAsync()
    {
        await PlanStore.LoadAsync();
        await RecordStore.LoadAsync();
    }

    void SetBusy(bool busy, string message)
    {
        _loginButton.SetEnabled(!busy);
        _registerButton.SetEnabled(!busy);
        SetStatus(message);
    }

    void SetStatus(string message)
    {
        _status.text = message;
        // 処理中の案内は赤ではなくグレーで出す
        bool isError = message.Contains("できませんでした") || message.Contains("ください");
        _status.style.color = isError ? new StyleColor(new Color(0.78f, 0.24f, 0.2f))
                                      : new StyleColor(new Color(0.45f, 0.45f, 0.45f));
    }
}
