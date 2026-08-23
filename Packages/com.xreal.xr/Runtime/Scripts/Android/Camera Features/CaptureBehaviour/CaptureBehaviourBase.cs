using UnityEngine;

namespace Unity.XR.XREAL
{
    /// <summary> A capture behaviour base. </summary>
    public class CaptureBehaviourBase : MonoBehaviour, IFrameConsumer
    {
        /// <summary> The RGB camera rig. </summary>
        [SerializeField] Transform CameraRig;
        /// <summary> The capture camera. </summary>
        public Camera CaptureCamera;
        public Camera CaptureCamera2;
        public Camera RGBCamera;
        public Camera GrayCamera;
        public Camera GrayCamera2;
        private FrameCaptureContext m_FrameCaptureContext;

        /// <summary> Gets the context. </summary>
        /// <returns> The context. </returns>
        public FrameCaptureContext GetContext()
        {
            return m_FrameCaptureContext;
        }

        /// <summary> Initializes this object. </summary>
        /// <param name="context">     The context.</param>
        /// <param name="blendCamera"> The blend camera.</param>
        public virtual void Init(FrameCaptureContext context)
        {
            this.m_FrameCaptureContext = context;
        }

        /// <summary> Updates the capture behaviour. </summary>
        protected virtual void Update()
        {
            if (m_FrameCaptureContext != null && m_FrameCaptureContext.RequestCameraParam().lockRoll)
            {
                Vector3 eulerAngles = CaptureCamera.transform.eulerAngles;
                eulerAngles.z = 0;
                CaptureCamera.transform.eulerAngles = eulerAngles;
                Vector3 eulerAngles2 = CaptureCamera2.transform.eulerAngles;
                eulerAngles2.z = 0;
                CaptureCamera2.transform.eulerAngles = eulerAngles2;
            }
        }

        public void SetCameraMask(int mask)
        {
            CaptureCamera.cullingMask = mask;
            CaptureCamera2.cullingMask = mask;
        }

        public void SetBackGroundColor(Color color)
        {
            CaptureCamera.backgroundColor = color;
            CaptureCamera2.backgroundColor = color;
        }

        /// <summary> Executes the 'frame' action. </summary>
        /// <param name="frame"> The frame.</param>
        public virtual void OnFrame(UniversalTextureFrame frame)
        {
            var mode = m_FrameCaptureContext.GetBlender().BlendMode;
            switch (mode)
            {
                case BlendMode.CameraOnly:
                    MoveToGod();
                    break;
                default:
                    break;
            }
        }

        private void MoveToGod()
        {
            CameraRig.transform.position = Vector3.one * 9999;
        }

        internal void SetupCameras(CaptureSide side, CameraType cameraType)
        {
            if (side == CaptureSide.Both)
            {
                CaptureCamera.gameObject.SetActive(true);
                CaptureCamera2.gameObject.SetActive(true);
                if (cameraType == CameraType.RGB)
                {
                    RGBCamera.gameObject.SetActive(true);
                    GrayCamera.gameObject.SetActive(false);
                    GrayCamera2.gameObject.SetActive(false);
                }
                else
                {
                    RGBCamera.gameObject.SetActive(false);
                    GrayCamera.gameObject.SetActive(true);
                    GrayCamera2.gameObject.SetActive(true);
                }
            }
            else
            {

                CaptureCamera.gameObject.SetActive(true);
                CaptureCamera2.gameObject.SetActive(false);
                if (cameraType == CameraType.RGB)
                {
                    RGBCamera.gameObject.SetActive(true);
                    GrayCamera.gameObject.SetActive(false);
                    GrayCamera2.gameObject.SetActive(false);
                }
                else
                {
                    RGBCamera.gameObject.SetActive(false);
                    GrayCamera.gameObject.SetActive(true);
                    GrayCamera2.gameObject.SetActive(false);
                }
            }
        }


    }
}
