#if UNITY_INPUT_SYSTEM
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.InputSystem.Layouts;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.InputSystem.Utilities;

namespace Unity.XR.XREAL
{
    [StructLayout(LayoutKind.Explicit, Size = 38)]
    internal struct XREALSimulatorHandState : IInputStateTypeInfo
    {
        public static FourCC formatId => new FourCC('X', 'R', 'X', 'H');
        public FourCC format => formatId;

        [InputControl(name = "indexPressed", usage = "indexPressed", layout = "Button", bit = (uint)XREALButtonType.TriggerButton, offset = 0)]
        [FieldOffset(0)]
        public ushort buttons;

        [InputControl(name = "pinchStrengthIndex", usage = "pinchStrengthIndex", layout = "Axis", offset = 2)]
        [FieldOffset(2)]
        public float pinchStrengthIndex;

        [InputControl(name = "pointerPosition", usage = "pointerPosition", layout = "Vector3", offset = 6)]
        [FieldOffset(6)]
        public Vector3 pointerPosition;

        [InputControl(name = "pointerRotation", usage = "pointerRotation", layout = "Quaternion", offset = 18)]
        [FieldOffset(18)]
        public Quaternion pointerRotation;


        [InputControl(name = "handGesture", usage = "handGesture", layout = "Integer", offset = 34)]
        [FieldOffset(34)]
        public int handGesture;

        public XREALSimulatorHandState WithButton(XREALButtonType button, bool state = true)
        {
            var bit = 1 << (int)button;
            if (state)
                buttons |= (ushort)bit;
            else
                buttons &= (ushort)~bit;
            return this;
        }

        public bool HasButton(XREALButtonType button)
        {
            var bit = 1 << (int)button;
            return (buttons & bit) != 0;
        }

        public XREALSimulatorHandState WithPosition(Vector3 position)
        {
            pointerPosition = position;
            return this;
        }

        public XREALSimulatorHandState WithRotation(Quaternion rotation)
        {
            pointerRotation = rotation;
            return this;
        }
    }
}
#endif
