using System;

namespace Rumbo.Ar.Domain
{
    // Protocol v1. Arrays make omitted coordinates/scalars distinguishable from valid zeroes.
    [Serializable]
    public class NavigationContext
    {
        public int protocolVersion;
        public string sessionId;
        public string siteId;
        public string floorId;
        public string mapVersion;
    }

    [Serializable]
    public sealed class NavigationNode
    {
        public string nodeId;
        public string floorId;
        public double[] position; // map [x, y, elevation], in map units
    }

    [Serializable]
    public sealed class RouteMessage : NavigationContext
    {
        public string routeId;
        public int revision;
        public string originNodeId;
        public string destinationNodeId;
        public string activeNodeId; // target, never the origin
        public NavigationNode[] nodes;
    }

    [Serializable]
    public sealed class CalibrationMessage : NavigationContext
    {
        public string arSpaceId;
        public string calibrationId;
        public string referenceId;
        public double[] mapOrigin; // physical camera position at the referenced VIO sample
        public double[] yawDegrees; // one value: map +y -> Unity horizontal, clockwise from +z
        public double[] metersPerMapUnit; // one positive value
        public double[] confidence; // one value in [0,1]
    }

    [Serializable]
    public sealed class ProgressMessage : NavigationContext
    {
        public string routeId;
        public int revision;
        public string activeNodeId;
        public string state; // navigating | arrived | unknown
        public long capturedAtUnixMs;
        public double[] confidence;
    }

    [Serializable]
    public sealed class PositionFixMessage : NavigationContext
    {
        public string nodeId;
        public long capturedAtUnixMs;
        public double[] position;
        public double[] metersPerMapUnit;
    }

    [Serializable]
    public sealed class BleReferenceMessage : NavigationContext
    {
        public string arSpaceId;
        public string calibrationId;
        public long capturedAtUnixMs;
        public double[] position; // device position, not the beacon's installed position
        public double[] confidence;
    }

    public readonly struct SpatialPoint
    {
        public readonly double X, Y, Z;
        public SpatialPoint(double x, double y, double z) { X = x; Y = y; Z = z; }
        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);
        public double HorizontalLength => Math.Sqrt(X * X + Z * Z);
        public static SpatialPoint operator +(SpatialPoint a, SpatialPoint b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static SpatialPoint operator -(SpatialPoint a, SpatialPoint b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static SpatialPoint operator *(SpatialPoint a, double b) => new(a.X * b, a.Y * b, a.Z * b);
    }

    public readonly struct VioSample
    {
        public readonly long CapturedAtUnixMs;
        public readonly SpatialPoint Position;
        public VioSample(long timestamp, SpatialPoint position) { CapturedAtUnixMs = timestamp; Position = position; }
    }
}
