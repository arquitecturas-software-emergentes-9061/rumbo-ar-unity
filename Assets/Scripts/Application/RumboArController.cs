using System;
using System.Collections.Generic;
using Rumbo.Ar.Domain;
using Rumbo.Ar.Infrastructure;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace Rumbo.Ar.Application
{
    public sealed class RumboArController : MonoBehaviour
    {
        [SerializeField] ARPlaneManager planeManager;
        [SerializeField] ARSession session;
        [SerializeField] ARRaycastManager raycastManager;
        [SerializeField] ARAnchorManager anchorManager;
        [SerializeField] Camera arCamera;
        [SerializeField] GameObject arrowPrefab;

        readonly List<ARRaycastHit> hits = new();
        ARPlane selectedPlane;
        ARAnchor anchor;
        GameObject arrow;
        ArTrackingStatus currentStatus = ArTrackingStatus.Lost;
        float statusChangedAt;
        float noPlaneSince;
        float heading;

        void Start()
        {
            heading = Mathf.Repeat(FlutterArBridge.InitialHeading(), 360f);
            statusChangedAt = Time.realtimeSinceStartup;
            noPlaneSince = statusChangedAt;
            FlutterArBridge.Report("started", "demo_direction_only");
            ARSession.stateChanged += OnSessionStateChanged;
            planeManager.trackablesChanged.AddListener(OnPlanesChanged);
            EnableSessionAfterCameraPermission();
            UpdateTracking();
        }

        void EnableSessionAfterCameraPermission()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            const string cameraPermission = UnityEngine.Android.Permission.Camera;
            if (UnityEngine.Android.Permission.HasUserAuthorizedPermission(cameraPermission))
            {
                session.enabled = true;
                return;
            }
            var callbacks = new UnityEngine.Android.PermissionCallbacks();
            callbacks.PermissionGranted += _ => session.enabled = true;
            callbacks.PermissionDenied += _ => FlutterArBridge.Report("permission_denied", "camera");
            callbacks.PermissionDeniedAndDontAskAgain += _ => FlutterArBridge.Report("permission_denied", "camera_permanently");
            UnityEngine.Android.Permission.RequestUserPermission(cameraPermission, callbacks);
#else
            session.enabled = true;
#endif
        }

        void OnDestroy()
        {
            ARSession.stateChanged -= OnSessionStateChanged;
            if (planeManager != null) planeManager.trackablesChanged.RemoveListener(OnPlanesChanged);
            ClearAnchor();
        }

        void Update()
        {
            UpdateTracking();
            if (anchor != null && arrow != null)
                arrow.SetActive(currentStatus == ArTrackingStatus.Normal &&
                    anchor.trackingState == TrackingState.Tracking &&
                    selectedPlane != null && selectedPlane.trackingState == TrackingState.Tracking);
            if (currentStatus == ArTrackingStatus.Normal && planeManager.trackables.count == 0 &&
                Time.realtimeSinceStartup - noPlaneSince > 5f)
            {
                FlutterArBridge.Report("no_surface", "point_camera_at_floor_or_table");
                noPlaneSince = float.PositiveInfinity;
            }
            if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame)
            {
                if (EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject())
                    PlaceAt(Touchscreen.current.primaryTouch.position.ReadValue());
            }
            else if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            {
                if (EventSystem.current == null || !EventSystem.current.IsPointerOverGameObject())
                    PlaceAt(Mouse.current.position.ReadValue());
            }
        }

        void OnPlanesChanged(ARTrackablesChangedEventArgs<ARPlane> args)
        {
            if (args.added.Count > 0 || args.updated.Count > 0) noPlaneSince = Time.realtimeSinceStartup;
            if (selectedPlane != null && selectedPlane.trackingState != TrackingState.Tracking)
                HideArrow("selected_surface_lost");
        }

        void OnSessionStateChanged(ARSessionStateChangedEventArgs args) => UpdateTracking();

        void UpdateTracking()
        {
            var state = ARSession.state;
            var next = state == ARSessionState.Unsupported ? ArTrackingStatus.Unsupported :
                state == ARSessionState.SessionTracking ? ArTrackingStatus.Normal :
                state == ARSessionState.None ? ArTrackingStatus.Lost : ArTrackingStatus.Limited;
            if (next == currentStatus) return;
            var now = Time.realtimeSinceStartup;
            var elapsed = now - statusChangedAt;
            FlutterArBridge.Report(next.ToString().ToLowerInvariant(),
                $"reason={ARSession.notTrackingReason};previous_seconds={elapsed:F2}");
            currentStatus = next;
            statusChangedAt = now;
            if (next != ArTrackingStatus.Normal) HideArrow("tracking_unreliable");
        }

        public void PlaceAt(Vector2 screenPoint)
        {
            if (currentStatus != ArTrackingStatus.Normal ||
                !raycastManager.Raycast(screenPoint, hits, TrackableType.PlaneWithinPolygon))
            {
                FlutterArBridge.Report("placement_unavailable", "no_tracked_surface_at_touch");
                return;
            }
            var plane = planeManager.GetPlane(hits[0].trackableId);
            if (plane == null || plane.alignment != PlaneAlignment.HorizontalUp ||
                plane.trackingState != TrackingState.Tracking) return;
            ARAnchor nextAnchor;
            try { nextAnchor = anchorManager.AttachAnchor(plane, hits[0].pose); }
            catch (Exception error)
            {
                FlutterArBridge.Report("placement_unavailable", error.Message);
                return;
            }
            if (nextAnchor == null)
            {
                FlutterArBridge.Report("placement_unavailable", "anchor_not_supported");
                return;
            }
            ClearAnchor();
            selectedPlane = plane;
            anchor = nextAnchor;
            arrow = Instantiate(arrowPrefab, anchor.transform);
            arrow.transform.localPosition = new Vector3(0f, 0.025f, 0f);
            arrow.transform.localRotation = Quaternion.Euler(0f, heading, 0f);
            FlutterArBridge.Report("guide_placed", $"demo_heading={heading:F0}");
        }

        public void TurnLeft() => SetHeading(heading - 15f);
        public void TurnRight() => SetHeading(heading + 15f);
        public void SetHeading(string degrees)
        {
            if (float.TryParse(degrees, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value)) SetHeading(value);
        }
        void SetHeading(float value)
        {
            heading = Mathf.Repeat(value, 360f);
            if (arrow != null) arrow.transform.localRotation = Quaternion.Euler(0f, heading, 0f);
            FlutterArBridge.Report("demo_heading_changed", heading.ToString("F0"));
        }

        public void Exit() => FlutterArBridge.ReturnToFlutter();

        void HideArrow(string reason)
        {
            if (arrow == null || !arrow.activeSelf) return;
            arrow.SetActive(false);
            FlutterArBridge.Report("guide_hidden", reason);
        }

        void ClearAnchor()
        {
            if (arrow != null) Destroy(arrow);
            if (anchor != null && anchorManager != null) anchorManager.TryRemoveAnchor(anchor);
            arrow = null;
            anchor = null;
            selectedPlane = null;
        }
    }
}
