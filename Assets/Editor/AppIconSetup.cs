using UnityEditor;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEngine;

/// <summary>
/// アプリ名とアイコンを Player Settings に一括で設定する (Unityメニュー Tools → CAN伐 → アプリ名とアイコンを設定)。
/// 手で設定してもよいが、Android の Adaptive アイコンはサイズ別の欄が多いのでスクリプトで入れる。
/// 一度実行すれば ProjectSettings.asset に保存されるので、以後このスクリプトは不要 (消してもよい)。
/// </summary>
public static class AppIconSetup
{
    const string ProductName = "CAN伐";
    const string CompanyName = "";   // 変えたいときだけ入れる (空なら今のまま)

    const string IconDir = "Assets/Sprites/AppIcon/";
    const string DefaultIcon = IconDir + "AppIcon.png";
    const string AdaptiveBackground = IconDir + "AppIcon_AdaptiveBackground.png";
    const string AdaptiveForeground = IconDir + "AppIcon_AdaptiveForeground.png";

    [MenuItem("Tools/CAN伐/アプリ名とアイコンを設定")]
    public static void Apply()
    {
        var icon = LoadIcon(DefaultIcon);
        var background = LoadIcon(AdaptiveBackground);
        var foreground = LoadIcon(AdaptiveForeground);
        if (icon == null || background == null || foreground == null)
        {
            EditorUtility.DisplayDialog("アイコン設定",
                $"アイコン画像が見つかりません。{IconDir} に3枚あるか確認してください", "OK");
            return;
        }

        PlayerSettings.productName = ProductName;
        if (!string.IsNullOrEmpty(CompanyName)) PlayerSettings.companyName = CompanyName;

        // Default Icon (全プラットフォーム共通の既定)
        PlayerSettings.SetIcons(NamedBuildTarget.Unknown, new[] { icon }, IconKind.Any);

        // Android: Adaptive (背景+前景の2層) のみ。Unity 6 では Round / Legacy は廃止 (deprecated) で、
        // 古い端末向けには Unity が Adaptive から自動生成する
        SetAndroidIcons(NamedBuildTarget.Android, AndroidPlatformIconKind.Adaptive, background, foreground);

        AssetDatabase.SaveAssets();
        Debug.Log($"[AppIconSetup] Product Name = {PlayerSettings.productName}, アイコン設定完了 " +
                  $"(Default Icon + Android Adaptive)。パッケージ名 {PlayerSettings.applicationIdentifier} は変更していません");
        SettingsService.OpenProjectSettings("Project/Player");
    }

    static void SetAndroidIcons(NamedBuildTarget target, PlatformIconKind kind, params Texture2D[] layers)
    {
        var icons = PlayerSettings.GetPlatformIcons(target, kind);
        foreach (var platformIcon in icons)
        {
            platformIcon.SetTextures(layers);
        }
        PlayerSettings.SetPlatformIcons(target, kind, icons);
    }

    /// <summary>アイコン用に取り込み設定を整えて読み込む (圧縮なし・ミップマップなし・透過あり)</summary>
    static Texture2D LoadIcon(string path)
    {
        if (AssetImporter.GetAtPath(path) is TextureImporter importer)
        {
            bool dirty = false;
            if (importer.textureCompression != TextureImporterCompression.Uncompressed)
            {
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                dirty = true;
            }
            if (importer.mipmapEnabled) { importer.mipmapEnabled = false; dirty = true; }
            if (!importer.alphaIsTransparency) { importer.alphaIsTransparency = true; dirty = true; }
            if (dirty) importer.SaveAndReimport();
        }
        else
        {
            AssetDatabase.ImportAsset(path);
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }
}
