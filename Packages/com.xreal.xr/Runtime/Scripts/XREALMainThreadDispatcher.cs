using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Unity.XR.XREAL
{
    /// <summary>
    /// A singleton MonoBehaviour class that dispatches actions to be executed on the Unity main thread.
    /// </summary>
    public class XREALMainThreadDispatcher : SingletonMonoBehaviour<XREALMainThreadDispatcher>
    {
        ConcurrentQueue<Action> m_Actions = new ConcurrentQueue<Action>();
        ConcurrentQueue<Action> m_RunningActions = new ConcurrentQueue<Action>();

        /// <summary>
        /// Event invoked every frame during the Update method.
        /// </summary>
        public static event Action OnUpdate;

        /// <summary>
        /// Queues an action to be executed on the Unity main thread in the next frame.
        /// </summary>
        /// <param name="action">The action to execute on the main thread.</param>
        public void QueueOnMainThread(Action action)
        {
            m_Actions.Enqueue(action);
        }

        /// <summary>
        /// Gets whether the application is currently paused in background (Android only)
        /// </summary>
        public bool IsPaused { get; private set; } = false;

        /// <summary>
        /// Queues an action to be executed on the Unity main thread after a specified delay.
        /// </summary>
        /// <param name="action">The action to execute on the main thread.</param>
        /// <param name="delaySeconds">The delay in seconds before the action is executed.</param>
        /// <param name="ctSource">Optional cancellation token source to cancel the scheduled action. 
        /// When null, the action cannot be canceled after being scheduled.</param>
        public void QueueOnMainThreadWithDelay(Action action, float delaySeconds, CancellationTokenSource ctSource = null)
        {
            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
                if (ctSource == null || !ctSource.IsCancellationRequested)
                    m_Actions.Enqueue(action);
            });
        }

        void Update()
        {
            OnUpdate?.Invoke();
            if (m_Actions.Count > 0)
            {
                (m_Actions, m_RunningActions) = (m_RunningActions, m_Actions);
                while (m_RunningActions.TryDequeue(out var action))
                {
                    try
                    {
                        action.Invoke();
                    }
                    catch (Exception e)
                    {
                        Debug.LogException(e);
                    }
                }
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        void OnApplicationPause(bool pause)
        {
            Debug.Log($"[XREALMainThreadDispatcher] OnApplicationPause: {pause}");
            IsPaused = pause;
            if (pause)
            {
                XREALPlugin.PauseSession();
            }
            else
            {
                XREALPlugin.ResumeSession();
            }
        }
#endif
    }
}
