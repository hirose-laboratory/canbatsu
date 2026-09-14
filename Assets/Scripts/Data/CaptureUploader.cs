using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
#if CANBATSU_FB_STORAGE
using Firebase.Storage;
#endif

/// <summary>
/// 撮り溜めた作業画像 (PlanCaptureService の出力) のアップロード待ちキュー。
/// 山では圏外前提なので、その場では送らずローカルに溜め、
/// 事務所等に戻ってから KickUploadPending でWi-Fi接続時だけまとめて送る。
///
/// Firebase Storage のDLLは未導入のため、Storage処理は CANBATSU_FB_STORAGE 定義時のみ
/// コンパイルされる (未定義ビルドはローカル保管のまま待機し、SDK導入後に定義を足すだけで送信が有効になる)。
/// アップロード成功後もローカルのファイルは消さない (実証実験でクラウド側と突き合わせて検証するため)。
/// </summary>
public static class CaptureUploader
{
    /// <summary>送信済みセッションの目印ファイル名 (これが有るディレクトリは再送しない)</summary>
    const string UploadedMarker = ".uploaded";

    /// <summary>状態の説明 (人向け。空文字なら表示不要)</summary>
    public static string Status { get; private set; } = "";

    /// <summary>Statusが変わるたびに通知</summary>
    public static event Action StatusChanged;

    static bool _running = false; // Storage未定義ビルドでは読み取り専用になるため明示的に初期化しておく

    /// <summary>未送信セッションを探してアップロードを試す (実行中の多重呼び出しは無視される)</summary>
    public static void KickUploadPending(MonoBehaviour runner)
    {
        if (_running || runner == null) return;

        var pending = FindPendingSessions();
        if (pending.Count == 0)
        {
            SetStatus("");
            return;
        }

#if CANBATSU_FB_STORAGE
        // モバイル回線で大容量を送らないようWi-Fi接続時だけ送る
        if (Application.internetReachability != NetworkReachability.ReachableViaLocalAreaNetwork)
        {
            SetStatus($"Wi-Fi待ち: {pending.Count} セッション");
            return;
        }
        _running = true;
        runner.StartCoroutine(UploadAll(pending));
#else
        // Storage未導入ビルド: 送信はせずローカルに置いたまま待つ
        SetStatus($"画像 {pending.Count} セッションをローカル保管中 (Storage未接続)");
#endif
    }

    /// <summary>captures/{planId}/session_*/ のうち未送信のものを列挙する (撮影中の現行セッションは閉じてから送るので除く)</summary>
    static List<string> FindPendingSessions()
    {
        var result = new List<string>();
        try
        {
            string root = Path.Combine(Application.persistentDataPath, "captures");
            if (!Directory.Exists(root)) return result;

            string active = PlanCaptureService.Active != null ? PlanCaptureService.Active.SessionDir : null;
            foreach (string planDir in Directory.GetDirectories(root))
            {
                foreach (string sessionDir in Directory.GetDirectories(planDir, "session_*"))
                {
                    if (File.Exists(Path.Combine(sessionDir, UploadedMarker))) continue;
                    if (sessionDir == active) continue;
                    result.Add(sessionDir);
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"アップロード対象の走査に失敗しました: {e.Message}");
        }
        return result;
    }

    static void SetStatus(string status)
    {
        if (Status == status) return;
        Status = status;
        StatusChanged?.Invoke();
    }

#if CANBATSU_FB_STORAGE
    /// <summary>セッションを1つずつ直列でアップロードする (帯域の食い合いと失敗の切り分けを避けるため並列にしない)</summary>
    static IEnumerator UploadAll(List<string> sessions)
    {
        try
        {
            var storage = FirebaseStorage.DefaultInstance;
            int uploaded = 0, held = 0;
            for (int i = 0; i < sessions.Count; i++)
            {
                // 途中でWi-Fiが切れたら残りは次回に持ち越す
                if (Application.internetReachability != NetworkReachability.ReachableViaLocalAreaNetwork)
                {
                    held += sessions.Count - i;
                    break;
                }

                string sessionDir = sessions[i];
                SetStatus($"画像アップロード中: {i + 1}/{sessions.Count} セッション");

                string sessionName = Path.GetFileName(sessionDir);
                string planId = Path.GetFileName(Path.GetDirectoryName(sessionDir));
                bool allOk = true;

                foreach (string filePath in Directory.GetFiles(sessionDir))
                {
                    string fileName = Path.GetFileName(filePath);
                    if (fileName == UploadedMarker) continue;

                    byte[] bytes = null;
                    try { bytes = File.ReadAllBytes(filePath); }
                    catch (Exception e) { Debug.LogWarning($"読み込みに失敗: {fileName}: {e.Message}"); }
                    if (bytes == null)
                    {
                        allOk = false;
                        break;
                    }

                    var task = storage
                        .GetReference($"plans/{planId}/captures/{sessionName}/{fileName}")
                        .PutBytesAsync(bytes);
                    while (!task.IsCompleted) yield return null;
                    if (task.IsFaulted || task.IsCanceled)
                    {
                        Debug.LogWarning($"アップロードに失敗: {fileName}: {task.Exception?.GetBaseException().Message}");
                        allOk = false;
                        break;
                    }
                }

                if (allOk)
                {
                    // 全ファイル成功したセッションだけ送信済みにする (途中失敗は次回まるごと再送)
                    try { File.Create(Path.Combine(sessionDir, UploadedMarker)).Dispose(); } catch (Exception) { }
                    uploaded++;
                }
                else
                {
                    held++;
                }
            }
            SetStatus(held > 0
                ? $"画像アップロード: {uploaded} セッション完了 / {held} セッション保留 (次回再試行)"
                : "画像アップロード完了");
        }
        finally
        {
            _running = false;
        }
    }
#endif
}
