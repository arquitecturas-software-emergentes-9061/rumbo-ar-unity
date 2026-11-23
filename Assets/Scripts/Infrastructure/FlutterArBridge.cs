using System;
using System.IO;
using Rumbo.Ar.Domain;
using UnityEngine;

namespace Rumbo.Ar.Infrastructure
{
    public static class FlutterArBridge
    {
        public const string ReceiverObjectName = "RumboArController";

        // Called by existing controller receivers, via UnityPlayer.UnitySendMessage on Android's main thread.
        // No new Java methods or channels are assumed.
        public static bool TryRead<T>(string json, out T message, out string error) where T : NavigationContext
        {
            message = null;
            error = "invalid_json";
            if (string.IsNullOrWhiteSpace(json) || json.Length > 524288 ||
                !json.TrimStart().StartsWith("{", StringComparison.Ordinal) || !json.TrimEnd().EndsWith("}", StringComparison.Ordinal)) return false;
            try
            {
                message = JsonUtility.FromJson<T>(json);
                if (message == null || message.protocolVersion != 1) { error = "unsupported_protocol"; return false; }
                error = null;
                return true;
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
            {
                return false;
            }
        }

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
