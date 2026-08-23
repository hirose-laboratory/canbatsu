using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.XR;
#if UNITY_URP
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#endif

namespace Unity.XR.XREAL
{
    /// <summary>
    /// A singleton class for interacting with the XREAL Virtual Controller. 
    /// Provides functionality to send haptic feedback based on button presses and touchpad interactions.
    /// </summary>
    public class XREALVirtualController : SingletonMonoBehaviour<XREALVirtualController>
    {
        /// <summary>
        /// Event triggered when a pointer down action occurs in the XREALButton script.
        /// </summary>
        public event Action<XREALButtonType, GameObject, PointerEventData> pointerDown;

        /// <summary>
        /// Event triggered when a pointer up action occurs in the XREALButton script.
        /// </summary>
        public event Action<XREALButtonType, GameObject, PointerEventData> pointerUp;

        /// <summary>
        /// Event triggered when a pointer drag action occurs in the XREALButton script.
        /// </summary>
        public event Action<XREALButtonType, GameObject, PointerEventData> pointerDrag;

        /// <summary>
        /// Event triggered when an end drag action occurs in the XREALButton script.
        /// </summary>
        public event Action<XREALButtonType, GameObject, PointerEventData> pointerEndDrag;

#pragma warning disable CS0414
        [SerializeField]
        Camera m_URPUICamera = null;
#pragma warning restore CS0414

        /// <summary>
        /// Gets the InputDevice representing the controller currently in use.
        /// </summary>
        public InputDevice Controller { get; private set; }

        byte[] m_Buffer = new byte[8];
#if UNITY_EDITOR && UNITY_INPUT_SYSTEM
        XREALControllerSimulator m_ControllerSimulator;
#endif

        void Start()
        {
            Debug.Log("[XREALVirtualController] Start");
#if UNITY_EDITOR && UNITY_INPUT_SYSTEM
            m_ControllerSimulator = GetComponent<XREALControllerSimulator>();
#endif
#if UNITY_URP
            if (m_URPUICamera != null && GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset)
            {
                m_URPUICamera.gameObject.SetActive(true);
                m_URPUICamera.GetUniversalAdditionalCameraData().allowXRRendering = false;
#if !UNITY_EDITOR
                foreach (var canvas in GetComponentsInChildren<Canvas>(true))
                {
                    canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    canvas.worldCamera = m_URPUICamera;
                }
#else
                var overlayData = m_URPUICamera.GetUniversalAdditionalCameraData();
                overlayData.renderType = CameraRenderType.Overlay;
#endif
            }
#endif
            UpdateController();
            InputDevices.deviceConnected += (InputDevice device) =>
            {
                if (!Controller.isValid)
                {
                    UpdateController();
                }
            };
            InputDevices.deviceDisconnected += (InputDevice device) =>
            {
                if (Controller == device)
                {
                    Controller = new InputDevice();
                }
            };
        }

        void UpdateController()
        {
            List<InputDevice> inputDevices = new List<InputDevice>();
            InputDevices.GetDevicesWithCharacteristics(InputDeviceCharacteristics.Controller, inputDevices);
            if (inputDevices.Count > 0)
                Controller = inputDevices[0];
        }

        /// <summary>
        /// Sends haptic feedback for button press or release events.
        /// </summary>
        /// <param name="buttonType">The type of the button (e.g., TriggerButton, GripButton).</param>
        /// <param name="press">Indicates whether the button is pressed (true) or released (false).</param>
        public void SendHapticButton(XREALButtonType buttonType, bool press)
        {
#if UNITY_EDITOR && UNITY_INPUT_SYSTEM
            if (m_ControllerSimulator != null)
                m_ControllerSimulator.UpdateButton(buttonType, press);
#endif
            if (Controller.isValid)
            {
                m_Buffer[0] = (byte)buttonType;
                m_Buffer[1] = press ? (byte)1 : (byte)0;
                Controller.SendHapticBuffer(0, m_Buffer);
            }
        }

