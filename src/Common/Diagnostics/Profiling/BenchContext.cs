using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.API.Common.Entities;

namespace VintageVisuals.Common.Diagnostics.Profiling
{
    /// <summary>
    /// D99. Everything that decides whether two bench numbers can be compared:
    /// which build, which game settings, which scene, which tier. Taken once per
    /// bench (settings do not change during one) and written beside every row.
    /// </summary>
    public sealed class BenchContext
    {
        public string Build;
        public string Gpu;
        public int Width, Height;
        public float RenderScale;
        public int VsyncMode, MaxFps, ViewDistance, ShadowQuality, SsaoQuality, GodRays;
        public string Quality;
        public string Scene;
        public string Position;
        public double HourOfDay;
        public string Started;

        /// <summary>The assembly's informational version, which carries the git hash (csproj StampGitRevision).</summary>
        public static string BuildId
        {
            get
            {
                try
                {
                    string v = typeof(BenchContext).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                    if (string.IsNullOrEmpty(v)) return "unknown";
                    int plus = v.IndexOf('+');
                    return plus >= 0 ? v.Substring(plus + 1) : v;
                }
                catch { return "unknown"; }
            }
        }

        public static BenchContext Capture(ICoreClientAPI capi, string scene)
        {
            var c = new BenchContext
            {
                Build = BuildId,
                Scene = string.IsNullOrEmpty(scene) ? "(held by hand)" : scene,
                Quality = QualityTier.Name(QualityTier.Current),
                Started = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            };
            try
            {
                c.Width = capi.Render.FrameWidth;
                c.Height = capi.Render.FrameHeight;
            }
            catch { }
            c.VsyncMode = Int(capi, "vsyncMode", -1);
            c.MaxFps = Int(capi, "maxFps", -1);
            c.ViewDistance = Int(capi, "viewDistance", -1);
            c.ShadowQuality = Int(capi, "shadowMapQuality", -1);
            c.SsaoQuality = Int(capi, "ssaoQuality", -1);
            c.GodRays = Int(capi, "godRays", -1);
            try { c.RenderScale = capi.Settings.Float["ssaa"]; } catch { c.RenderScale = float.NaN; }

            GpuTelemetry nv = GpuTelemetry.Shared;
            c.Gpu = nv?.DeviceName;
            if (string.IsNullOrEmpty(c.Gpu)) c.Gpu = GlRendererName();

            try
            {
                EntityPos pos = capi.World.Player.Entity.Pos;
                c.Position = ((int)pos.X).ToString(CultureInfo.InvariantCulture) + "," +
                             ((int)pos.Y).ToString(CultureInfo.InvariantCulture) + "," +
                             ((int)pos.Z).ToString(CultureInfo.InvariantCulture);
                c.HourOfDay = capi.World.Calendar.HourOfDay;
            }
            catch { c.Position = "?"; }
            return c;
        }

        private static int Int(ICoreClientAPI capi, string key, int fallback)
        {
            try { return capi.Settings.Int[key]; } catch { return fallback; }
        }

        private static string GlRendererName()
        {
            try
            {
                // OpenTK is the game's; resolved by name so the mod keeps no reference to it.
                Type gl = Type.GetType("OpenTK.Graphics.OpenGL.GL, OpenTK.Graphics");
                Type name = Type.GetType("OpenTK.Graphics.OpenGL.StringName, OpenTK.Graphics");
                if (gl == null || name == null) return "unknown";
                MethodInfo get = gl.GetMethod("GetString", new[] { name });
                object renderer = Enum.Parse(name, "Renderer");
                return get?.Invoke(null, new[] { renderer }) as string ?? "unknown";
            }
            catch { return "unknown"; }
        }

        /// <summary>
        /// Warnings a reader must see before trusting the numbers: a frame cap
        /// or vsync can make every run read the same.
        /// </summary>
        public string Warnings(double frameMs)
        {
            var sb = new StringBuilder();
            if (VsyncMode > 0) sb.Append("vsync is on (mode ").Append(VsyncMode).Append("): runs may all read the refresh interval. ");
            if (MaxFps > 0 && frameMs > 0 && frameMs < 1000.0 / MaxFps * 1.08)
                sb.Append("frames are at the max-fps cap (").Append(MaxFps).Append("): raise it or the differences vanish. ");
            return sb.ToString().Trim();
        }

        public string Describe()
        {
            return "build " + Build + ", " + Gpu + ", " + Width + "x" + Height +
                   " scale " + RenderScale.ToString("0.##", CultureInfo.InvariantCulture) +
                   ", view " + ViewDistance + ", shadows " + ShadowQuality + ", ssao " + SsaoQuality +
                   ", godrays " + GodRays + ", vsync " + VsyncMode + ", max fps " + MaxFps +
                   ", quality " + Quality + ", scene " + Scene;
        }
    }
}
