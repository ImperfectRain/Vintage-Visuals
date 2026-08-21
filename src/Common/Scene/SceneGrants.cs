namespace VintageVisuals.Common.Scene
{
    /// <summary>
    /// What each subsystem was allowed to take this tick, as a fraction of what
    /// it asked for.
    ///
    /// Computed in one place on purpose. The obvious alternative - every
    /// subsystem calling <see cref="VisualBudget.Request"/> from its own
    /// renderer - looks tidier and does not work: the subsystems tick at
    /// different rates and different stages, so a budget rebuilt once would be
    /// exhausted by whoever ran first and the rest would collapse to nothing for
    /// most frames. Arbitration has to happen at one cadence, and this is it.
    ///
    /// Every field is a MULTIPLIER on what the subsystem already wanted, so 1
    /// means "take what you asked for" and every consumer's zero still means
    /// vanilla.
    /// </summary>
    public readonly struct SceneGrants
    {
        /// <summary>Colour the grade may remove, as a fraction of what it wanted.</summary>
        public readonly float GradeSaturation;

        /// <summary>Contrast the grade may remove.</summary>
        public readonly float GradeContrast;

        /// <summary>Light the grade may remove.</summary>
        public readonly float GradeLight;

        /// <summary>Rain fog, as a fraction of the configured strength.</summary>
        public readonly float RainFog;

        /// <summary>Cloud shadow depth, as a fraction of the configured strength.</summary>
        public readonly float CloudShadow;

        /// <summary>The overcast term's dimming of the direct lobe.</summary>
        public readonly float Overcast;

        public SceneGrants(float gradeSaturation, float gradeContrast, float gradeLight,
                           float rainFog, float cloudShadow, float overcast)
        {
            GradeSaturation = gradeSaturation;
            GradeContrast = gradeContrast;
            GradeLight = gradeLight;
            RainFog = rainFog;
            CloudShadow = cloudShadow;
            Overcast = overcast;
        }

        /// <summary>Everything at full strength: what a consumer sees before arbitration runs.</summary>
        public static SceneGrants Full
        {
            get { return new SceneGrants(1f, 1f, 1f, 1f, 1f, 1f); }
        }
    }

    /// <summary>
    /// What each subsystem WANTS, pushed in from config before arbitration.
    ///
    /// Config-scaled values, so they belong here rather than in
    /// <see cref="EnvironmentState"/> - a strength slider states what the player
    /// wants, not what the world is doing.
    /// </summary>
    public readonly struct SceneDemand
    {
        public readonly float RainFog;
        public readonly float CloudShadow;
        public readonly float Overcast;

        public SceneDemand(float rainFog, float cloudShadow, float overcast)
        {
            RainFog = rainFog;
            CloudShadow = cloudShadow;
            Overcast = overcast;
        }
    }

    /// <summary>Runs the arbitration. Pure, so the whole table can be driven in a test.</summary>
    public static class SceneArbiter
    {
        public static SceneGrants Arbitrate(SceneIntent intent, EnvironmentState world, SceneDemand demand,
                                            out VisualBudget budget)
        {
            budget = new VisualBudget(intent[IntentChannel.Restraint], intent[IntentChannel.Readability]);

            // Order matters, and it is by ownership rather than by importance.
            // Whoever owns a role claims it first and gets what it asks for;
            // the secondaries then share what is left, dampened. Running a
            // secondary first would let it take the owner's allowance at half
            // weight, which is the worst of both.

            // Colour grading owns saturation and contrast. What it wants to
            // remove is estimated from the pressures rather than from the grade
            // itself, because the grade is computed later in the frame and this
            // has to be settled before anyone reads it.
            float gloom = intent[IntentChannel.Gloom];
            float wet = intent[IntentChannel.Wetness];
            float night = intent[IntentChannel.Night];

            float wantSaturation = wet * 0.28f + gloom * 0.12f + night * 0.45f;
            float wantContrast = wet * 0.14f + gloom * 0.10f + night * 0.10f;
            float wantLight = wet * 0.07f + gloom * 0.03f;

            float gotSaturation = budget.Request("colorgrade", VisualRole.Saturation, wantSaturation);
            float gotContrast = budget.Request("colorgrade", VisualRole.Contrast, wantContrast);
            float gotLight = budget.Request("colorgrade", VisualRole.SceneLight, wantLight);

            // Weather owns haze.
            float wantFog = Clamp01(demand.RainFog) * world.Rain;
            float gotFog = budget.Request("weather", VisualRole.Haze, wantFog);

            // Cloud shadow and the overcast term are both secondary claims on
            // scene light, and both are dampened. That is the right answer:
            // neither of them is what "the light level" primarily means, and
            // between them they were removing light twice on every overcast day.
            float wantShadow = Clamp01(demand.CloudShadow) * world.CloudCover;
            float gotShadow = budget.Request("weather", VisualRole.SceneLight, wantShadow);

            float wantOvercast = Clamp01(demand.Overcast) * world.CloudCover;
            float gotOvercast = budget.Request("pseudopbr", VisualRole.SceneLight, wantOvercast);

            return new SceneGrants(
                Fraction(gotSaturation, wantSaturation),
                Fraction(gotContrast, wantContrast),
                Fraction(gotLight, wantLight),
                Fraction(gotFog, wantFog),
                Fraction(gotShadow, wantShadow),
                Fraction(gotOvercast, wantOvercast));
        }

        /// <summary>
        /// Granted over wanted, and 1 when nothing was wanted.
        ///
        /// The zero case matters: a subsystem that asked for nothing must not be
        /// handed a multiplier of zero, or switching the weather off would leave
        /// every other effect scaled by an arbitration that never happened.
        /// </summary>
        private static float Fraction(float granted, float wanted)
        {
            return wanted <= 1e-4f ? 1f : Clamp01(granted / wanted);
        }

        private static float Clamp01(float v)
        {
            if (float.IsNaN(v)) return 0f;
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }
    }
}
