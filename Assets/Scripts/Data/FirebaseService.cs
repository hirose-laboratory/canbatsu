using System.Threading.Tasks;
using Firebase;
using Firebase.Auth;
using Firebase.Firestore;
using UnityEngine;

/// <summary>
/// Firebaseの初期化と共有インスタンス置き場。
/// 使う前に必ず await FirebaseService.InitAsync() を呼ぶ (2回目以降は同じTaskを返すだけ)。
/// 実際の初期化はログイン画面 (LoginPageController) が起動時に行う。
/// </summary>
public static class FirebaseService
{
    static Task<bool> _initTask;

    public static FirebaseAuth Auth { get; private set; }
    public static FirebaseFirestore Db { get; private set; }

    /// <summary>ログイン中ユーザーのUID (未ログインならnull)</summary>
    public static string Uid => Auth?.CurrentUser?.UserId;

    public static Task<bool> InitAsync()
    {
        if (_initTask == null) _initTask = InitInternalAsync();
        return _initTask;
    }

    static async Task<bool> InitInternalAsync()
    {
        // 依存ライブラリの確認と修復 (Firebase公式の推奨手順)
        var status = await FirebaseApp.CheckAndFixDependenciesAsync();
        if (status != DependencyStatus.Available)
        {
            Debug.LogError($"Firebaseの初期化に失敗しました: {status}。" +
                           "Assets直下にgoogle-services.jsonがあるか確認してください");
            return false;
        }

        Auth = FirebaseAuth.DefaultInstance;
        Db = FirebaseFirestore.DefaultInstance;
        return true;
    }
}
