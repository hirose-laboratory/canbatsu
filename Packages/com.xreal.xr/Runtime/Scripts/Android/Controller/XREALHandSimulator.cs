#if UNITY_INPUT_SYSTEM && XR_HANDS
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit.Inputs;
using UnityEngine.XR.Interaction.Toolkit.Inputs.Simulation;

namespace Unity.XR.XREAL
{
    public class XREALHandSimulator : MonoBehaviour
    {
#if UNITY_EDITOR
        [Tooltip("If 'Hand Pinch Action Reference' and 'Hand Grip Action Reference' are not manually assigned, \r\nthis script will automatically retrieve the InputActionReference named 'Pinch' and 'Grab' \r\nfrom the 'simulatedHandExpressions' list in XRDeviceSimulator.")]
        [SerializeField]
        private InputActionReference handPinchActionReference, handGripActionReference;

        private XROrigin mXROrigin;
        private XRDeviceSimulator mSimulator;

        private XRHandTrackingEvents mRightHandTrackingEvent;
        private XRHandTrackingEvents mLeftHandTrackingEvent;

        private XREALSimulatorHandState mLeftHandState;
        private XREALSimulatorHandState mRightHandState;
        private XREALHandTracking mLeftHand;
        private XREALHandTracking mRightHand;

        private void Start()
        {
            mRightHand = InputSystem.AddDevice<XREALHandTracking>("RightHand");
            InputSystem.SetDeviceUsage(mRightHand, CommonUsages.RightHand);
            mLeftHand = InputSystem.AddDevice<XREALHandTracking>("LeftHand");
            InputSystem.SetDeviceUsage(mLeftHand, CommonUsages.LeftHand);
            mXROrigin = XREALUtility.FindAnyObjectByType<XROrigin>();

            if (mXROrigin)
            {
                var xrInputModalityManager = mXROrigin.gameObject.GetComponent<XRInputModalityManager>();
                if (xrInputModalityManager != null)
                {
                    if (xrInputModalityManager.rightHand != null)
                    {
                        Transform rightHandRoot = xrInputModalityManager.rightHand.transform;
                        int childCount = rightHandRoot.childCount;
                        for (int i = 0; i < childCount; i++)
                        {
                            mRightHandTrackingEvent = rightHandRoot.GetChild(i).GetComponent<XRHandTrackingEvents>();
                            if (mRightHandTrackingEvent != null)
                            {
                                mRightHandTrackingEvent.jointsUpdated.AddListener(OnRightHandJointsUpdate);
                                break;
                            }
                        }
                    }
                    if (xrInputModalityManager.leftHand != null)
                    {
                        Transform leftHandRoot = xrInputModalityManager.leftHand.transform;
                        int childCount = leftHandRoot.childCount;
                        for (int i = 0; i < childCount; i++)
                        {
                            mLeftHandTrackingEvent = leftHandRoot.GetChild(i).GetComponent<XRHandTrackingEvents>();
                            if (mLeftHandTrackingEvent != null)
                            {
                                mLeftHandTrackingEvent.jointsUpdated.AddListener(OnLeftHandJointsUpdate);
                                break;
                            }
                        }
                    }
                }
            }

            mSimulator = XRDeviceSimulator.instance;
            if (mSimulator != null)
            {
                var handExpressions = mSimulator.simulatedHandExpressions;
                if (handExpressions != null)
                {
                    for (int i = 0; i < handExpressions.Count; i++)
                    {
                        if (handPinchActionReference == null && handExpressions[i].name.Equals("Pinch"))
                        {
                            handPinchActionReference = handExpressions[i].toggleAction;
                            continue;
                        }
                        if (handGripActionReference == null && handExpressions[i].name.Equals("Grab"))
                        {
                            handGripActionReference = handExpressions[i].toggleAction;
                            continue;
                        }
                    }
                }

                if (handPinchActionReference != null)
                {
                    handPinchActionReference.action.performed += PinchActionPerformed;
                }
                if (handGripActionReference != null)
                {
                    handGripActionReference.action.performed += GripActionPerformed;
                }
            }
        }

