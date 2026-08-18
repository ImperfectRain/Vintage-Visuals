using System;
using VintageVisuals.ColorGrade;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// Exercises the eye-adaptation model.
    ///
    /// It is deliberately free of game types precisely so it can be checked
    /// here: the behaviour that matters (asymmetric speed, frame-rate
    /// independence, settling exactly on target) is the kind that looks fine in
    /// a five-second glance in game and is wrong over a minute.
    /// </summary>
    public static class AdaptiveExposureChecks
    {
        public static void Run(Action<string, bool, string> check)
        {
            // Most checks here are self-describing and need no detail string.
            Action<string, bool> ok = (name, condition) => check(name, condition, "");

            const float dark = 1.6f;
            const float bright = 1.0f;

            // --- target curve ---
            ok("pitch dark targets DarkGain",
                Math.Abs(AdaptiveExposure.TargetFor(0f, dark, bright) - dark) < 1e-6f);
            ok("full light targets BrightGain",
                Math.Abs(AdaptiveExposure.TargetFor(1f, dark, bright) - bright) < 1e-6f);
            ok("half light sits between",
                Math.Abs(AdaptiveExposure.TargetFor(0.5f, dark, bright) - 1.3f) < 1e-6f);
            ok("out-of-range light is clamped, not extrapolated",
                Math.Abs(AdaptiveExposure.TargetFor(5f, dark, bright) - bright) < 1e-6f &&
                Math.Abs(AdaptiveExposure.TargetFor(-5f, dark, bright) - dark) < 1e-6f);

            ok("light level normalises 0..1",
                AdaptiveExposure.NormaliseLightLevel(0) == 0f &&
                Math.Abs(AdaptiveExposure.NormaliseLightLevel(AdaptiveExposure.MaxGameLightLevel) - 1f) < 1e-6f);
            ok("over-bright light level clamps to 1",
                Math.Abs(AdaptiveExposure.NormaliseLightLevel(999) - 1f) < 1e-6f);

            // --- easing ---
            var eye = new AdaptiveExposure();
            ok("starts neutral", Math.Abs(eye.Current - 1f) < 1e-6f);

            eye.Step(1.6f, 0.1f, 4f, 1f);
            check("one tick moves toward target but does not arrive",
                eye.Current > 1f && eye.Current < 1.6f, eye.Current.ToString("R"));

            // Brightening (going dark) must be slower than darkening.
            var toDark = new AdaptiveExposure();
            var toLight = new AdaptiveExposure();
            toDark.Step(1.6f, 1f, 4f, 1f);
            for (int i = 0; i < 10; i++) toLight.Step(1.6f, 0.1f, 4f, 1f);
            check("brighten and darken use different time constants",
                Math.Abs(toDark.Current - toLight.Current) < 1e-3f,
                "one 1s step=" + toDark.Current.ToString("R") + " ten 0.1s steps=" + toLight.Current.ToString("R"));

            var fast = new AdaptiveExposure();
            var slow = new AdaptiveExposure();
            fast.Step(1.6f, 1f, 1f, 1f);
            slow.Step(1.6f, 1f, 8f, 1f);
            check("a longer brighten constant adapts more slowly", fast.Current > slow.Current,
                "tau=1 -> " + fast.Current.ToString("R") + ", tau=8 -> " + slow.Current.ToString("R"));

            // Frame-rate independence: the same elapsed time must land in the
            // same place regardless of how it was subdivided.
            var coarse = new AdaptiveExposure();
            var fine = new AdaptiveExposure();
            for (int i = 0; i < 20; i++) coarse.Step(1.6f, 0.1f, 4f, 1f);
            for (int i = 0; i < 200; i++) fine.Step(1.6f, 0.01f, 4f, 1f);
            check("frame-rate independent over 2s", Math.Abs(coarse.Current - fine.Current) < 2e-3f,
                "10Hz=" + coarse.Current.ToString("R") + " 100Hz=" + fine.Current.ToString("R"));

            // --- convergence and stability ---
            var settle = new AdaptiveExposure();
            for (int i = 0; i < 2000; i++) settle.Step(1.6f, 0.1f, 4f, 1f);
            check("settles exactly on target so uploads can stop",
                settle.Current == 1.6f, settle.Current.ToString("R"));

            var back = new AdaptiveExposure();
            for (int i = 0; i < 2000; i++) back.Step(1.6f, 0.1f, 4f, 1f);
            for (int i = 0; i < 2000; i++) back.Step(1.0f, 0.1f, 4f, 1f);
            check("returns exactly to neutral", back.Current == 1.0f, back.Current.ToString("R"));

            // --- degenerate inputs must not corrupt state ---
            var guarded = new AdaptiveExposure();
            guarded.Step(1.6f, 0f, 4f, 1f);
            ok("zero delta is ignored", Math.Abs(guarded.Current - 1f) < 1e-6f);
            guarded.Step(1.6f, -5f, 4f, 1f);
            ok("negative delta is ignored", Math.Abs(guarded.Current - 1f) < 1e-6f);
            guarded.Step(float.NaN, 0.1f, 4f, 1f);
            ok("NaN target is ignored", Math.Abs(guarded.Current - 1f) < 1e-6f);
            guarded.Step(1.6f, float.NaN, 4f, 1f);
            ok("NaN delta is ignored", Math.Abs(guarded.Current - 1f) < 1e-6f);

            var snap = new AdaptiveExposure();
            snap.Step(1.6f, 0.1f, 0f, 0f);
            check("zero time constant snaps rather than dividing by zero",
                Math.Abs(snap.Current - 1.6f) < 1e-6f, snap.Current.ToString("R"));

            var sane = new AdaptiveExposure();
            for (int i = 0; i < 500; i++) sane.Step(i % 2 == 0 ? 1.6f : 1.0f, 0.1f, 4f, 1f);
            check("never goes NaN or out of gain range under thrashing",
                !float.IsNaN(sane.Current) && sane.Current >= 0.9f && sane.Current <= 1.7f,
                sane.Current.ToString("R"));
        }
    }
}
