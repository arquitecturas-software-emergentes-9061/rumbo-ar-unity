using System;
using System.Collections.Generic;

namespace Rumbo.Ar.Domain
{
    [Serializable]
    public sealed class NavigationSettings
    {
        public double minimumConfidence = .75;
        public long maximumBleAgeMs = 2000;
        public long maximumVioSkewMs = 150;
        public long maximumProgressAgeMs = 3000;
        public double maximumResidualMeters = 2;
        public double maximumTotalCorrectionMeters = 5;
        public double correctionTimeConstantSeconds = 2;
        public double maximumCorrectionSpeed = .25;
        public double waypointRadiusMeters = .8;

        public bool IsValid => ValidConfidence(minimumConfidence) && maximumBleAgeMs > 0 && maximumBleAgeMs <= 10000 &&
            maximumVioSkewMs > 0 && maximumVioSkewMs <= maximumBleAgeMs && maximumProgressAgeMs > 0 &&
            Positive(maximumResidualMeters) && Positive(maximumTotalCorrectionMeters) &&
            Positive(correctionTimeConstantSeconds) && Positive(maximumCorrectionSpeed) && Positive(waypointRadiusMeters);
        static bool Positive(double v) => NavigationSession.Finite(v) && v > 0;
        static bool ValidConfidence(double v) => NavigationSession.Finite(v) && v > 0 && v <= 1;
    }

    // All positions and correction offsets here use the same stable AR frame, in meters.
    // Never moves XROrigin, the camera or an ARAnchor. Only the guide's map transform changes.
    public sealed class NavigationSession
    {
        readonly NavigationSettings settings;
        RouteMessage route;
        CalibrationMessage calibration;
        SpatialPoint unityOrigin;
        SpatialPoint correction, correctionTarget;
        int activeIndex;
        long lastBleAt, lastProgressAt;
        bool locationKnown;
        bool correctionEnabled;
        public NavigationSession(NavigationSettings settings)
        {
            if (settings == null || !settings.IsValid) throw new ArgumentException("invalid_navigation_settings");
            // Copy so Inspector/external mutations cannot change a running correction halfway through a step.
            this.settings = new NavigationSettings {
                minimumConfidence = settings.minimumConfidence, maximumBleAgeMs = settings.maximumBleAgeMs,
                maximumVioSkewMs = settings.maximumVioSkewMs, maximumProgressAgeMs = settings.maximumProgressAgeMs,
                maximumResidualMeters = settings.maximumResidualMeters, maximumTotalCorrectionMeters = settings.maximumTotalCorrectionMeters,
                correctionTimeConstantSeconds = settings.correctionTimeConstantSeconds,
                maximumCorrectionSpeed = settings.maximumCorrectionSpeed, waypointRadiusMeters = settings.waypointRadiusMeters
            };
        }

        public bool HasRoute => route != null;
        public bool IsCalibrated => calibration != null;
        public bool Arrived { get; private set; }
        public int ActiveIndex => activeIndex;
        public string ActiveNodeId => route?.nodes[activeIndex].nodeId;
        public string SessionId => route?.sessionId;
        public string RouteId => route?.routeId;
        public int Revision => route?.revision ?? 0;
        public string CalibrationId => calibration?.calibrationId;
        public SpatialPoint Correction => correction;
        public SpatialPoint CorrectionTarget => correctionTarget;
        public bool CanGuide(bool arTracking, bool anchorTracking, bool surfaceTracking) =>
            HasRoute && IsCalibrated && locationKnown && !Arrived && arTracking && anchorTracking && surfaceTracking;

        public static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        public static bool Id(string v) => !string.IsNullOrWhiteSpace(v) && v.Length <= 128;
        public static bool Coordinates(double[] v) => v != null && v.Length == 3 &&
            Finite(v[0]) && Finite(v[1]) && Finite(v[2]) && Math.Abs(v[0]) <= 1000000 && Math.Abs(v[1]) <= 1000000 && Math.Abs(v[2]) <= 1000000;
        static bool Scalar(double[] v) => v != null && v.Length == 1 && Finite(v[0]);
        bool Confident(double[] v) => Scalar(v) && v[0] >= settings.minimumConfidence && v[0] <= 1;
        static bool ContextValid(NavigationContext c) => c != null && c.protocolVersion == 1 &&
            Id(c.sessionId) && Id(c.siteId) && Id(c.floorId) && Id(c.mapVersion);
        static bool SameContext(NavigationContext a, NavigationContext b) => ContextValid(a) && ContextValid(b) &&
            a.sessionId == b.sessionId && a.siteId == b.siteId && a.floorId == b.floorId && a.mapVersion == b.mapVersion;
        static bool Fresh(long timestamp, long now, long age) => timestamp > 0 && timestamp <= now && now - timestamp <= age;
        static bool Fail(string code, out string error) { error = code; return false; }
        int IndexOf(string id, RouteMessage r) => Array.FindIndex(r.nodes, n => n.nodeId == id);