        private void PinchActionPerformed(InputAction.CallbackContext obj)
        {
            if (mSimulator.manipulatingLeftDevice)
            {
                bool state = mLeftHandState.HasButton(XREALButtonType.TriggerButton);
                mLeftHandState.WithButton(XREALButtonType.TriggerButton, !state);
                InputState.Change(mLeftHand, mLeftHandState);

            }
            else if (mSimulator.manipulatingRightDevice)
            {
                bool state = mRightHandState.HasButton(XREALButtonType.TriggerButton);
                mRightHandState.WithButton(XREALButtonType.TriggerButton, !state);
                InputState.Change(mRightHand, mRightHandState);
            }
        }

        private void GripActionPerformed(InputAction.CallbackContext context)
        {
            if (mSimulator.manipulatingLeftDevice)
            {
                bool state = mLeftHandState.HasButton(XREALButtonType.TriggerButton);
                mLeftHandState.WithButton(XREALButtonType.TriggerButton, !state);
                InputState.Change(mLeftHand, mLeftHandState);

            }
            else if (mSimulator.manipulatingRightDevice)
            {
                bool state = mRightHandState.HasButton(XREALButtonType.TriggerButton);
                mRightHandState.WithButton(XREALButtonType.TriggerButton, !state);
                InputState.Change(mRightHand, mRightHandState);
            }
        }

        private void OnRightHandJointsUpdate(XRHandJointsUpdatedEventArgs args)
        {
            Pose pointerPose = CalculatePointerPose(args.hand);
            mRightHandState = mRightHandState.WithPosition(pointerPose.position);
            mRightHandState = mRightHandState.WithRotation(pointerPose.rotation);
            InputState.Change(mRightHand, mRightHandState);
        }

        private void OnLeftHandJointsUpdate(XRHandJointsUpdatedEventArgs args)
        {
            Pose pointerPose = CalculatePointerPose(args.hand);
            mLeftHandState = mLeftHandState.WithPosition(pointerPose.position);
            mLeftHandState = mLeftHandState.WithRotation(pointerPose.rotation);
            InputState.Change(mLeftHand, mLeftHandState);
        }

        private Pose CalculatePointerPose(XRHand hand)
        {
            hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out Pose wristPose);
            hand.GetJoint(XRHandJointID.MiddleProximal).TryGetPose(out Pose middleProximalPose);
            hand.GetJoint(XRHandJointID.RingProximal).TryGetPose(out Pose ringProximalPose);

            Vector3 middleToRing = (middleProximalPose.position - ringProximalPose.position).normalized;
            Vector3 middleToWrist = (middleProximalPose.position - wristPose.position).normalized;
            Vector3 middleToCenter = Vector3.Cross(middleToWrist, middleToRing).normalized;

            Vector3 handPosition = middleProximalPose.position + middleToWrist * 0.02f + middleToCenter * (hand.handedness == Handedness.Left ? -0.06f : 0.06f);
            Vector3 pointerPosition = handPosition + middleToRing * 0.01f;

            Vector3 neckOffset = new Vector3(0, -0.15f, 0);
            Vector3 neckPosition = Vector3.zero + Vector3.Lerp(mXROrigin.Camera.transform.rotation * neckOffset, neckOffset, 0.5f);

            Vector3 shoulderOffst = new Vector3(hand.handedness == Handedness.Left ? -0.15f : 0.15f, 0, 0);
            Vector3 cameraEuler = mXROrigin.Camera.transform.eulerAngles;
            cameraEuler.x = cameraEuler.z = 0;
            Vector3 shoulderPosition = neckPosition + Quaternion.Euler(cameraEuler) * shoulderOffst;

            float distance = pointerPosition.y - shoulderPosition.y;
            Vector3 reflectedWristPosition = pointerPosition;
            float ratio = Mathf.Min(Mathf.Abs(distance) / 0.3f, 1);
            reflectedWristPosition.y -= 1.5f * ratio * distance;

            Vector3 rayOrigin = Vector3.Lerp(reflectedWristPosition, shoulderPosition, 0.6f);
            hand.GetJoint(XRHandJointID.IndexProximal).TryGetPose(out Pose indexPose);
            Vector3 rootDir = (indexPose.position - rayOrigin).normalized;
            Vector3 subDir = (handPosition - wristPose.position).normalized;
            Vector3 pointerDirection = Vector3.Slerp(rootDir, subDir, 0.70f);
            Quaternion pointerRotation = Quaternion.LookRotation(pointerDirection);

            return new Pose
            {
                position = pointerPosition,
                rotation = pointerRotation
            };
        }
#endif
    }
}
#endif
