using System;

namespace Kalo.BlamHost
{
    /// <summary>
    /// A cutscene camera point's orientation. Kalo uses yaw, pitch (up is positive) and roll applied in that order. The tag's
    /// three angles (a, b, c) build the rotation Rx(c) * Ry(-b) * Rz(a), checked against reach_tag_test's debug camera.
    /// </summary>
    internal static class CameraAngles
    {
        private const double Deg = Math.PI / 180;

        public static float[] ToTag(float yaw, float pitch, float roll)
        {
            double cY = Math.Cos(yaw * Deg), sY = Math.Sin(yaw * Deg), cP = Math.Cos(pitch * Deg), sP = Math.Sin(pitch * Deg), cR = Math.Cos(roll * Deg), sR = Math.Sin(roll * Deg);
            double fx = cY * cP;
            double lx = cR * -sY + sR * (-cY * sP);
            double ux = -sR * -sY + cR * (-cY * sP), uy = -sR * cY + cR * (-sY * sP), uz = cR * cP;
            return new[]
            {
                (float)(Math.Atan2(-lx, fx) / Deg),
                (float)(Math.Asin(Math.Max(-1, Math.Min(1, -ux))) / Deg),
                (float)(Math.Atan2(-uy, uz) / Deg),
            };
        }

        public static float[] FromTag(float tagA, float tagB, float tagC)
        {
            double ca = Math.Cos(tagA * Deg), sa = Math.Sin(tagA * Deg), cb = Math.Cos(tagB * Deg), sb = Math.Sin(tagB * Deg), cc = Math.Cos(tagC * Deg), sc = Math.Sin(tagC * Deg);
            // Columns of the rotation: forward (first), left (second), up (third); only these rows are needed.
            double fx = cb * ca;
            double fy = cc * sa - sc * sb * ca, fz = sc * sa + cc * sb * ca;
            double lz = sc * ca + cc * -sb * sa, uz = cc * cb;
            double yaw = Math.Atan2(fy, fx) / Deg;
            return new[]
            {
                (float)(yaw < 0 ? yaw + 360 : yaw),
                (float)(Math.Asin(Math.Max(-1, Math.Min(1, fz))) / Deg),
                (float)(Math.Atan2(lz, uz) / Deg),
            };
        }
    }
}