        public bool ReceiveRoute(RouteMessage next, out string error)
        {
            error = null;
            if (!ContextValid(next) || !Id(next.routeId) || next.revision < 1 || !Id(next.originNodeId) ||
                !Id(next.destinationNodeId) || !Id(next.activeNodeId) || next.nodes == null || next.nodes.Length < 2 || next.nodes.Length > 2048)
                return Fail("invalid_route", out error);
            var ids = new HashSet<string>();
            for (var i = 0; i < next.nodes.Length; i++)
            {
                var n = next.nodes[i];
                if (n == null || !Id(n.nodeId) || !ids.Add(n.nodeId) || n.floorId != next.floorId || !Coordinates(n.position))
                    return Fail("invalid_node_or_floor", out error);
                if (i > 0)
                {
                    var p = next.nodes[i - 1].position;
                    if (Math.Abs(p[0] - n.position[0]) + Math.Abs(p[1] - n.position[1]) < 1e-6)
                        return Fail("zero_horizontal_segment", out error);
                }
            }
            var index = IndexOf(next.activeNodeId, next);
            if (next.nodes[0].nodeId != next.originNodeId || next.nodes[next.nodes.Length - 1].nodeId != next.destinationNodeId || index < 1)
                return Fail("invalid_route_endpoints_or_target", out error);
            if (route != null && next.sessionId == route.sessionId)
            {
                if (!SameContext(route, next) || next.destinationNodeId != route.destinationNodeId)
                    return Fail("context_change_requires_new_session", out error);
                if (next.revision <= route.revision) return Fail("stale_route_revision", out error);
                // A recalculated route explicitly supplies its corresponding active node. No index from the old graph is reused.
            }
            else InvalidateCalibration();
            route = next;
            activeIndex = index;
            Arrived = false;
            lastProgressAt = 0;
            return true;
        }

        public bool Calibrate(CalibrationMessage next, string arSpaceId, string referenceId, VioSample reference, long now, out string error)
        {
            error = null;
            if (!SameContext(route, next) || next.arSpaceId != arSpaceId || !Id(next.calibrationId) ||
                next.referenceId != referenceId || !Id(referenceId) || !Fresh(reference.CapturedAtUnixMs, now, 30000) ||
                !Coordinates(next.mapOrigin) || !Scalar(next.yawDegrees) || Math.Abs(next.yawDegrees[0]) > 360 ||
                !Scalar(next.metersPerMapUnit) || next.metersPerMapUnit[0] < .0001 || next.metersPerMapUnit[0] > 1000 ||
                !Confident(next.confidence) || !Finite(reference.Position.Length))
                return Fail("invalid_calibration", out error);
            // BLE refines a live calibration. Replacing its rotation/origin would jump the guide.
            if (calibration != null) return Fail("calibration_already_set_use_ble", out error);
            calibration = next;
            unityOrigin = reference.Position;
            correction = correctionTarget = default;
            locationKnown = true;
            return true;
        }

        public void InvalidateCalibration()
        {
            calibration = null;
            correction = correctionTarget = default;
            lastBleAt = 0;
            correctionEnabled = false;
            locationKnown = false;
        }

        public void PauseCorrection()
        {
            correctionTarget = correction;
            correctionEnabled = false; // retain timestamp watermark to reject replay after recovery
        }

        public SpatialPoint MapToAr(double[] map, bool corrected = true)
        {
            if (!IsCalibrated || !Coordinates(map)) throw new InvalidOperationException("calibration_required");
            var yaw = calibration.yawDegrees[0] * Math.PI / 180;
            var scale = calibration.metersPerMapUnit[0];
            var x = (map[0] - calibration.mapOrigin[0]) * scale;
            var z = (map[1] - calibration.mapOrigin[1]) * scale;
            var y = (map[2] - calibration.mapOrigin[2]) * scale;
            return unityOrigin + new SpatialPoint(Math.Cos(yaw) * x + Math.Sin(yaw) * z, y,
                -Math.Sin(yaw) * x + Math.Cos(yaw) * z) + (corrected ? correction : default);
        }