        /// <summary>
        /// Sends haptic feedback for touchpad interactions by specifying the 2D axis position.
        /// </summary>
        /// <param name="buttonType">The type of the button (e.g., Primary2DAxis).</param>
        /// <param name="axisX">The normalized X-axis value of the touchpad (-1 to 1).</param>
        /// <param name="axisY">The normalized Y-axis value of the touchpad (-1 to 1).</param>
        public void SendHapticAxis(XREALButtonType buttonType, float axisX, float axisY)
        {
#if UNITY_EDITOR && UNITY_INPUT_SYSTEM
            if (m_ControllerSimulator != null)
                m_ControllerSimulator.SendHapticAxis(buttonType, axisX, axisY);
#endif
            if (Controller.isValid)
            {
                short xValue = (short)Mathf.RoundToInt(axisX * 32767);
                short yValue = (short)Mathf.RoundToInt(axisY * 32767);
                byte[] xBytes = BitConverter.GetBytes(xValue);
                byte[] yBytes = BitConverter.GetBytes(yValue);
                m_Buffer[0] = (byte)buttonType;
                m_Buffer[1] = 1;
                m_Buffer[2] = xBytes[0];
                m_Buffer[3] = xBytes[1];
                m_Buffer[4] = yBytes[0];
                m_Buffer[5] = yBytes[1];
                Controller.SendHapticBuffer(0, m_Buffer);
            }
        }

        /// <summary>
        /// Sends a signal to end haptic feedback for a touchpad interaction.
        /// </summary>
        /// <param name="buttonType">The type of the button (e.g., Primary2DAxis).</param>
        public void SendHapticAxisEnd(XREALButtonType buttonType)
        {
#if UNITY_EDITOR && UNITY_INPUT_SYSTEM
            if (m_ControllerSimulator != null)
                m_ControllerSimulator.SendHapticAxisEnd(buttonType);
#endif
            if (Controller.isValid)
            {
                m_Buffer[0] = (byte)buttonType;
                m_Buffer[1] = 0;
                Controller.SendHapticBuffer(0, m_Buffer);
            }
        }

        /// <summary>
        /// Invokes the pointerDown event
        /// </summary>
        /// <param name="buttonType">The type of the XREAL button that was pressed.</param>
        /// <param name="target">The GameObject that received the pointer down event.</param>
        /// <param name="eventData">Pointer event data associated with the event.</param>
        public void OnPointerDown(XREALButtonType buttonType, GameObject target, PointerEventData eventData)
        {
            pointerDown?.Invoke(buttonType, target, eventData);
        }

        /// <summary>
        /// Invokes the pointerUp event.
        /// </summary>
        /// <param name="buttonType">The type of the XREAL button that was released.</param>
        /// <param name="target">The GameObject that received the pointer up event.</param>
        /// <param name="eventData">Pointer event data associated with the event.</param>
        public void OnPointerUp(XREALButtonType buttonType, GameObject target, PointerEventData eventData)
        {
            pointerUp?.Invoke(buttonType, target, eventData);
        }

        /// <summary>
        /// Invokes the pointerDrag event.
        /// </summary>
        /// <param name="buttonType">The type of the XREAL button being dragged.</param>
        /// <param name="target">The GameObject that is receiving the pointer drag event.</param>
        /// <param name="eventData">Pointer event data associated with the event.</param>
        public void OnPointerDrag(XREALButtonType buttonType, GameObject target, PointerEventData eventData)
        {
            pointerDrag?.Invoke(buttonType, target, eventData);
        }

        /// <summary>
        /// Invokes the pointerEndDrag event.
        /// </summary>
        /// <param name="buttonType">The type of the XREAL button that finished dragging.</param>
        /// <param name="target">The GameObject that received the pointer end drag event.</param>
        /// <param name="eventData">Pointer event data associated with the event.</param>
        public void OnPointerEndDrag(XREALButtonType buttonType, GameObject target, PointerEventData eventData)
        {
            pointerEndDrag?.Invoke(buttonType, target, eventData);
        }
    }
}
