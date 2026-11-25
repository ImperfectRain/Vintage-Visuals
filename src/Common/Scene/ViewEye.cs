using Vintagestory.API.MathTools;

namespace VintageVisuals.Common.Scene
{
    /// <summary>
    /// Where the eye is in the space the chunk shaders measure positions in.
    ///
    /// That space has the PLAYER's feet at 0,0,0: CameraMatrixOriginf is "the
    /// player camera matrix with the player at the origin", and chunkopaque /
    /// chunkliquid build worldPos the same way. The eye height and any
    /// third-person offset live only in the matrix, so a view vector built as
    /// normalize(-worldPos) looks from the feet, and a floor at standing height
    /// reads as edge-on (Fresnel near 1). The eye is the point the view matrix
    /// maps to the origin: -R^T t, column-major (D73 found it on water, D71
    /// applies it to terrain).
    ///
    /// Anything implausible gives zero, which is the previous behaviour.
    /// </summary>
    public static class ViewEye
    {
        public static void FromView(float[] m, Vec3f into)
        {
            into.Set(0f, 0f, 0f);
            if (m == null || m.Length < 16) return;
            float x = -(m[0] * m[12] + m[1] * m[13] + m[2] * m[14]);
            float y = -(m[4] * m[12] + m[5] * m[13] + m[6] * m[14]);
            float z = -(m[8] * m[12] + m[9] * m[13] + m[10] * m[14]);
            if (float.IsNaN(x + y + z) || x * x + y * y + z * z > 64f * 64f) return;
            into.Set(x, y, z);
        }
    }
}
