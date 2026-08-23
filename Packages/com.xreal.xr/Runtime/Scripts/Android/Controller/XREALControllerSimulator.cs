#if UNITY_INPUT_SYSTEM
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace Unity.XR.XREAL
{
    public class XREALControllerSimulator : MonoBehaviour
    {
#if UNITY_EDITOR
        private XREALSimulatorControllerState m_RightControllerState;
        private XREALController m_RightXREALController;
        private bool m_Touching = false;
        private Vector2 m_TouchStartPos = Vector2.zero;

        void OnEnable()
        {
            if (m_RightXREALController == null)
            {
                m_RightXREALController = InputSystem.AddDevice<XREALController>();
                InputSystem.SetDeviceUsage(m_RightXREALController, CommonUsages.RightHand);
            }
        }

        void OnDestroy()
        {
            if (m_RightXREALController != null)
                InputSystem.RemoveDevice(m_RightXREALController);
        }

        internal void UpdateButton(XREALButtonType buttonType, bool appendButton)
        {
            m_RightControllerState = m_RightControllerState.WithButton(buttonType, appendButton);
            if (m_RightXREALController != null)
                InputState.Change(m_RightXREALController, m_RightControllerState);
        }

        internal void SendHapticAxis(XREALButtonType buttonType, float touchX, float touchY)
        {
            if (!m_Touching)
            {
                m_TouchStartPos.x = touchX;
                m_TouchStartPos.y = touchY;
                m_Touching = true;
            }
            float deltaX = Mathf.Clamp(touchX - m_TouchStartPos.x, -1, 1);
            float deltaY = Mathf.Clamp(touchY - m_TouchStartPos.y, -1, 1);
            switch (buttonType)
            {
                case XREALButtonType.Primary2DAxis:
                    m_RightControllerState.primary2DAxis.x = deltaX;
                    m_RightControllerState.primary2DAxis.y = deltaY;
                    break;
                case XREALButtonType.Secondary2DAxis:
                    m_RightControllerState.secondary2DAxis.x = deltaX;
                    m_RightControllerState.secondary2DAxis.y = deltaY;
                    break;
            }
            if (m_RightXREALController != null)
                InputState.Change(m_RightXREALController, m_RightControllerState);
        }

        internal void SendHapticAxisEnd(XREALButtonType buttonType)
        {
            m_Touching = false;
            switch (buttonType)
            {
                case XREALButtonType.Primary2DAxis:
                    m_RightControllerState.primary2DAxis.x = 0;
                    m_RightControllerState.primary2DAxis.y = 0;
                    break;
                case XREALButtonType.Secondary2DAxis:
                    m_RightControllerState.secondary2DAxis.x = 0;
                    m_RightControllerState.secondary2DAxis.y = 0;
                    break;
            }
            if (m_RightXREALController != null)
                InputState.Change(m_RightXREALController, m_RightControllerState);
        }
#endif
    }
}
#endif
