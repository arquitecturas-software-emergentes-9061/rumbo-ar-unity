using System;
using System.Collections.Generic;
using Rumbo.Ar.Domain;
using Rumbo.Ar.Infrastructure;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.XR.CoreUtils;

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
        [SerializeField] NavigationSettings navigationSettings = new();

        NavigationSession navigation;
        readonly VioHistory vioHistory = new();
        string arSpaceId;
        string calibrationReferenceId;
        VioSample calibrationReference;
        PositionFixMessage firstFix;
        VioSample firstPose;
        double magneticBias;
        double firstMagnetic, firstCameraYaw;
        long lastPositionFixAt;
        int largeResiduals;
        string lastInstruction;
        readonly List<Canvas> demoCanvases = new();
        Transform sessionSpace;
        Matrix4x4 anchorInSession;
        bool hadNavigationAnchor;
        string guideState;
        const float SurfaceHeightTolerance = .25f;
        [SerializeField, Min(.01f)] float guideMoveSpeed = 3f;
        [SerializeField, Min(1f)] float guideTurnSpeed = 180f;
        SpatialPoint displayedPoint;
        bool hasDisplayedPoint;

        void Awake()
        {
            gameObject.name = FlutterArBridge.ReceiverObjectName;
            arSpaceId = Guid.NewGuid().ToString("N");
            navigation = new NavigationSession(navigationSettings);
        }

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
            sessionSpace = anchorManager.GetComponent<XROrigin>().TrackablesParent;
            statusChangedAt = Time.realtimeSinceStartup;
            noPlaneSince = statusChangedAt;
            FlutterArBridge.Report("started", "demo_direction_only");
            FlutterArBridge.Report("navigation_ready", $"protocolVersion=1;arSpaceId={arSpaceId}");
            FlutterArBridge.AttachAndroidPlayer();
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
            if (navigation.HasRoute)
            {
                UpdateNavigation();
                return; // taps and manual heading only apply in the preserved demo mode
            }
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
                state == ARSessionState.SessionTracking && ARSession.notTrackingReason == NotTrackingReason.None ? ArTrackingStatus.Normal :
                state == ARSessionState.None ? ArTrackingStatus.Lost : ArTrackingStatus.Limited;
            // Check the AR session state even when its coarse tracking category did not change.
            if (navigation.IsCalibrated && (state == ARSessionState.Ready || state == ARSessionState.None || state == ARSessionState.Unsupported))
                ResetSpatialReference("ar_session_reset");
            if (next == currentStatus) return;
            var now = Time.realtimeSinceStartup;
            var elapsed = now - statusChangedAt;
            FlutterArBridge.Report(next.ToString().ToLowerInvariant(),
                $"reason={ARSession.notTrackingReason};previous_seconds={elapsed:F2}");
            currentStatus = next;
            statusChangedAt = now;
            if (next != ArTrackingStatus.Normal)
            {
                HideArrow("tracking_unreliable");
                vioHistory.Clear();
                calibrationReferenceId = null;
                firstFix = null;
                navigation.PauseCorrection();
            }
        }

        public void PlaceAt(Vector2 screenPoint)
        {
            if (navigation.HasRoute) return;
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
            if (navigation.HasRoute || !NavigationSession.Finite(value)) return;
            heading = Mathf.Repeat(value, 360f);
            if (arrow != null) arrow.transform.localRotation = Quaternion.Euler(0f, heading, 0f);
            FlutterArBridge.Report("demo_heading_changed", heading.ToString("F0"));
        }

        public void Exit() => FlutterArBridge.ReturnToFlutter();

        [UnityEngine.Scripting.Preserve]
        public void ResetDemo(string degrees)
        {
            ClearAnchor();
            navigation = new NavigationSession(navigationSettings);
            ResetSpatialReference("demo");
            foreach (var canvas in demoCanvases)
                if (canvas != null) canvas.gameObject.SetActive(true);
            demoCanvases.Clear();
            SetHeading(degrees);
        }

        [UnityEngine.Scripting.Preserve]
        public void ReceiveRoute(string json)
        {
            if (!FlutterArBridge.TryRead<RouteMessage>(json, out var message, out var error) || !navigation.ReceiveRoute(message, out error))
            { Reject("route", error); return; }
            // Keep one anchor and one arrow across reroutes in the same calibrated session.
            if (!navigation.IsCalibrated)
            {
                ClearAnchor();
                vioHistory.Clear();
                calibrationReferenceId = null;
                firstFix = null;
                lastPositionFixAt = 0;
                magneticBias = 0;
                largeResiduals = 0;
                lastInstruction = null;
            }
            HideArrow("route_updated");
            UpdateModeLabel();
            ReportNavigation("route_accepted", navigation.IsCalibrated ? "" : "calibration_required");
            if (VioAvailable) FlutterArBridge.Report("normal", "route_attached");
        }

        [UnityEngine.Scripting.Preserve]
        public void CaptureCalibrationReference(string referenceId)
        {
            if (!NavigationSession.Id(referenceId) || !navigation.HasRoute || !VioAvailable || navigation.IsCalibrated)
            { Reject("calibration", "route_and_tracking_required_or_already_calibrated"); return; }
            calibrationReferenceId = referenceId;
            calibrationReference = new VioSample(UtcNowMs, CameraInStableSpace());
            var p = calibrationReference.Position;
            // The existing reportStatus(state, reason, timestamp) transports this JSON reason unchanged.
            var sample = new CalibrationReferenceEvent {
                arSpaceId = arSpaceId, referenceId = referenceId, sessionId = navigation.SessionId,
                capturedAtUnixMs = calibrationReference.CapturedAtUnixMs,
                unityPosition = new[] { p.X, p.Y, p.Z },
                cameraYawDegrees = arCamera.transform.eulerAngles.y - sessionSpace.eulerAngles.y
            };
            FlutterArBridge.Report("calibration_reference", JsonUtility.ToJson(sample));
        }

        [Serializable]
        sealed class CalibrationReferenceEvent
        {
            public int protocolVersion = 1;
            public string arSpaceId, referenceId, sessionId;
            public long capturedAtUnixMs;
            public double[] unityPosition;
            public float cameraYawDegrees;
        }

        [UnityEngine.Scripting.Preserve]
        public void ReceiveCalibration(string json)
        {
            if (!VioAvailable) { Reject("calibration", "tracking_required"); return; }
            if (!FlutterArBridge.TryRead<CalibrationMessage>(json, out var message, out var error) ||
                !navigation.Calibrate(message, arSpaceId, calibrationReferenceId, calibrationReference, UtcNowMs, out error))
            { Reject("calibration", error); return; }
            calibrationReferenceId = null;
            ReportNavigation("calibration_accepted");
        }

        [UnityEngine.Scripting.Preserve]
        public void ReceiveProgress(string json)
        {
            if (!FlutterArBridge.TryRead<ProgressMessage>(json, out var message, out var error) ||
                !navigation.ReceiveProgress(message, UtcNowMs, out error)) { Reject("progress", error); return; }
            if (message.state == "unknown" || navigation.Arrived) HideArrow(message.state);
            ReportNavigation("progress_accepted", message.state);
        }

        [UnityEngine.Scripting.Preserve]
        public void ReceiveBleReference(string json)
        {
            if (!FlutterArBridge.TryRead<BleReferenceMessage>(json, out var message, out var error)) { Reject("ble", error); return; }
            var now = UtcNowMs;
            // A message can arrive between Update calls. Bracket its capture time with the latest tracked pose,
            // otherwise a low-latency reference newer than the preceding frame would always be rejected.
            if (VioAvailable) vioHistory.Add(new VioSample(now, CameraInStableSpace()));
            if (!vioHistory.TryAt(message.capturedAtUnixMs, navigationSettings.maximumVioSkewMs, out var sample))
            { Reject("ble", "vio_sample_unavailable"); return; }
            if (!navigation.ReceiveBle(message, arSpaceId, sample, now, VioAvailable, out error)) { Reject("ble", error); return; }
            ReportNavigation("ble_accepted");
        }

        [UnityEngine.Scripting.Preserve]
        public void ReceivePositionFix(string json)
        {
            if (!FlutterArBridge.TryRead<PositionFixMessage>(json, out var fix, out _) ||
                !navigation.Matches(fix) || !NavigationSession.Id(fix.nodeId) ||
                !NavigationSession.Coordinates(fix.position) ||
                fix.metersPerMapUnit == null || fix.metersPerMapUnit.Length != 1 ||
                !VioAvailable || fix.capturedAtUnixMs <= lastPositionFixAt) return;

            var scale = fix.metersPerMapUnit[0];
            var now = UtcNowMs;
            if (!NavigationSession.Finite(scale) || scale < .0001 || scale > 1000 ||
                fix.capturedAtUnixMs > now || now - fix.capturedAtUnixMs > 2000) return;
            vioHistory.Add(new VioSample(now, CameraInStableSpace()));
            if (!vioHistory.TryAt(fix.capturedAtUnixMs, navigationSettings.maximumVioSkewMs, out var pose)) return;
            lastPositionFixAt = fix.capturedAtUnixMs;

            if (navigation.IsCalibrated)
            {
                var reference = new BleReferenceMessage {
                    protocolVersion = 1, sessionId = fix.sessionId, siteId = fix.siteId,
                    floorId = fix.floorId, mapVersion = fix.mapVersion, arSpaceId = arSpaceId,
                    calibrationId = navigation.CalibrationId, capturedAtUnixMs = fix.capturedAtUnixMs,
                    position = fix.position, confidence = new[] { .8 }
                };
                if (navigation.ReceiveBle(reference, arSpaceId, pose, now, true, out var error))
                    largeResiduals = 0;
                else if (error == "ble_offset_exceeds_limit" && ++largeResiduals >= 3)
                    ResetSpatialReference("persistent_drift");
                return;
            }

            var magnetic = FlutterArBridge.MagneticHeading();
            var cameraYaw = CameraYaw();
            if (firstFix == null || fix.capturedAtUnixMs - firstFix.capturedAtUnixMs > 45000)
            {
                firstFix = fix;
                firstPose = pose;
                firstMagnetic = magnetic;
                firstCameraYaw = cameraYaw;
                ReportNavigation("calibration_walk");
                return;
            }
            if (fix.nodeId == firstFix.nodeId) return;
            var dx = (fix.position[0] - firstFix.position[0]) * scale;
            var dz = (fix.position[1] - firstFix.position[1]) * scale;
            if (!HeadingAlignment.TryYaw(dx, dz, pose.Position - firstPose.Position, out var yaw))
                return;
            if (!TryCameraHeight(out var height)) return;
            magneticBias = HeadingAlignment.MagneticYawAdjustment(firstMagnetic, magnetic, firstCameraYaw, cameraYaw);
            var referenceId = Guid.NewGuid().ToString("N");
            var origin = (double[])fix.position.Clone();
            origin[2] += height / scale;
            var calibration = new CalibrationMessage {
                protocolVersion = 1, sessionId = fix.sessionId, siteId = fix.siteId, floorId = fix.floorId,
                mapVersion = fix.mapVersion, arSpaceId = arSpaceId, calibrationId = referenceId,
                referenceId = referenceId, mapOrigin = origin, yawDegrees = new[] { yaw },
                metersPerMapUnit = new[] { scale }, confidence = new[] { .8 }
            };
            if (navigation.Calibrate(calibration, arSpaceId, referenceId, pose, now, out _))
            {
                firstFix = null;
                ReportNavigation("calibration_accepted");
            }
        }

        bool TryCameraHeight(out double height)
        {
            height = 0;
            var cameraPosition = arCamera.transform.position;
            if (!raycastManager.Raycast(new Ray(cameraPosition, Vector3.down), hits, TrackableType.PlaneWithinPolygon))
                return false;
            foreach (var hit in hits)
            {
                var plane = planeManager.GetPlane(hit.trackableId);
                var distance = cameraPosition.y - hit.pose.position.y;
                if (plane != null && plane.alignment == PlaneAlignment.HorizontalUp &&
                    plane.trackingState == TrackingState.Tracking && distance >= .75f && distance <= 2.3f)
                    height = Math.Max(height, distance);
            }
            return height > 0;
        }

        double CameraYaw()
        {
            var cameraForward = arCamera.transform.forward;
            var forward = hadNavigationAnchor && anchor != null
                ? anchorInSession.MultiplyVector(anchor.transform.InverseTransformDirection(cameraForward))
                : sessionSpace.InverseTransformDirection(cameraForward);
            return NavigationSession.HeadingDegrees(default, Point(forward));
        }

        // Explicitly required after AR reset/lost anchor or a floor/context change. Keeps route and destination.
        [UnityEngine.Scripting.Preserve]
        public void InvalidateCalibration(string reason) => ResetSpatialReference("mobile_requested:" + reason);

        bool VioAvailable => currentStatus == ArTrackingStatus.Normal && arCamera != null && sessionSpace != null &&
            (sessionSpace.lossyScale - Vector3.one).sqrMagnitude < .000001f &&
            (!hadNavigationAnchor || (anchor != null && anchor.trackingState == TrackingState.Tracking));
        static long UtcNowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        static SpatialPoint Point(Vector3 p) => new(p.x, p.y, p.z);
        static Vector3 Vector(SpatialPoint p) => new((float)p.X, (float)p.Y, (float)p.Z);

        SpatialPoint CameraInStableSpace()
        {
            if (anchor != null && hadNavigationAnchor)
                return Point(anchorInSession.MultiplyPoint3x4(anchor.transform.InverseTransformPoint(arCamera.transform.position)));
            return Point(sessionSpace.InverseTransformPoint(arCamera.transform.position));
        }

        Vector3 StableToWorld(SpatialPoint p) => anchor != null && hadNavigationAnchor
            ? anchor.transform.TransformPoint(anchorInSession.inverse.MultiplyPoint3x4(Vector(p)))
            : sessionSpace.TransformPoint(Vector(p));

        void UpdateNavigation()
        {
            if (hadNavigationAnchor && anchor == null) { ResetSpatialReference("navigation_anchor_removed"); return; }
            if (!VioAvailable)
            {
                vioHistory.Clear();
                navigation.PauseCorrection();
                SetGuideState("tracking_unreliable");
                return;
            }
            var now = UtcNowMs;
            var camera = CameraInStableSpace();
            vioHistory.Add(new VioSample(now, camera));
            if (!navigation.IsCalibrated) { SetGuideState("calibration_required"); return; }
            navigation.TickCorrection(Math.Min(Time.unscaledDeltaTime, .1f), now, true);
            if (navigation.AdvanceFromVio(camera, true)) ReportNavigation(navigation.Arrived ? "arrived" : "segment_changed");
            if (navigation.Arrived) { SetGuideState("arrived"); return; }
            if (!navigation.CanGuide(true, true, true)) { SetGuideState("location_unknown"); return; }

            var start = navigation.SegmentStart;
            var target = navigation.Target;
            var segment = target - start;
            var lengthSquared = segment.X * segment.X + segment.Z * segment.Z;
            var along = ((camera.X - start.X) * segment.X + (camera.Z - start.Z) * segment.Z) / lengthSquared;
            // Put the arrow on the real active segment, up to 1m ahead of the projected camera position.
            var t = Math.Max(0, Math.Min(.95, along + 1 / Math.Sqrt(lengthSquared)));
            var desiredPoint = start + segment * t;
            if (hasDisplayedPoint)
            {
                var delta = desiredPoint - displayedPoint;
                if (delta.Length > 1e-6)
                    desiredPoint = displayedPoint + delta * Math.Min(1, guideMoveSpeed * Math.Min(Time.unscaledDeltaTime, .1f) / delta.Length);
            }
            var desired = StableToWorld(desiredPoint);
            var worldTarget = StableToWorld(target);
            if (!raycastManager.Raycast(new Ray(desired + Vector3.up * .5f, Vector3.down), hits, TrackableType.PlaneWithinPolygon))
            { SetGuideState("no_surface_at_segment"); return; }
            ARPlane surface = null;
            Pose pose = default;
            foreach (var hit in hits)
            {
                var candidate = planeManager.GetPlane(hit.trackableId);
                if (candidate == null || candidate.alignment != PlaneAlignment.HorizontalUp || candidate.trackingState != TrackingState.Tracking ||
                    Mathf.Abs(hit.pose.position.y - desired.y) > SurfaceHeightTolerance) continue;
                surface = candidate;
                pose = hit.pose;
                break;
            }
            if (surface == null) { SetGuideState("no_surface_at_floor_height"); return; }
            if (anchor == null)
            {
                try { anchor = anchorManager.AttachAnchor(surface, pose); }
                catch (Exception e) { SetGuideState("anchor_unavailable:" + e.Message); return; }
                if (anchor == null) { SetGuideState("anchor_unavailable"); return; }
                anchorInSession = sessionSpace.worldToLocalMatrix * anchor.transform.localToWorldMatrix;
                hadNavigationAnchor = true;
                arrow = Instantiate(arrowPrefab, anchor.transform);
                arrow.SetActive(false);
            }
            selectedPlane = surface;
            var direction = worldTarget - desired;
            direction.y = 0;
            if (direction.sqrMagnitude < .000001f) { SetGuideState("segment_too_short"); return; }
            var rotation = Quaternion.LookRotation(direction, Vector3.up);
            if (hasDisplayedPoint) rotation = Quaternion.RotateTowards(arrow.transform.rotation, rotation, guideTurnSpeed * Math.Min(Time.unscaledDeltaTime, .1f));
            arrow.transform.SetPositionAndRotation(pose.position + Vector3.up * .025f, rotation);
            displayedPoint = desiredPoint;
            hasDisplayedPoint = true;
            var visible = navigation.CanGuide(true, anchor.trackingState == TrackingState.Tracking, selectedPlane.trackingState == TrackingState.Tracking);
            arrow.SetActive(visible);
            SetGuideState(visible ? "visible" : "anchor_or_surface_unreliable");
            if (visible)
            {
                var instruction = HeadingAlignment.Instruction(
                    NavigationSession.HeadingDegrees(camera, target) - CameraYaw() - magneticBias);
                var meters = (int)Math.Ceiling((target - camera).HorizontalLength);
                var nextInstruction = $"turn={instruction};meters={meters}";
                if (nextInstruction != lastInstruction)
                {
                    lastInstruction = nextInstruction;
                    ReportNavigation("guidance", nextInstruction);
                }
            }
        }

        void SetGuideState(string state)
        {
            if (state != "visible" && arrow != null) arrow.SetActive(false);
            if (state != "visible") lastInstruction = null;
            if (guideState == state) return;
            guideState = state;
            ReportNavigation("navigation_guide", state);
        }

        void UpdateModeLabel()
        {
            foreach (var canvas in FindObjectsByType<Canvas>(FindObjectsSortMode.None))
                if (canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                {
                    demoCanvases.Add(canvas);
                    canvas.gameObject.SetActive(false);
                }
        }

        void ResetSpatialReference(string reason)
        {
            navigation.InvalidateCalibration();
            vioHistory.Clear();
            calibrationReferenceId = null;
            firstFix = null;
            lastPositionFixAt = 0;
            largeResiduals = 0;
            magneticBias = 0;
            lastInstruction = null;
            arSpaceId = Guid.NewGuid().ToString("N");
            ClearAnchor();
            guideState = null;
            FlutterArBridge.Report("calibration_required", $"arSpaceId={arSpaceId};reason={reason}");
        }

        void Reject(string kind, string error) => ReportNavigation("navigation_error", $"message={kind};code={error}");
        void ReportNavigation(string state, string reason = "") => FlutterArBridge.Report(state,
            $"sessionId={navigation.SessionId};routeId={navigation.RouteId};revision={navigation.Revision};activeNodeId={navigation.ActiveNodeId};calibrationId={navigation.CalibrationId};{reason}");

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
            hadNavigationAnchor = false;
            hasDisplayedPoint = false;
        }
    }
}
