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
        }

        /// <summary>
        /// The cloud window's corner must update on every Update, not only on
        /// the frames that re-read the tiles.
        /// </summary>
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
