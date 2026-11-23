using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using Rumbo.Ar.Domain;
using UnityEngine;
using UnityEngine.TestTools;

namespace Rumbo.Ar.Tests
{
    // Synthetic messages and a plain GameObject arrow. Does not simulate or certify an ARCore provider.
    public sealed class NavigationControllerTests
    {
        GameObject root;
        Component controller;
        Type type;
        const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        object Get(string name) => type.GetField(name, Private).GetValue(controller);
        void Set(string name, object value) => type.GetField(name, Private).SetValue(controller, value);
        void Send(string name, string value) => type.GetMethod(name, new[] { typeof(string) }).Invoke(controller, new object[] { value });

        [SetUp]
        public void SetUp()
        {
            type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Rumbo.Ar.Application.RumboArController")).First(t => t != null);
            root = new GameObject("Test controller");
            controller = root.AddComponent(type);
            ((Behaviour)controller).enabled = false; // exercise public receivers without starting camera/session services
        }

        [UnityTearDown]
        public IEnumerator TearDown() { UnityEngine.Object.Destroy(root); yield return null; }

        static T Context<T>() where T : NavigationContext, new() => new T {
            protocolVersion = 1, sessionId = "synthetic-session", siteId = "site", floorId = "floor", mapVersion = "v1"
        };
        static RouteMessage Route(int revision = 1)
        {
            var r = Context<RouteMessage>();
            r.routeId = "route"; r.revision = revision; r.originNodeId = "a"; r.destinationNodeId = "c"; r.activeNodeId = "b";
            r.nodes = new[] {
                new NavigationNode { nodeId = "a", floorId = "floor", position = new[] { 0d, 0d, 0d } },
                new NavigationNode { nodeId = "b", floorId = "floor", position = new[] { 0d, 5d, 0d } },
                new NavigationNode { nodeId = "c", floorId = "floor", position = new[] { 5d, 5d, 0d } }
            };
            return r;
        }
        GameObject Arrow()
        {
            var arrow = new GameObject("Synthetic arrow");
            arrow.transform.SetParent(root.transform);
            Set("arrow", arrow);
            return arrow;
        }

        NavigationSession Calibrate()
        {
            Send("ReceiveRoute", JsonUtility.ToJson(Route()));
            var nav = (NavigationSession)Get("navigation");
            var c = Context<CalibrationMessage>();
            c.arSpaceId = (string)Get("arSpaceId"); c.calibrationId = "cal"; c.referenceId = "ref";
            c.mapOrigin = new double[3]; c.yawDegrees = new[] { 0d }; c.metersPerMapUnit = new[] { 1d }; c.confidence = new[] { .9 };
            Assert.That(nav.Calibrate(c, c.arSpaceId, "ref", new VioSample(Now, default), Now, out var error), Is.True, error);
            return nav;
        }

        [UnityTest]
        public IEnumerator PublicReceiversPreserveArrowSessionAndProgressDuringReroute()
        {
            var nav = Calibrate();
            var arrow = Arrow();
            var reroute = Route(2); reroute.activeNodeId = "c";
            Send("ReceiveRoute", JsonUtility.ToJson(reroute));
            Assert.That(Get("arrow"), Is.SameAs(arrow));
            Assert.That(Get("navigation"), Is.SameAs(nav));
            Assert.That(nav.ActiveNodeId, Is.EqualTo("c"));
            Assert.That(nav.CalibrationId, Is.EqualTo("cal"));
            var heading = (float)Get("heading");
            Send("SetHeading", "150");
            Assert.That((float)Get("heading"), Is.EqualTo(heading));
            arrow.SetActive(true);
            var p = Context<ProgressMessage>();
            p.routeId = "route"; p.revision = 2; p.activeNodeId = "c"; p.state = "unknown"; p.capturedAtUnixMs = Now;
            Send("ReceiveProgress", JsonUtility.ToJson(p));
            Assert.That(arrow.activeSelf, Is.False);
            Assert.That(nav.CanGuide(true, true, true), Is.False);
            p.state = "navigating"; p.confidence = new[] { .9 }; p.capturedAtUnixMs++;
            yield return null;
            Send("ReceiveProgress", JsonUtility.ToJson(p));
            Assert.That(nav.CanGuide(true, true, true), Is.True);
            Assert.That(Get("arrow"), Is.SameAs(arrow));
            Assert.That(nav.SessionId, Is.EqualTo("synthetic-session"));
        }

        [UnityTest]
        public IEnumerator InsufficientSessionTrackingHidesActualArrowAndKeepsRoute()
        {
            Send("ReceiveRoute", JsonUtility.ToJson(Route()));
            var nav = (NavigationSession)Get("navigation");
            var arrow = Arrow();
            Set("currentStatus", ArTrackingStatus.Normal);
            type.GetMethod("UpdateTracking", Private).Invoke(controller, null);
            Assert.That(arrow.activeSelf, Is.False);
            Assert.That(nav.SessionId, Is.EqualTo("synthetic-session"));
            Assert.That(nav.ActiveNodeId, Is.EqualTo("b"));
            yield return null;
        }

        [UnityTest]
        public IEnumerator BleReceiverMatchesObservationArrivingBetweenVioFrames()
        {
            var nav = Calibrate();
            var cameraObject = new GameObject("Synthetic tracked camera", typeof(Camera));
            cameraObject.transform.SetParent(root.transform);
            cameraObject.transform.localPosition = new Vector3(.5f, 1.6f, 0);
            Set("arCamera", cameraObject.GetComponent<Camera>());
            Set("sessionSpace", root.transform);
            Set("currentStatus", ArTrackingStatus.Normal);
            var at = Now;
            ((VioHistory)Get("vioHistory")).Add(new VioSample(at - 30, new SpatialPoint(.5, 1.6, 0)));
            var b = Context<BleReferenceMessage>();
            b.arSpaceId = (string)Get("arSpaceId"); b.calibrationId = "cal";
            b.capturedAtUnixMs = at; b.position = new[] { 0d, 0d, 1.6 }; b.confidence = new[] { .9 };
            Send("ReceiveBleReference", JsonUtility.ToJson(b));
            Assert.That(nav.CorrectionTarget.X, Is.EqualTo(.5).Within(1e-6));
            Assert.That(nav.Correction.X, Is.Zero); // receiver sets a target, never teleports the guide
            yield return null;
        }
    }
}
