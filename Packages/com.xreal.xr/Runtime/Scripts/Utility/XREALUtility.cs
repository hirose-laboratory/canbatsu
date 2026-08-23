using System;
using System.Linq;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Management;
using static UnityEngine.XR.XRDisplaySubsystem;

namespace Unity.XR.XREAL
{
    public static class XREALUtility
    {
        public const string k_Identifier = "com.xreal.xr";

        private static Camera mainCamera;

        /// <summary>
        /// Gets the main camera, which is either the XR Origin's camera or null if not found.
        /// </summary>
        public static Camera MainCamera
        {
            get
            {
                if (mainCamera == null)
                {
                    XROrigin origin = FindAnyObjectByType<XROrigin>();
                    mainCamera = (origin != null) ? origin.Camera : null;
                }
                return mainCamera;
            }
        }

        /// <summary>
        /// Finds an object of the specified type in the scene.
        /// </summary>
        /// <typeparam name="T">The type of the object to find.</typeparam>
        /// <returns>The object if found; otherwise, null.</returns>
        public static T FindAnyObjectByType<T>() where T : UnityEngine.Object
        {
#if UNITY_2023_1_OR_NEWER
            return UnityEngine.Object.FindAnyObjectByType<T>();
#else
            return UnityEngine.Object.FindObjectOfType<T>();
#endif
        }

        /// <summary>
        /// Gets the currently active XR loader.
        /// </summary>
        /// <returns>The active XR loader, or null if no loader is active.</returns>
        public static XRLoader GetActiveLoader()
        {
            if (XRGeneralSettings.Instance != null && XRGeneralSettings.Instance.Manager != null)
            {
                return XRGeneralSettings.Instance.Manager.activeLoader;
            }

            return null;
        }

        /// <summary>
        /// Determines whether the XREAL XR loader is currently active.
        /// </summary>
        /// <returns>True if the XREAL XR loader is active; otherwise, false.</returns>
        public static bool IsLoaderActive()
        {
#if UNITY_EDITOR
            var targetGroup = UnityEditor.BuildPipeline.GetBuildTargetGroup(UnityEditor.EditorUserBuildSettings.activeBuildTarget);
            var settings = UnityEditor.XR.Management.XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(targetGroup);
            return settings != null && settings.Manager.activeLoaders.OfType<XREALXRLoader>().Any();
#else
            return GetActiveLoader() is XREALXRLoader;
#endif
        }

        /// <summary>
        /// Gets the loaded subsystem of the specified type from the active loader.
        /// </summary>
        /// <typeparam name="T">The type of the subsystem to retrieve.</typeparam>
        /// <returns>The loaded subsystem of the specified type, or null if not found.</returns>
        public static T GetLoadedSubsystem<T>() where T : class, ISubsystem
        {
            XRLoader loader = GetActiveLoader();
            if (loader != null)
            {
                return loader.GetLoadedSubsystem<T>();
            }
            return null;
        }

        /// <summary>
        /// Retrieves the XR render parameter for the specified eye.
        /// </summary>
        /// <param name="eye">The eye index (0 for left, 1 for right).</param>
        /// <param name="parameter">The render parameter for the specified eye.</param>
        /// <returns>True if the render parameter is successfully retrieved; otherwise, false.</returns>
        public static bool GetXRRenderParameter(int eye, out XRRenderParameter parameter)
        {
            parameter = new XRRenderParameter();
            XRDisplaySubsystem displaySubsystem = GetLoadedSubsystem<XRDisplaySubsystem>();
            if (displaySubsystem == null)
                return false;

            var passCount = displaySubsystem.GetRenderPassCount();
            if (passCount == 0)
                return false;

            int passIndex = passCount == 2 && eye == 1 ? 1 : 0;
            displaySubsystem.GetRenderPass(passIndex, out XRRenderPass renderPass);
            var parameterCount = renderPass.GetRenderParameterCount();
            if (parameterCount == 0)
                return false;

            int parameterIndex = parameterCount == 2 && eye == 1 ? 1 : 0;
            renderPass.GetRenderParameter(MainCamera, parameterIndex, out parameter);
            return true;
        }

        /// <summary>
        /// Determines if the specified transform is a child of the main camera.
        /// </summary>
        /// <param name="transform">The transform to check.</param>
        /// <returns>True if the transform is a child of the main camera; otherwise, false.</returns>
        public static bool IsChildOfCamera(this Transform transform)
        {
            Transform cameraTransform = MainCamera.transform;
            Transform current = transform;

            while (current != null)
            {
                if (current == cameraTransform)
                {
                    return true;
                }
                current = current.parent;
            }

            return false;
        }

        /// <summary>
        /// Calculates the relative pose of the specified transform to the main camera.
        /// </summary>
        /// <param name="transform">The transform to calculate the relative pose for.</param>
        /// <returns>The relative pose to the main camera.</returns>
        public static Pose GetRelativePoseToCamera(this Transform transform)
        {
            Transform cameraTransform = MainCamera.transform;
            Vector3 relativePosition = cameraTransform.InverseTransformPoint(transform.position);
            Quaternion relativeRotation = Quaternion.Inverse(cameraTransform.rotation) * transform.rotation;

            return new Pose(relativePosition, relativeRotation);
        }

