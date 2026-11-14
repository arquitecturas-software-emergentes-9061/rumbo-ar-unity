using System;
using System.IO;
using UnityEngine;

namespace Rumbo.Ar.Infrastructure
{
    public static class FlutterArBridge
    {
        public static void Report(string state, string reason = "")
        {
            var timestamp = DateTime.UtcNow.ToString("O");
            Debug.Log($"[RumboAR] {timestamp} {state} {reason}");
            try
            {
                var path = Path.Combine(UnityEngine.Application.persistentDataPath, "rumbo_ar_tracking.csv");
                if (!File.Exists(path)) File.AppendAllText(path, "timestamp_utc,state,reason\n");
                File.AppendAllText(path, $"\"{timestamp}\",\"{Cell(state)}\",\"{Cell(reason)}\"\n");
            }
            catch (Exception error) { Debug.LogWarning("AR log unavailable: " + error.Message); }
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var bridge = new AndroidJavaClass("com.uitopic.rumbo.RumboArBridge");
                bridge.CallStatic("reportLogPath", Path.Combine(UnityEngine.Application.persistentDataPath, "rumbo_ar_tracking.csv"));
                bridge.CallStatic("reportStatus", state, reason, timestamp);
            }
            catch (Exception error) { Debug.LogWarning("Flutter bridge unavailable: " + error.Message); }
#endif
        }

        static string Cell(string value) => value.Replace("\r", " ").Replace("\n", " ").Replace("\"", "\"\"");

        public static float InitialHeading()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                using var intent = activity.Call<AndroidJavaObject>("getIntent");
                return intent.Call<float>("getFloatExtra", "rumbo_demo_heading", 0f);
            }
            catch (Exception error) { Debug.LogWarning("Demo heading unavailable: " + error.Message); }
#endif
            return 0f;
        }

        public static void ReturnToFlutter()
        {
            Report("closed");
#if UNITY_ANDROID && !UNITY_EDITOR
            using var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using var activity = player.GetStatic<AndroidJavaObject>("currentActivity");
            activity.Call("finish");
#endif
        }
    }
}
