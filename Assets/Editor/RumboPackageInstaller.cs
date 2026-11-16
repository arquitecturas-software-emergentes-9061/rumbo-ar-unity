using System;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace Rumbo.Editor
{
    public static class RumboPackageInstaller
    {
        static AddAndRemoveRequest request;
        static double deadline;

        public static void Install()
        {
            request = Client.AddAndRemove(
                packagesToAdd: new[] {
                    "com.unity.xr.management",
                    "com.unity.xr.arfoundation",
                    "com.unity.xr.arcore"
                },
                packagesToRemove: Array.Empty<string>());
            deadline = EditorApplication.timeSinceStartup + 600;
            EditorApplication.update += Poll;
        }

        static void Poll()
        {
            if (!request.IsCompleted && EditorApplication.timeSinceStartup < deadline) return;
            EditorApplication.update -= Poll;
            if (!request.IsCompleted || request.Status != StatusCode.Success)
            {
                Debug.LogError("Rumbo AR package installation failed: " + request.Error?.message);
                EditorApplication.Exit(1);
                return;
            }
            Debug.Log("Rumbo AR packages resolved.");
            EditorApplication.Exit(0);
        }
    }
}
