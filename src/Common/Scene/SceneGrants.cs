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

        /// <summary>
        /// Ground haze, as a fraction of the configured strength.
        ///
        /// A SECOND claim on the same role as rain fog, and deliberately so.
        /// Both put air between the camera and the world, and the budget
        /// exists precisely so that a rainy morning in a humid valley does not
        /// get charged for the air twice by two subsystems that were each
        /// reasonable alone.
        /// </summary>
        public readonly float HeightHaze;

        /// <summary>Cloud shadow depth, as a fraction of the configured strength.</summary>
        public readonly float CloudShadow;

        /// <summary>The overcast term's dimming of the direct lobe.</summary>
        public readonly float Overcast;

        public SceneGrants(float gradeSaturation, float gradeContrast, float gradeLight,
                           float rainFog, float cloudShadow, float overcast,
                           float heightHaze = 1f)
        {
            HeightHaze = heightHaze;
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
            get { return new SceneGrants(1f, 1f, 1f, 1f, 1f, 1f, 1f); }
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
        public readonly float HeightHaze;

        public SceneDemand(float rainFog, float cloudShadow, float overcast, float heightHaze = 0f)
        {
            RainFog = rainFog;
            CloudShadow = cloudShadow;
            Overcast = overcast;
            HeightHaze = heightHaze;
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

            // Weather owns haze. Rain that is FALLING, not rain that fell -
            // the air clears within seconds of a shower stopping even though
            // the ground stays wet for a minute.
            float wantFog = Clamp01(demand.RainFog) * world.Rain;
            float gotFog = budget.Request("weather", VisualRole.Haze, wantFog);

            // Ground haze is a secondary claim on the same role, so it is
            // dampened and it takes what rain fog left. That ordering is the
            // right way round: on the morning both want, the rain is the thing
            // the player can see a reason for.
            float wantHaze = Clamp01(demand.HeightHaze) * HazePressure(world);
            float gotHaze = budget.Request("atmosphere", VisualRole.Haze, wantHaze);

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
                Fraction(gotOvercast, wantOvercast),
                Fraction(gotHaze, wantHaze));
        }

        /// <summary>
        /// How much the world itself wants ground haze, before the player's
        /// slider is applied at all.
        ///
        /// Haze lies on the ground when the air is humid, still and cool, and
        /// it burns off as the day warms and the wind gets up. Every term here
        /// is a fact the game supplies:
        ///
        ///  - HUMIDITY is worldgen rainfall, so it asks what KIND of place this
        ///    is. A rainforest hazes and a desert does not, and neither changes
        ///    because it happened to rain yesterday.
        ///  - WETNESS carries the recent weather, so a valley hazes after rain.
        ///  - WIND disperses it, which is the term that stops the haze being a
        ///    constant the player learns to ignore.
        ///  - DAYLIGHT burns it off, so it is a morning and evening thing.
        ///  - SKY EXPOSURE keeps it out of caves, where "ground level" means
        ///    nothing and vanilla's own fog is already doing the work.
        ///
        /// This is the least authoritative thing in this file and it is worth
        /// being plain about that: no part of Vintage Story models ground haze,
        /// so unlike the cloud shadows - which read the game's own cloud tiles -
        /// there is nothing here to be faithful TO. It is a plausible shape, not
        /// a measurement, and it sits at the bottom of the information ladder in
        /// docs/VISUAL-LANGUAGE.md by necessity rather than by choice.
        /// </summary>
        public static float HazePressure(EnvironmentState world)
        {
            float damp = Clamp01(world.Humidity * 0.6f + world.Wetness * 0.4f);
            float still = 1f - Clamp01(world.WindSpeed);
            float cool = 1f - Clamp01(world.DayLight);
            float outside = Clamp01(world.SkyExposure);

            return Clamp01(damp * still * cool * outside);
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
