using System;
using System.IO;
using System.Text.RegularExpressions;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// Anything a shader uses to reconstruct a world position must be sampled
    /// every frame.
    ///
    /// A shader only ever sees camera-relative coordinates. Every field that has
    /// to stay put on the ground - rain ripples, the cloud shadow window, the
    /// noise fallback - rebuilds a world position as
    ///
    ///     cameraRelativePos + CameraPosition
    ///
    /// where the first term changes every frame and the second is a uniform.
    /// Sample that uniform on a throttle and the sum drifts with the player
    /// between samples, then snaps back. The effect is unmistakable and was
    /// reported twice in the same words: the field "moves with the player".
    ///
    /// It has now been written three times. EnvironmentTracker sampled the
    /// camera on its 0.1s tick, so rain ripples swam across the ground.
    /// CloudTileReader recomputed the shadow window's corner inside a 4 Hz
    /// throttle, so the whole cloud field stepped four times a second. And
    /// before both, SceneInputs was pushed from Apply() and so only moved when
    /// a config slider did.
    ///
    /// The rule that came out of it: the expensive part of a sampler may be
    /// throttled, the ANCHOR may not. These checks pin the two places where the
    /// anchor lives, because the throttle is always the tempting thing to wrap
    /// one more line in.
    /// </summary>
    public static class WorldAnchorChecks
    {
        public static void Run(string repo, Action<string, bool, string> check)
        {
            CheckCameraIsPerFrame(repo, check);
            CheckCloudCornerIsPerFrame(repo, check);
            CheckCloudFieldContrast(repo, check);
        }

        /// <summary>
        /// EnvironmentTracker must sample the camera before its tick gate.
        ///
        /// Matched on ORDER rather than mere presence: the call existing
        /// somewhere in the method is exactly what was true when it was broken -
        /// it sat inside SampleObserver, on the far side of the early return.
        /// </summary>
        private static void CheckCameraIsPerFrame(string repo, Action<string, bool, string> check)
        {
            string source = File.ReadAllText(Path.Combine(repo, "src/Common/Scene/EnvironmentTracker.cs"));

            int frame = source.IndexOf("public void OnRenderFrame(", StringComparison.Ordinal);
            check("EnvironmentTracker.OnRenderFrame was found", frame >= 0, "");
            if (frame < 0) return;

            string body = source.Substring(frame,
                source.IndexOf("\n        }", frame, StringComparison.Ordinal) - frame);

            int sample = body.IndexOf("SampleCamera()", StringComparison.Ordinal);
            int gate = body.IndexOf("_sinceTick", StringComparison.Ordinal);

            check("the camera is sampled every frame", sample >= 0, "SampleCamera() is not called at all");

            check("the camera is sampled BEFORE the tick gate", sample >= 0 && gate >= 0 && sample < gate,
                "a throttled camera makes every world-anchored field swim with the player");

            // The expensive half must stay on the tick, or this trades one bug
            // for a chunk query every frame.
            int observer = source.IndexOf("private void SampleObserver()", StringComparison.Ordinal);
            if (observer < 0) return;

            string observerBody = source.Substring(observer,
                source.IndexOf("\n        }", observer, StringComparison.Ordinal) - observer);

            check("the light-level lookup stays out of the per-frame path",
                !body.Contains("SampleObserver()") && observerBody.Contains("GetLightLevel"),
                "SampleObserver does a chunk query and belongs on the tick");

            // Ambient.BlendedCloudDensity read 0 through a whole storm in game
            // (2026-11-23), so flecks drew under it. Falling rain stands in.
            // The cloud-tile mean must not: it read the same under a storm
            // and under a sunny broken sky, and hid every fleck at noon.
            check("cloud cover counts falling rain and not the cloud-tile mean",
                source.Contains("_cloudCover = Math.Max(ambient, GameMath.Clamp(_precipitation, 0f, 1f));") &&
                !source.Contains("Math.Max(ambient, _tileCover)"),
                "a dead ambient figure reads a storm as clear; the tile mean reads a sunny noon as overcast");

            // Dust motes are lit only inside a beam: spawned only where the
            // leaf map says a real gap sends sun, only under a clear sky, and
            // capped so they stay a hint of dust. No world query of their own.
            string motes = File.ReadAllText(Path.Combine(repo, "src/PseudoPBR/DustMoteSpawner.cs"));
            check("dust motes spawn only in real sun shafts, under a clear sky, capped and off at zero",
                motes.Contains("if (!InShaft(map, x, y, z, sun)) continue;") &&
                motes.Contains("world.CloudCover > CloudLimit") &&
                motes.Contains("if (strength < 0.001f) return;") &&
                motes.Contains("_alive < cap") &&
                motes.Contains("return gapDensity <= 2 && around >= 2;") &&
                !motes.Contains("BlockAccessor") && !motes.Contains("WeatherSystem"),
                "a mote outside a beam is snow, and a spawner that asks the world is a second tracker");
            // INTERACTION: motes thicken where beams do. The spawner mirrors
            // the shader's humid-air term; both are evaluated, the shader's
            // from the shipped GLSL, and must agree.
            try
            {
                string terrain = File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/shadersnippets/pbrterrainmaterial.glsl"));
                string shaft = GlslEval.StripComments(GlslEval.FunctionBody(terrain, "float vvTerrainShaftLevel(") ?? "");
                string lowExpr = GlslEval.Rhs(GlslEval.Statement(shaft, "float lowSun"));
                string humidExpr = GlslEval.Rhs(GlslEval.Statement(shaft, "float humid"));
                bool ok = motes.Contains("BeamHumidity(world.Wetness, sun.Y)");
                string detail = "";
                foreach (double wet in new[] { 0.0, 0.5, 1.0 })
                foreach (double y in new[] { 0.1, 0.3, 0.45, 0.8 })
                {
                    var syms = GlslEval.Syms("l", GlslEval.Vec(0, y, 0), "vv_sceneWetness", wet, "fog", 0.0);
                    syms["lowSun"] = new GlslEval.Val(GlslEval.Scalar(lowExpr, syms));
                    double gpu = GlslEval.Scalar(humidExpr, syms);
                    double cpu = VintageVisuals.PseudoPBR.DustMoteSpawner.BeamHumidity((float)wet, (float)y);
                    if (Math.Abs(gpu - cpu) > 1e-4) { ok = false; detail += $" wet={wet} y={y}: shader {gpu:0.000}, motes {cpu:0.000}"; }
                }
                check("I-motes thicken with the beams: the spawner's humid air agrees with the shaft shader", ok, detail);
            }
            catch (Exception ex) { check("I-motes humid air is readable", false, ex.Message); }
        }

        /// <summary>
        /// The cloud window's corner must update on every Update, not only on
        /// the frames that re-read the tiles.
        /// </summary>
        /// <summary>
        /// A uniform cloud field casts no shadow; a broken one keeps its
        /// clouds. Seen in game 2026-11-23: a sunny cirrocumulus noon read
        /// every tile at 0.86 and the world sat in cloud shadow.
        /// </summary>
        private static void CheckCloudFieldContrast(string repo, Action<string, bool, string> check)
        {
            float[] uniform = new float[64];
            for (int i = 0; i < uniform.Length; i++) uniform[i] = 0.86f;
            VintageVisuals.Weather.CloudTileReader.ApplyFieldContrast(uniform);
            float uniformMax = 0f;
            foreach (float v in uniform) uniformMax = Math.Max(uniformMax, v);

            float[] broken = new float[64];
            for (int i = 0; i < broken.Length; i++) broken[i] = i % 2 == 0 ? 0.86f : 0f;
            VintageVisuals.Weather.CloudTileReader.ApplyFieldContrast(broken);

            string source = File.ReadAllText(Path.Combine(repo, "src/Weather/CloudTileReader.cs"));
            check("a uniform cloud field casts no cloud shadow and a broken one keeps its clouds",
                uniformMax < 1e-6f && Math.Abs(broken[0] - 0.86f) < 1e-4f && broken[1] == 0f &&
                source.Contains("if (!clear) ApplyFieldContrast(_target);"),
                "uniform " + uniformMax + ", broken cloud " + broken[0] + " gap " + broken[1]);
        }

        private static void CheckCloudCornerIsPerFrame(string repo, Action<string, bool, string> check)
        {
            string source = File.ReadAllText(Path.Combine(repo, "src/Weather/CloudTileReader.cs"));

            int update = source.IndexOf("public void Update(", StringComparison.Ordinal);
            check("CloudTileReader.Update was found", update >= 0, "");
            if (update < 0) return;

            string body = source.Substring(update,
                source.IndexOf("\n        }", update, StringComparison.Ordinal) - update);

            int corner = body.IndexOf("UpdateOrigin()", StringComparison.Ordinal);
            int gate = body.IndexOf("if (!readTiles) return;", StringComparison.Ordinal);

            check("the cloud window corner updates every frame", corner >= 0,
                "UpdateOrigin() is not called");

            check("the cloud corner updates BEFORE the read throttle",
                corner >= 0 && gate >= 0 && corner < gate,
                "a throttled corner makes the whole cloud field step with the player");

            // The reflective read is the part worth throttling, and it must
            // actually be behind the gate.
            int read = body.IndexOf("_tilesField.GetValue", StringComparison.Ordinal);
            check("the reflective tile read stays behind the throttle",
                read < 0 || (gate >= 0 && gate < read),
                "256 boxing reflective reads per frame is the cost this throttle exists to avoid");
        }
    }
}