        public SpatialPoint SegmentStart => MapToAr(route.nodes[activeIndex - 1].position);
        public SpatialPoint Target => MapToAr(route.nodes[activeIndex].position);
        public static double HeadingDegrees(SpatialPoint from, SpatialPoint to) => Math.Atan2(to.X - from.X, to.Z - from.Z) * 180 / Math.PI;

        public bool ReceiveProgress(ProgressMessage next, long now, out string error)
        {
            error = null;
            if (!SameContext(route, next) || next.routeId != route.routeId || next.revision != route.revision ||
                !Fresh(next.capturedAtUnixMs, now, settings.maximumProgressAgeMs) || next.capturedAtUnixMs <= lastProgressAt ||
                (next.state != "unknown" && next.state != "navigating" && next.state != "arrived"))
                return Fail("invalid_progress", out error);
            var index = IndexOf(next.activeNodeId, route);
            if (index < activeIndex || index < 1 || (next.state == "arrived" && index != route.nodes.Length - 1) ||
                (next.state != "unknown" && !Confident(next.confidence))) return Fail("invalid_progress_target_or_confidence", out error);
            if (Arrived && next.state != "arrived") return Fail("route_already_arrived", out error);
            lastProgressAt = next.capturedAtUnixMs;
            if (next.state == "unknown") { locationKnown = false; return true; }
            activeIndex = index;
            Arrived = next.state == "arrived";
            locationKnown = IsCalibrated;
            return true;
        }

        public bool AdvanceFromVio(SpatialPoint position, bool tracking)
        {
            if (!tracking || !IsCalibrated || !locationKnown || Arrived || !Finite(position.Length)) return false;
            // A calibrated camera is a known position. Never skip unobserved waypoints by elapsed time or by heading.
            if ((Target - position).HorizontalLength > settings.waypointRadiusMeters) return false;
            if (activeIndex == route.nodes.Length - 1) Arrived = true;
            else activeIndex++;
            return true;
        }

        public bool ReceiveBle(BleReferenceMessage next, string arSpaceId, VioSample vio, long now, bool tracking, out string error)
        {
            error = null;
            if (!tracking || !IsCalibrated) return Fail("tracking_or_calibration_required", out error);
            if (!SameContext(route, next) || next.arSpaceId != arSpaceId || next.calibrationId != CalibrationId ||
                !Coordinates(next.position) || !Confident(next.confidence) ||
                !Fresh(next.capturedAtUnixMs, now, settings.maximumBleAgeMs) || next.capturedAtUnixMs <= lastBleAt ||
                vio.CapturedAtUnixMs <= 0 || Math.Abs((double)vio.CapturedAtUnixMs - next.capturedAtUnixMs) > settings.maximumVioSkewMs ||
                !Finite(vio.Position.Length)) return Fail("invalid_ble_reference", out error);
            var baseBle = MapToAr(next.position, false);
            // Sign: moving the guide to VIO - map(BLE) aligns both estimates without moving the AR camera.
            var offset = vio.Position - baseBle;
            offset = new SpatialPoint(offset.X, 0, offset.Z); // BLE does not estimate floor height or yaw
            if ((offset - correction).Length > settings.maximumResidualMeters || offset.Length > settings.maximumTotalCorrectionMeters)
                return Fail("ble_offset_exceeds_limit", out error);
            correctionTarget = offset;
            lastBleAt = next.capturedAtUnixMs;
            correctionEnabled = true;
            locationKnown = true;
            return true;
        }

        public void TickCorrection(double seconds, long now, bool tracking)
        {
            if (!tracking || !IsCalibrated || !correctionEnabled || !Finite(seconds) || seconds <= 0 || !Fresh(lastBleAt, now, settings.maximumBleAgeMs)) return;
            var delta = correctionTarget - correction;
            var length = delta.Length;
            if (length < 1e-9) return;
            // Exponential smoothing plus a speed bound, both based on supplied time (deterministic in tests).
            var step = Math.Min(length * (1 - Math.Exp(-seconds / settings.correctionTimeConstantSeconds)), settings.maximumCorrectionSpeed * seconds);
            correction += delta * (step / length);
        }
    }
}