        /// <summary>
        /// Calculates the relative pose of the specified transform to the parent of the main camera.
        /// </summary>
        /// <param name="transform">The transform to calculate the relative pose for.</param>
        /// <returns>The relative pose to the parent of the main camera.</returns>
        public static Pose GetRelativePoseToCameraParent(this Transform transform)
        {
            Transform cameraTransform = MainCamera.transform;

            if (cameraTransform.parent != null)
            {
                Vector3 relativePosition = cameraTransform.parent.InverseTransformPoint(transform.position);
                Quaternion relativeRotation = Quaternion.Inverse(cameraTransform.parent.rotation) * transform.rotation;
                return new Pose(relativePosition, relativeRotation);
            }
            else
            {
                return new Pose(transform.position, transform.rotation);
            }
        }

        /// <summary>
        /// Adds or retrieves the specified component from the GameObject.
        /// </summary>
        /// <typeparam name="T">The type of the component to add or retrieve.</typeparam>
        /// <param name="gameObject">The GameObject to add or retrieve the component from.</param>
        /// <returns>The component of type T.</returns>
        public static T AddOrGetComponent<T>(this GameObject gameObject) where T : Component
        {
            if (gameObject.TryGetComponent(out T component))
            {
                return component;
            }
            else
            {
                return gameObject.AddComponent<T>();
            }
        }

        /// <summary>
        /// Adds or retrieves the specified component from the Component's GameObject.
        /// </summary>
        /// <typeparam name="T">The type of the component to add or retrieve.</typeparam>
        /// <param name="component">The Component to add or retrieve the component from.</param>
        /// <returns>The component of type T.</returns>
        public static T AddOrGetComponent<T>(this Component component) where T : Component
        {
            return component.gameObject.AddOrGetComponent<T>();
        }

        /// <summary>
        /// Saves a RenderTexture as a PNG file.
        /// </summary>
        /// <param name="renderTexture">The RenderTexture to save.</param>
        /// <param name="filePath">The path to save the file.</param>
        public static void SaveRenderTextureToFile(RenderTexture renderTexture, string filePath)
        {
            Texture2D texture = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.RGB24, false);
            RenderTexture activeRenderTexture = RenderTexture.active;
            RenderTexture.active = renderTexture;
            texture.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
            texture.Apply();
            RenderTexture.active = activeRenderTexture;
            byte[] bytes = texture.EncodeToPNG();
#if UNITY_EDITOR
            UnityEngine.Object.DestroyImmediate(texture);
#else
            UnityEngine.Object.Destroy(texture);
#endif
            System.IO.File.WriteAllBytes(filePath, bytes);
            Debug.Log("RenderTexture saved to " + filePath);
        }

        /// <summary>
        /// Retrieves the RenderTexture from XRDisplaySubsystem and saves it to a file.
        /// </summary>
        /// <param name="renderPassIndex">The index of the render pass.</param>
        /// <param name="filePath">The path to save the file.</param>
        public static void SaveXRRenderTextureToFile(string filePath, int renderPassIndex = 0)
        {
            var displaySubsytem = GetLoadedSubsystem<XRDisplaySubsystem>();
            if (displaySubsytem == null)
            {
                Debug.LogError("XRDisplaySubsystem is null.");
                return;
            }
            RenderTexture renderTexture = displaySubsytem.GetRenderTextureForRenderPass(renderPassIndex);
            if (renderTexture != null)
            {
                SaveRenderTextureToFile(renderTexture, filePath);
            }
            else
            {
                Debug.LogError("RenderTexture is null for the given render pass index.");
            }
        }

#if UNITY_ANDROID
        static AndroidJavaObject s_UnityActivity;

        /// <summary>
        /// Gets the current Unity activity in the Android environment.
        /// </summary>
        public static AndroidJavaObject UnityActivity
        {
            get
            {
                if (s_UnityActivity == null)
                {
                    using AndroidJavaClass unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                    s_UnityActivity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                }
                return s_UnityActivity;
            }
        }

        /// <summary>
        /// Runs the specified action on the Unity Android UI thread.
        /// </summary>
        /// <param name="action">The action to execute on the UI thread.</param>
        public static void RunOnUiThread(Action action)
        {
            UnityActivity.Call("runOnUiThread", new AndroidJavaRunnable(action));
        }

        public static void StartAutoLogcat()
        {
            using AndroidJavaClass cls = new AndroidJavaClass("com.xreal.evapro.autologcat.AutoLogcatHelper");
            cls.CallStatic("start", UnityActivity);
        }

        public static void StopAutoLogcat()
        {
            using AndroidJavaClass cls = new AndroidJavaClass("com.xreal.evapro.autologcat.AutoLogcatHelper");
            cls.CallStatic("stop");
        }
#endif
    }
}
