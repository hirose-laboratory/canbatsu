#if UNITY_ANDROID
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.Management;

namespace Unity.XR.XREAL.Editor
{
    public partial class XREALBuildProcessor : XRBuildHelper<XREALSettings>
    {
        internal static string PackagePath => $"Packages/{XREALUtility.k_Identifier}";
        internal static string PackageRuntime => $"{PackagePath}/Runtime";
        internal static string PackagePlugins => $"{PackageRuntime}/Plugins/Android";
        static List<string> s_CopiedAssets = new List<string>();

        public override void OnPreprocessBuild(BuildReport report)
        {
            base.OnPreprocessBuild(report);
            if (XREALUtility.IsLoaderActive())
            {
#if UNITY_6000_0_OR_NEWER
                PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.Activity;
#endif
                if (XREALSettings.GetSettings().SupportMultiResume)
                {
#if UNITY_6000_0_OR_NEWER
                    ImportAsset("nractivitylife_6-release");
#else
                    ImportAsset("nractivitylife-release");
#endif
                }
                if (XREALSettings.GetSettings().EnableNativeSessionManager)
                {
                    ImportAsset("libXREALNativeSessionManager");
                }
            }
        }

        void ImportAsset(string name)
        {
            foreach (var asset in AssetDatabase.FindAssets(name, new string[] { PackagePlugins }))
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(asset);
                string relativePath = assetPath.Substring(PackageRuntime.Length + 1);
                string destPath = Path.Combine("Assets", relativePath);
                string destFolder = Path.GetDirectoryName(destPath);
                if (!Directory.Exists(destFolder))
                    Directory.CreateDirectory(destFolder);
                AssetDatabase.CopyAsset(assetPath, destPath);
                s_CopiedAssets.Add(destPath);
                if (AssetImporter.GetAtPath(destPath) is PluginImporter importer)
                {
                    importer.SetCompatibleWithPlatform(BuildTarget.Android, true);
                    importer.SaveAndReimport();
                }
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        public override void OnPostprocessBuild(BuildReport report)
        {
            base.OnPostprocessBuild(report);
            foreach (var assetPath in s_CopiedAssets)
            {
                AssetDatabase.DeleteAsset(assetPath);
            }
            s_CopiedAssets.Clear();
        }
    }
}
#endif
