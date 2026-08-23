using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Management;

/// <summary>
/// XR (XREALグラス表示) の起動と停止を手動で管理する。
/// 「Initialize XR on Startup」をオフにして、作業開始のタイミングでだけXRを立ち上げる方針:
///  - アプリ起動時 (ログイン等) はただのスマホアプリとして動く (起動ハング対策)
///  - 作業開始 → EnsureStarted() / 作業終了 → Stop()
/// </summary>
public static class XrSession
{
    public static bool IsRunning { get; private set; }

    /// <summary>直近の失敗理由 (UI表示用)</summary>
    public static string LastError { get; private set; } = "";

    public static bool EnsureStarted()
    {
        var settings = XRGeneralSettings.Instance;
        var manager = settings != null ? settings.Manager : null;
        if (manager == null)
        {
            LastError = "XR設定がビルドに入っていません (XR Plug-in ManagementのXREALチェック+保存を確認)";
            Debug.LogWarning($"XrSession: {LastError}");
            return false;
        }

        if (manager.activeLoader == null)
        {
            manager.InitializeLoaderSync();
            if (manager.activeLoader == null)
            {
                LastError = "XRを初期化できませんでした (グラスの接続を確認)";
                Debug.LogWarning($"XrSession: {LastError}");
                return false;
            }
        }

        if (!IsRunning)
        {
            manager.StartSubsystems();
            IsRunning = true;
            Debug.Log("XrSession: XR開始");
        }
        return true;
    }

    /// <summary>XRの表示サブシステムが実際に動いているか (診断表示用)</summary>
    public static bool IsDisplayRunning()
    {
        var displays = new List<XRDisplaySubsystem>();
        SubsystemManager.GetSubsystems(displays);
        foreach (var display in displays)
        {
            if (display.running) return true;
        }
        return false;
    }

    public static void Stop()
    {
        var settings = XRGeneralSettings.Instance;
        var manager = settings != null ? settings.Manager : null;
        if (manager == null || manager.activeLoader == null)
        {
            IsRunning = false;
            return;
        }

        if (IsRunning)
        {
            manager.StopSubsystems();
        }
        manager.DeinitializeLoader();
        IsRunning = false;
        Debug.Log("XrSession: XR停止");
    }
}
