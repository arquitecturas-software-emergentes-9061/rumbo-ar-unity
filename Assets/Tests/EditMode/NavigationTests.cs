using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Rumbo.Ar.Domain;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Rumbo.Ar.Tests
{
    // All coordinates, routes, timestamps and BLE observations below are SYNTHETIC test fixtures.
    public sealed class NavigationTests
    {
        const long Now = 1800000000000;
        NavigationSession session;
        NavigationSettings settings;

        [SetUp]
        public void SetUp()
        {
            settings = new NavigationSettings();
            session = new NavigationSession(settings);
        }

        static T Context<T>() where T : NavigationContext, new() => new T {
            protocolVersion = 1, sessionId = "test-session", siteId = "test-site", floorId = "floor-1", mapVersion = "map-7"
        };
        static NavigationNode Node(string id, double x, double y) => new() { nodeId = id, floorId = "floor-1", position = new[] { x, y, 0d } };
        static RouteMessage Route(int revision = 1)
        {
            var r = Context<RouteMessage>();
            r.routeId = "test-route"; r.revision = revision; r.originNodeId = "a"; r.destinationNodeId = "c"; r.activeNodeId = "b";
            r.nodes = new[] { Node("a", 0, 0), Node("b", 0, 10), Node("c", 10, 10) };
            return r;
        }
        static CalibrationMessage Calibration(double yaw = 0, double scale = 1)
        {
            var c = Context<CalibrationMessage>();
            c.arSpaceId = "ar-1"; c.calibrationId = "cal-1"; c.referenceId = "ref-1";
            c.mapOrigin = new double[3]; c.yawDegrees = new[] { yaw }; c.metersPerMapUnit = new[] { scale }; c.confidence = new[] { .9 };
            return c;
        }
        void Calibrated(double yaw = 0, double scale = 1, SpatialPoint origin = default)
        {
            Assert.That(session.ReceiveRoute(Route(), out var error), Is.True, error);
            Assert.That(session.Calibrate(Calibration(yaw, scale), "ar-1", "ref-1", new VioSample(Now, origin), Now, out error), Is.True, error);
        }
        static BleReferenceMessage Ble(long at = Now, double mapX = 0, double mapY = 0)
        {
            var b = Context<BleReferenceMessage>();
            b.arSpaceId = "ar-1"; b.calibrationId = "cal-1"; b.capturedAtUnixMs = at;
            b.position = new[] { mapX, mapY, 1.6 }; b.confidence = new[] { .9 };
            return b;
        }
        static ProgressMessage Progress(string target = "b", string state = "navigating", long at = Now)
        {
            var p = Context<ProgressMessage>();
            p.routeId = "test-route"; p.revision = 1; p.activeNodeId = target; p.state = state;
            p.capturedAtUnixMs = at; p.confidence = new[] { .9 };
            return p;
        }
        static void Equal(SpatialPoint actual, double x, double y, double z)
        {
            Assert.That(actual.X, Is.EqualTo(x).Within(1e-6));
            Assert.That(actual.Y, Is.EqualTo(y).Within(1e-6));
            Assert.That(actual.Z, Is.EqualTo(z).Within(1e-6));
        }

        [Test]
        public void InitialDisplacementAlignsMapAxesAndRejectsAnInconsistentWalk()
        {
            Assert.That(HeadingAlignment.TryYaw(0, 3, new SpatialPoint(3, 0, 0), out var yaw), Is.True);
            Assert.That(yaw, Is.EqualTo(90).Within(.001));
            Assert.That(HeadingAlignment.TryYaw(0, 3, new SpatialPoint(.2, 0, 0), out _), Is.False);
            Assert.That(HeadingAlignment.TryYaw(0, 3, new SpatialPoint(7, 0, 0), out _), Is.False);
            Assert.That(HeadingAlignment.MagneticYawAdjustment(350, 15, 0, 20), Is.EqualTo(1).Within(.001));
            Assert.That(HeadingAlignment.MagneticYawAdjustment(350, 90, 0, 20), Is.Zero);
        }

        [TestCase(-50, "left")]
        [TestCase(50, "right")]
        [TestCase(355, "straight")]
        [TestCase(180, "around")]
        public void TurnsUseSignedRelativeHeading(double yaw, string expected)
        {
            Assert.That(HeadingAlignment.Instruction(yaw), Is.EqualTo(expected));
        }

        [Test]
        public void MapTransformUsesDistinctOriginScaleYawAndElevation()
        {
            Calibrated(90, 2, new SpatialPoint(4, 3, 5));
            Equal(session.MapToAr(new[] { 1d, 2d, 3d }), 8, 9, 3);
            Assert.That(NavigationSession.HeadingDegrees(session.SegmentStart, session.Target), Is.EqualTo(90).Within(1e-6));
        }

        [Test]
        public void MapOriginNeedNotBeZero()
        {
            session.ReceiveRoute(Route(), out _);
            var c = Calibration(); c.mapOrigin = new[] { 100d, -20d, 1.6 };
            Assert.That(session.Calibrate(c, "ar-1", "ref-1", new VioSample(Now, new SpatialPoint(4, 2, 8)), Now, out _), Is.True);
            Equal(session.MapToAr(new[] { 101d, -18d, 0d }), 5, .4, 10);
        }

        [Test]
        public void CannotRenderOrTransformBeforeCalibration()
        {
            Assert.That(session.ReceiveRoute(Route(), out _), Is.True);
            Assert.That(session.CanGuide(true, true, true), Is.False);
            Assert.Throws<InvalidOperationException>(() => session.MapToAr(new double[3]));
        }

        [TestCase("empty")][TestCase("null")][TestCase("floor")][TestCase("nan")][TestCase("missing_position")]
        [TestCase("duplicate")][TestCase("zero_segment")][TestCase("origin")][TestCase("destination")]
        [TestCase("target")][TestCase("protocol")][TestCase("site")][TestCase("map")][TestCase("session")]
        public void InvalidRoutesAreRejectedAtomically(string defect)
        {
            Calibrated(); var r = Route(2);
            switch (defect)
            {
                case "empty": r.nodes = Array.Empty<NavigationNode>(); break;
                case "null": r = null; break;
                case "floor": r.nodes[1].floorId = "floor-2"; break;
                case "nan": r.nodes[1].position[0] = double.NaN; break;
                case "missing_position": r.nodes[1].position = null; break;
                case "duplicate": r.nodes[1].nodeId = "a"; break;
                case "zero_segment": r.nodes[1].position = new double[3]; break;
                case "origin": r.originNodeId = "missing"; break;
                case "destination": r.destinationNodeId = "a"; break;
                case "target": r.activeNodeId = "a"; break;
                case "protocol": r.protocolVersion = 2; break;
                case "site": r.siteId = ""; break;
                case "map": r.mapVersion = ""; break;
                case "session": r.sessionId = ""; break;
            }
            Assert.That(session.ReceiveRoute(r, out _), Is.False);
            Assert.That(session.Revision, Is.EqualTo(1));
            Assert.That(session.IsCalibrated, Is.True);
        }

        [Test]
        public void VioReachesWaypointThenTurnsWithoutRestart()
        {
            Calibrated();
            Assert.That(session.AdvanceFromVio(new SpatialPoint(0, 1.6, 10), true), Is.True);
            Assert.That(session.ActiveNodeId, Is.EqualTo("c"));
            Assert.That(session.SessionId, Is.EqualTo("test-session"));
            Assert.That(NavigationSession.HeadingDegrees(session.SegmentStart, session.Target), Is.EqualTo(90));
            Assert.That(session.AdvanceFromVio(new SpatialPoint(10, 1.6, 10), true), Is.True);
            Assert.That(session.Arrived, Is.True);
            Assert.That(session.CanGuide(true, true, true), Is.False);
        }

        [Test]
        public void UnknownLocationAndTrackingLossCannotInventProgress()
        {
            Calibrated();
            Assert.That(session.AdvanceFromVio(new SpatialPoint(0, 0, 10), false), Is.False);
            Assert.That(session.ReceiveProgress(Progress(state: "unknown"), Now, out _), Is.True);
            Assert.That(session.AdvanceFromVio(new SpatialPoint(0, 0, 10), true), Is.False);
            Assert.That(session.CanGuide(true, true, true), Is.False);
            Assert.That(session.ActiveNodeId, Is.EqualTo("b"));
        }

        [Test]
        public void ReroutePreservesCalibrationAndUsesExplicitCorrespondingTarget()
        {
            Calibrated();
            session.AdvanceFromVio(new SpatialPoint(0, 0, 10), true);
            var r = Route(2); r.routeId = "recalculated"; r.activeNodeId = "c";
            r.nodes[1] = Node("other", 5, 2);
            Assert.That(session.ReceiveRoute(r, out _), Is.True);
            Assert.That(session.ActiveNodeId, Is.EqualTo("c"));
            Assert.That(session.CalibrationId, Is.EqualTo("cal-1"));
            Assert.That(session.ReceiveRoute(Route(), out _), Is.False);
        }

        [Test]
        public void FloorChangeRequiresNewSessionAndNewCalibration()
        {
            Calibrated(); var r = Route(2); r.floorId = "floor-2";
            foreach (var n in r.nodes) n.floorId = r.floorId;
            Assert.That(session.ReceiveRoute(r, out _), Is.False);
            r.sessionId = "new-session";
            Assert.That(session.ReceiveRoute(r, out _), Is.True);
            Assert.That(session.IsCalibrated, Is.False);
            Assert.That(session.CanGuide(true, true, true), Is.False);
        }

        [TestCase("space")][TestCase("reference")][TestCase("stale")][TestCase("scale")]
        [TestCase("yaw")][TestCase("confidence")][TestCase("missing")]
        public void CalibrationRejectsInvalidOrMissingData(string defect)
        {
            session.ReceiveRoute(Route(), out _); var c = Calibration(); var at = Now;
            switch (defect)
            {
                case "space": c.arSpaceId = "wrong"; break;
                case "reference": c.referenceId = "wrong"; break;
                case "stale": at -= 30001; break;
                case "scale": c.metersPerMapUnit[0] = 0; break;
                case "yaw": c.yawDegrees[0] = double.PositiveInfinity; break;
                case "confidence": c.confidence[0] = .2; break;
                case "missing": c.yawDegrees = null; break;
            }
            Assert.That(session.Calibrate(c, "ar-1", "ref-1", new VioSample(at, default), Now, out _), Is.False);
        }

        [Test]
        public void BleComputesOffsetWithCorrectSignInRotatedScaledFrame()
        {
            Calibrated(90, 2, new SpatialPoint(4, 3, 5));
            var b = Ble(mapX: 1, mapY: 2);
            Assert.That(session.ReceiveBle(b, "ar-1", new VioSample(Now, new SpatialPoint(8.5, 20, 2.75)), Now, true, out _), Is.True);
            Equal(session.CorrectionTarget, .5, 0, -.25);
            Equal(session.Correction, 0, 0, 0);
        }

        [TestCase("stale")][TestCase("future")][TestCase("floor")][TestCase("map")][TestCase("session")]
        [TestCase("space")][TestCase("calibration")][TestCase("confidence")][TestCase("nan")]
        [TestCase("skew")][TestCase("tracking")][TestCase("large")]
        public void BleRejectsUnreliableReferencesWithoutChangingCorrection(string defect)
        {
            Calibrated(); var b = Ble(); var vio = new VioSample(Now, new SpatialPoint(.5, 1.6, 0)); var tracking = true;
            switch (defect)
            {
                case "stale": b.capturedAtUnixMs -= 2001; break;
                case "future": b.capturedAtUnixMs++; break;
                case "floor": b.floorId = "other"; break;
                case "map": b.mapVersion = "other"; break;
                case "session": b.sessionId = "other"; break;
                case "space": b.arSpaceId = "other"; break;
                case "calibration": b.calibrationId = "other"; break;
                case "confidence": b.confidence[0] = .1; break;
                case "nan": b.position[0] = double.NaN; break;
                case "skew": vio = new VioSample(Now - 151, default); break;
                case "tracking": tracking = false; break;
                case "large": vio = new VioSample(Now, new SpatialPoint(2.01, 0, 0)); break;
            }
            Assert.That(session.ReceiveBle(b, "ar-1", vio, Now, tracking, out _), Is.False);
            Equal(session.CorrectionTarget, 0, 0, 0);
        }

        [Test]
        public void CorrectionIsGradualSpeedBoundedAndFreezesWithoutFreshBle()
        {
            Calibrated(); session.ReceiveBle(Ble(), "ar-1", new VioSample(Now, new SpatialPoint(1, 1.6, 0)), Now, true, out _);
            session.TickCorrection(.1, Now + 100, true);
            Assert.That(session.Correction.X, Is.GreaterThan(0).And.LessThanOrEqualTo(.025));
            var frozen = session.Correction.X;
            session.TickCorrection(5, Now + 5000, true);
            Assert.That(session.Correction.X, Is.EqualTo(frozen));
            session.TickCorrection(.1, Now + 101, false);
            Assert.That(session.Correction.X, Is.EqualTo(frozen));
        }

        [Test]
        public void RecoveryRequiresNewBleAndPreservesRouteProgress()
        {
            Calibrated(); session.AdvanceFromVio(new SpatialPoint(0, 0, 10), true);
            session.ReceiveBle(Ble(), "ar-1", new VioSample(Now, new SpatialPoint(.5, 1.6, 0)), Now, true, out _);
            session.TickCorrection(.1, Now + 100, true);
            session.PauseCorrection(); var before = session.Correction.X;
            session.TickCorrection(.1, Now + 200, true);
            Assert.That(session.Correction.X, Is.EqualTo(before));
            var at = Now + 10000;
            Assert.That(session.ReceiveBle(Ble(at), "ar-1", new VioSample(at, new SpatialPoint(.6, 1.6, 0)), at, true, out _), Is.True);
            session.TickCorrection(.1, at + 100, true);
            Assert.That(session.Correction.X, Is.GreaterThan(before));
            Assert.That(session.ActiveNodeId, Is.EqualTo("c"));
            Assert.That(session.SessionId, Is.EqualTo("test-session"));
            Assert.That(session.CalibrationId, Is.EqualTo("cal-1"));
        }

        [Test]
        public void LongSyntheticWalkWithGradualDriftAndBeaconGapIsBounded()
        {
            Calibrated();
            for (var i = 0; i < 36000; i++) // one hour, 10 Hz; gap from 10 to 20 minutes
            {
                var at = Now + i * 100L;
                var mapX = i * .02;
                var drift = Math.Min(4, i * .0002);
                if (i % 10 == 0 && (i < 6000 || i > 12000))
                    Assert.That(session.ReceiveBle(Ble(at, mapX), "ar-1", new VioSample(at, new SpatialPoint(mapX + drift, 1.6, 0)), at, true, out _), Is.True);
                var before = session.Correction;
                session.TickCorrection(.1, at, true);
                Assert.That((session.Correction - before).Length, Is.LessThanOrEqualTo(.0250001));
                Assert.That(session.Correction.Length, Is.LessThanOrEqualTo(5));
            }
            Assert.That(session.Correction.X, Is.EqualTo(4).Within(.01));
            Assert.That(session.SessionId, Is.EqualTo("test-session"));
        }

        [Test]
        public void RepeatedOffsetsCannotExceedTotalCorrectionLimit()
        {
            Calibrated();
            for (var i = 0; i < 1000; i++)
            {
                var at = Now + i * 100L;
                var offset = session.Correction.X + .5;
                var accepted = session.ReceiveBle(Ble(at), "ar-1", new VioSample(at, new SpatialPoint(offset, 0, 0)), at, true, out _);
                Assert.That(accepted, Is.EqualTo(offset <= 5));
                session.TickCorrection(.1, at, true);
                Assert.That(session.Correction.X, Is.LessThanOrEqualTo(5));
            }
        }

        [Test]
        public void DuplicateBleAndOutOfOrderProgressAreRejected()
        {
            Calibrated(); var b = Ble(); var v = new VioSample(Now, default);
            Assert.That(session.ReceiveBle(b, "ar-1", v, Now, true, out _), Is.True);
            Assert.That(session.ReceiveBle(b, "ar-1", v, Now, true, out _), Is.False);
            session.PauseCorrection();
            Assert.That(session.ReceiveBle(b, "ar-1", v, Now, true, out _), Is.False);
            Assert.That(session.ReceiveProgress(Progress("c"), Now, out _), Is.True);
            Assert.That(session.ReceiveProgress(Progress("b", at: Now + 1), Now + 1, out _), Is.False);
            Assert.That(session.ReceiveProgress(Progress("c", at: Now - 1), Now, out _), Is.False);
        }

        [TestCase("stale")][TestCase("future")][TestCase("confidence")][TestCase("revision")]
        [TestCase("floor")][TestCase("false_arrival")][TestCase("target")]
        public void InvalidProgressCannotChangeTarget(string defect)
        {
            Calibrated(); var p = Progress();
            switch (defect)
            {
                case "stale": p.capturedAtUnixMs -= 3001; break;
                case "future": p.capturedAtUnixMs++; break;
                case "confidence": p.confidence[0] = .1; break;
                case "revision": p.revision++; break;
                case "floor": p.floorId = "other"; break;
                case "false_arrival": p.state = "arrived"; break;
                case "target": p.activeNodeId = "absent"; break;
            }
            Assert.That(session.ReceiveProgress(p, Now, out _), Is.False);
            Assert.That(session.ActiveNodeId, Is.EqualTo("b"));
            Assert.That(session.Arrived, Is.False);
        }

        [TestCase(false, true, true)][TestCase(true, false, true)][TestCase(true, true, false)]
        public void AnyInsufficientTrackingHidesGuideAndRecoveryRestoresIt(bool ar, bool anchor, bool plane)
        {
            Calibrated();
            Assert.That(session.CanGuide(ar, anchor, plane), Is.False);
            Assert.That(session.CanGuide(true, true, true), Is.True);
        }

        [Test]
        public void ResetCalibrationPreservesDestinationAndActiveNode()
        {
            Calibrated(); session.AdvanceFromVio(new SpatialPoint(0, 0, 10), true);
            session.InvalidateCalibration();
            Assert.That(session.ActiveNodeId, Is.EqualTo("c"));
            Assert.That(session.CanGuide(true, true, true), Is.False);
            Assert.That(session.Calibrate(Calibration(), "ar-1", "ref-1", new VioSample(Now, default), Now, out _), Is.True);
            Assert.That(session.ActiveNodeId, Is.EqualTo("c"));
        }

        [Test]
        public void HistoryInterpolatesCaptureTimeAndRejectsGapsAndExtrapolation()
        {
            var h = new VioHistory();
            h.Add(new VioSample(Now, default)); h.Add(new VioSample(Now + 100, new SpatialPoint(2, 0, 0)));
            Assert.That(h.TryAt(Now + 50, 150, out var sample), Is.True); Equal(sample.Position, 1, 0, 0);
            Assert.That(h.TryAt(Now - 1, 150, out _), Is.False);
            Assert.That(h.TryAt(Now + 101, 150, out _), Is.False);
            h.Add(new VioSample(Now + 1000, default));
            Assert.That(h.TryAt(Now + 500, 150, out _), Is.False);
            h.Clear(); Assert.That(h.TryAt(Now, 150, out _), Is.False);
        }

        [Test]
        public void SameInputAndDeltaTimeProduceDeterministicCorrection()
        {
            Calibrated(); var other = new NavigationSession(settings);
            other.ReceiveRoute(Route(), out _); other.Calibrate(Calibration(), "ar-1", "ref-1", new VioSample(Now, default), Now, out _);
            for (var i = 0; i < 100; i++)
            {
                var at = Now + i * 100;
                var sample = new VioSample(at, new SpatialPoint(.1 * Math.Sin(i), 0, 0));
                session.ReceiveBle(Ble(at), "ar-1", sample, at, true, out _);
                other.ReceiveBle(Ble(at), "ar-1", sample, at, true, out _);
                session.TickCorrection(.1, at, true); other.TickCorrection(.1, at, true);
                Assert.That(session.Correction.X, Is.EqualTo(other.Correction.X));
            }
        }

        static Type RuntimeType(string name) => AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(name)).First(t => t != null);
        static bool ReadRoute(string json, out RouteMessage route)
        {
            var bridge = RuntimeType("Rumbo.Ar.Infrastructure.FlutterArBridge");
            var args = new object[] { json, null, null };
            var result = (bool)bridge.GetMethod("TryRead").MakeGenericMethod(typeof(RouteMessage)).Invoke(null, args);
            route = (RouteMessage)args[1]; return result;
        }

        [Test]
        public void BridgeReceivesTypedRouteAndRejectsMalformedOrMissingCoordinates()
        {
            Assert.That(ReadRoute(JsonUtility.ToJson(Route()), out var route), Is.True);
            Assert.That(session.ReceiveRoute(route, out _), Is.True);
            Assert.That(ReadRoute("{bad json", out _), Is.False);
            Assert.That(ReadRoute("{}", out _), Is.False);
            // Test the missing numeric array directly rather than relying on JsonUtility's numeric string format.
            var invalid = Route(2); invalid.nodes[1].position = null;
            Assert.That(ReadRoute(JsonUtility.ToJson(invalid), out route), Is.True);
            Assert.That(session.ReceiveRoute(route, out _), Is.False);
        }

        [Test]
        public void PrototypeSceneKeepsSerializedDependenciesAndPublicReceivers()
        {
            var scene = EditorSceneManager.OpenScene("Assets/Scenes/RumboArPrototype.unity", OpenSceneMode.Additive);
            try
            {
                var controllerType = RuntimeType("Rumbo.Ar.Application.RumboArController");
                var controller = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren(controllerType, true)).Single();
                var so = new SerializedObject(controller);
                foreach (var field in new[] { "planeManager", "session", "raycastManager", "anchorManager", "arCamera", "arrowPrefab" })
                    Assert.That(so.FindProperty(field).objectReferenceValue, Is.Not.Null, field);
                foreach (var receiver in new[] { "ReceiveRoute", "CaptureCalibrationReference", "ReceiveCalibration", "ReceiveProgress", "ReceiveBleReference", "InvalidateCalibration", "SetHeading" })
                    Assert.That(controllerType.GetMethod(receiver, new[] { typeof(string) }), Is.Not.Null, receiver);
                Assert.That(so.FindProperty("navigationSettings"), Is.Not.Null);
            }
            finally { EditorSceneManager.CloseScene(scene, true); }
        }
    }
}
