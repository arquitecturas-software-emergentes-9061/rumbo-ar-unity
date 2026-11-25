using System;

namespace Rumbo.Ar.Domain
{
    public static class HeadingAlignment
    {
        public static bool TryYaw(double mapX, double mapZ, SpatialPoint displacement, out double yaw)
        {
            yaw = 0;
            var expected = Math.Sqrt(mapX * mapX + mapZ * mapZ);
            var measured = displacement.HorizontalLength;
            if (!NavigationSession.Finite(expected) || !NavigationSession.Finite(measured) ||
                expected < 1.25 || measured < 1.25 || Math.Abs(expected - measured) > Math.Max(.65, expected * .25))
                return false;
            yaw = Signed(NavigationSession.HeadingDegrees(default, displacement) -
                NavigationSession.HeadingDegrees(default, new SpatialPoint(mapX, 0, mapZ)));
            return true;
        }

        public static double MagneticYawAdjustment(double firstMagnetic, double magnetic, double firstYaw, double yaw)
        {
            var disagreement = Signed((magnetic - firstMagnetic) - (yaw - firstYaw));
            return NavigationSession.Finite(disagreement) && Math.Abs(disagreement) <= 20 ? disagreement * .2 : 0;
        }

        public static string Instruction(double yaw)
        {
            var signed = Signed(yaw);
            if (Math.Abs(signed) >= 145) return "around";
            if (signed >= 28) return "right";
            if (signed <= -28) return "left";
            return "straight";
        }

        static double Signed(double degrees) => ((degrees + 180) % 360 + 360) % 360 - 180;
    }
}
