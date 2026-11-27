using System;
using Vintagestory.API.MathTools;

namespace VintageVisuals.Reflections.Mirrors
{
    /// <summary>
    /// The geometry of a planar mirror camera, in doubles, column-major 4x4
    /// (the layout of <see cref="Mat4d"/> and of every matrix the game hands
    /// to GL: element [col * 4 + row]).
    ///
    /// Everything is in the CAMERA-RELATIVE space the chunk renderer draws in
    /// (world minus <c>EntityPlayer.CameraPos</c>), so a reflected view is the
    /// game's own <c>CameraMatrixOrigin</c> times a reflection, and every
    /// world-space input the terrain shader has (positions, shadow coordinates,
    /// fog distance) keeps its meaning in the mirror pass.
    /// </summary>
    public static class MirrorMath
    {
        /// <summary>
        /// Reflection across the plane n.x + w = 0 (n unit): x - 2 (n.x + w) n.
        /// </summary>
        public static double[] Reflection(double nx, double ny, double nz, double w)
        {
            double[] r = Mat4d.Create();
            r[0] = 1 - 2 * nx * nx; r[1] = -2 * nx * ny;    r[2] = -2 * nx * nz;    r[3] = 0;
            r[4] = -2 * ny * nx;    r[5] = 1 - 2 * ny * ny; r[6] = -2 * ny * nz;    r[7] = 0;
            r[8] = -2 * nz * nx;    r[9] = -2 * nz * ny;    r[10] = 1 - 2 * nz * nz; r[11] = 0;
            r[12] = -2 * w * nx;    r[13] = -2 * w * ny;    r[14] = -2 * w * nz;    r[15] = 1;
            return r;
        }

        public static double[] Mul(double[] a, double[] b)
        {
            double[] o = new double[16];
            Mat4d.Multiply(o, a, b);
            return o;
        }

        public static double[] Inverse(double[] a)
        {
            double[] o = new double[16];
            Mat4d.Invert(o, a);
            return o;
        }

        public static double[] Copy(double[] a)
        {
            double[] o = new double[16];
            Array.Copy(a, o, 16);
            return o;
        }

        /// <summary>m * (x, y, z, w).</summary>
        public static void Transform(double[] m, double x, double y, double z, double w,
                                     out double ox, out double oy, out double oz, out double ow)
        {
            ox = m[0] * x + m[4] * y + m[8] * z + m[12] * w;
            oy = m[1] * x + m[5] * y + m[9] * z + m[13] * w;
            oz = m[2] * x + m[6] * y + m[10] * z + m[14] * w;
            ow = m[3] * x + m[7] * y + m[11] * z + m[15] * w;
        }

        /// <summary>
        /// Maps the screen rectangle [u0,u1] x [v0,v1] (0..1 uv) to the whole
        /// viewport: post-multiplied into a projection, it draws only that part
        /// of the frame, at whatever resolution the viewport has.
        /// </summary>
        public static double[] Crop(double u0, double v0, double u1, double v1)
        {
            double sx = 1.0 / Math.Max(1e-6, u1 - u0);
            double sy = 1.0 / Math.Max(1e-6, v1 - v0);
            double cx = (u0 + u1) - 1.0;   // centre of the rect in NDC
            double cy = (v0 + v1) - 1.0;
            double[] c = Mat4d.Create();
            c[0] = sx; c[5] = sy;
            c[12] = -cx * sx; c[13] = -cy * sy;
            return c;
        }

        /// <summary>
        /// Oblique near plane (Lengyel 2005, general form): replaces the
        /// projection's near plane by the view-space plane <paramref name="c"/>
        /// (kept side c.x >= 0, the camera outside it), leaving x and y - and
        /// so every pixel position - exactly as they were. This is what stops
        /// the geometry BEHIND a mirror (the wall it hangs on, the floor under
        /// a mirrored floor) from being drawn in front of the reflection,
        /// without a clip plane in a shader the game owns.
        /// </summary>
        public static double[] Oblique(double[] projection, double[] c)
        {
            double[] p = Copy(projection);
            double[] inv = Inverse(p);
            Transform(inv, Math.Sign(c[0]), Math.Sign(c[1]), 1.0, 1.0, out double qx, out double qy, out double qz, out double qw);
            double cq = c[0] * qx + c[1] * qy + c[2] * qz + c[3] * qw;
            if (Math.Abs(cq) < 1e-12) return p;
            double r4q = p[3] * qx + p[7] * qy + p[11] * qz + p[15] * qw;
            double a = 2.0 * r4q / cq;
            p[2] = a * c[0] - p[3];
            p[6] = a * c[1] - p[7];
            p[10] = a * c[2] - p[11];
            p[14] = a * c[3] - p[15];
            return p;
        }

        /// <summary>
        /// A plane (n, w) given in the space a matrix maps FROM, expressed in
        /// the space it maps TO: (M^-1)^T applied to the plane vector.
        /// </summary>
        public static double[] TransformPlane(double[] m, double nx, double ny, double nz, double w)
        {
            double[] inv = Inverse(m);
            // plane' [j] = sum_i plane[i] * inv[row i, col j] = sum_i plane[i] * inv[j*4 + i]
            double[] pl = { nx, ny, nz, w };
            double[] o = new double[4];
            for (int j = 0; j < 4; j++)
            {
                o[j] = pl[0] * inv[j * 4 + 0] + pl[1] * inv[j * 4 + 1] + pl[2] * inv[j * 4 + 2] + pl[3] * inv[j * 4 + 3];
            }
            return o;
        }

        public static float[] ToFloat(double[] m)
        {
            float[] f = new float[16];
            for (int i = 0; i < 16; i++) f[i] = (float)m[i];
            return f;
        }
    }
}
