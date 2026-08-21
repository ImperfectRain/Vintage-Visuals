using System;
using System.Linq;
using VintageVisuals.Common;
using VintageVisuals.Common.Scene;
using VintageVisuals.ColorGrade;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// Drives the intent, budget and arbitration layers through the world states
    /// they exist for.
    ///
    /// All three are ordinary arithmetic that decides what the whole screen
    /// looks like, so all three get driven here rather than only in a running
    /// client - the same rule the grade stack and the wetness model follow.
    ///
    /// The load-bearing check is the budget one. It exists because of a real
    /// defect: in heavy rain, colour grading, the overcast term and rain fog
    /// were each removing light and colour from the same afternoon, each tuned
    /// alone, each reasonable alone, with nothing between them.
    /// </summary>
    public static class SceneIntentChecks
    {
        public static void Run(Action<string, bool, string> check)
        {
            Action<string, bool> ok = (name, condition) => check(name, condition, "");

            // --- intent ---------------------------------------------------
            SceneIntent clear = SceneIntentBuilder.Build(EnvironmentState.Clear);

            ok("a clear temperate noon asks for almost nothing",
                clear[IntentChannel.Restraint] < 0.05f && clear[IntentChannel.Readability] < 0.05f &&
                clear[IntentChannel.Wetness] < 0.05f && clear[IntentChannel.Gloom] < 0.05f);

            SceneIntent storm = SceneIntentBuilder.Build(With(rain: 1f, cloudCover: 1f));
            ok("a storm asks for wetness and atmosphere",
                storm[IntentChannel.Wetness] > 0.5f && storm[IntentChannel.Atmosphere] > 0.2f);

            SceneIntent cave = SceneIntentBuilder.Build(With(skyExposure: 0f, depth: 1f, dayLight: 0f));
            ok("a cave at night asks for restraint", cave[IntentChannel.Restraint] > 0.5f);
            ok("a cave at night asks for readability", cave[IntentChannel.Readability] > 0.5f);
            ok("a cave is enclosed and firelit",
                cave[IntentChannel.Enclosure] > 0.7f && cave[IntentChannel.ArtificialLight] > 0.5f);

            SceneIntent desert = SceneIntentBuilder.Build(With(temperature: 36f, humidity: 0.05f));
            ok("a hot open desert asks for heat", desert[IntentChannel.Heat] > 0.3f);
            ok("a cellar in that desert does not",
                SceneIntentBuilder.Build(With(temperature: 36f, skyExposure: 0f))[IntentChannel.Heat] < 0.05f);

            SceneIntent tundra = SceneIntentBuilder.Build(With(temperature: -20f));
            ok("freezing air asks for cold", tundra[IntentChannel.Cold] > 0.3f);

            // Every channel is 0..1 whatever it is fed, including nonsense.
            SceneIntent broken = SceneIntentBuilder.Build(new EnvironmentState(
                float.NaN, float.NaN, float.NaN, new Vintagestory.API.MathTools.Vec2f(),
                float.NaN, float.NaN, float.NaN, float.NaN, float.NaN,
                float.NaN, float.NaN, float.NaN, float.NaN, float.NaN,
                new Vintagestory.API.MathTools.Vec3f(), float.NaN));

            ok("a NaN world produces finite intent",
                Enum.GetValues(typeof(IntentChannel)).Cast<IntentChannel>()
                    .All(c => !float.IsNaN(broken[c]) && broken[c] >= 0f && broken[c] <= 1f));

            // --- contributions ---------------------------------------------
            ok("every contribution names its source and reason",
                cave.Contributions.Count > 0 &&
                cave.Contributions.All(c => !string.IsNullOrEmpty(c.Source) && !string.IsNullOrEmpty(c.Reason)));

            var capped = new SceneIntent();
            capped.Add("test", IntentChannel.Wetness, 5f, "far past the cap");
            ok("an oversized contribution is capped and says so",
                capped.Contributions[0].Capped &&
                Math.Abs(capped.Contributions[0].Amount - SceneIntent.ContributionCap) < 1e-4f);

            var quiet = new SceneIntent();
            quiet.Add("test", IntentChannel.Wetness, 0f, "nothing");
            ok("a contribution of nothing leaves no trace", quiet.Contributions.Count == 0);

            // --- budget -----------------------------------------------------
            var budget = new VisualBudget(0f, 0f);

            float owner = budget.Request("colorgrade", VisualRole.Saturation, 0.3f);
            ok("the role's owner gets what it asks for", Math.Abs(owner - 0.3f) < 1e-4f);

            float secondary = budget.Request("weather", VisualRole.Saturation, 0.3f);
            ok("a secondary claim is dampened, not refused",
                secondary > 0.01f && secondary < 0.3f);

            var greedy = new VisualBudget(0f, 0f);
            for (int i = 0; i < 20; i++) greedy.Request("colorgrade", VisualRole.Saturation, 1f);
            ok("no amount of claiming exceeds the allowance",
                greedy.Remaining(VisualRole.Saturation) <= 1e-4f);

            ok("a trimmed claim is recorded as trimmed",
                greedy.Claims.Any(c => c.Trimmed));

            var restrained = new VisualBudget(1f, 1f);
            ok("full restraint and readability shrink the light allowance hard",
                restrained.Remaining(VisualRole.SceneLight) <
                new VisualBudget(0f, 0f).Remaining(VisualRole.SceneLight) * 0.4f);

            ok("restraint never takes an allowance to nothing",
                restrained.Remaining(VisualRole.SceneLight) > 0f);

            // --- arbitration: the defect this whole layer exists for ---------
            EnvironmentState rainyAfternoon = With(rain: 1f, cloudCover: 1f);
            SceneIntent rainyIntent = SceneIntentBuilder.Build(rainyAfternoon);

            SceneGrants grants = SceneArbiter.Arbitrate(
                rainyIntent, rainyAfternoon, new SceneDemand(1f, 1f, 1f), out VisualBudget rainyBudget);

            ok("in a storm, at least one light claim is trimmed",
                grants.CloudShadow < 0.999f || grants.Overcast < 0.999f);

            ok("the overcast term and the cloud shadows do not both get full strength",
                grants.CloudShadow + grants.Overcast < 2f);

            ok("arbitration records who took what",
                rainyBudget.Claims.Count >= 6 && rainyBudget.Describe().Contains("colorgrade"));

            // A subsystem that wants nothing must not be handed a zero
            // multiplier, or switching the weather off would scale everything
            // else by an arbitration that never ran.
            SceneGrants idle = SceneArbiter.Arbitrate(
                SceneIntentBuilder.Build(EnvironmentState.Clear), EnvironmentState.Clear,
                new SceneDemand(0f, 0f, 0f), out _);

            ok("wanting nothing grants everything",
                idle.RainFog == 1f && idle.CloudShadow == 1f && idle.Overcast == 1f);

            // --- proximity: readability's context-aware half -----------------
            //
            // The point is narrow and worth pinning: something nearby may raise
            // the floor under what obscures, and may do nothing else. It must
            // not brighten the world, tint it, or otherwise announce itself.
            SceneIntent alone = SceneIntentBuilder.Build(With(rain: 1f));
            SceneIntent crowded = SceneIntentBuilder.Build(With(rain: 1f, proximity: 1f));

            ok("company raises readability",
                crowded[IntentChannel.Readability] > alone[IntentChannel.Readability] + 0.05f);

            ok("company raises restraint",
                crowded[IntentChannel.Restraint] > alone[IntentChannel.Restraint] + 0.05f);

            ok("company changes nothing else about the scene",
                crowded[IntentChannel.Wetness] == alone[IntentChannel.Wetness] &&
                crowded[IntentChannel.Gloom] == alone[IntentChannel.Gloom] &&
                crowded[IntentChannel.Night] == alone[IntentChannel.Night]);

            // And it has to actually reach the thing that obscures.
            EnvironmentState storming = With(rain: 1f, cloudCover: 1f);
            EnvironmentState storming2 = With(rain: 1f, cloudCover: 1f, proximity: 1f);

            SceneGrants quiet2 = SceneArbiter.Arbitrate(
                SceneIntentBuilder.Build(storming), storming, new SceneDemand(1f, 1f, 1f), out _);
            SceneGrants threatened = SceneArbiter.Arbitrate(
                SceneIntentBuilder.Build(storming2), storming2, new SceneDemand(1f, 1f, 1f), out _);

            ok("a creature nearby costs the storm some of its fog",
                threatened.RainFog < quiet2.RainFog - 0.01f);

            // --- style ------------------------------------------------------
            ok("an unknown style name falls back to None",
                StyleOffsets.Parse("nonsense") == StyleKind.None &&
                StyleOffsets.Parse(null) == StyleKind.None);

            ok("style names round-trip case-insensitively",
                StyleOffsets.Parse("muted") == StyleKind.Muted);

            ok("None changes nothing",
                StyleOffsets.For(StyleKind.None).Exposure == 1f &&
                StyleOffsets.For(StyleKind.None).Saturation == 0f);

            ok("Muted removes colour and Vivid adds it",
                StyleOffsets.For(StyleKind.Muted).Saturation < 0f &&
                StyleOffsets.For(StyleKind.Vivid).Saturation > 0f);

            ok("Cold is cooler than Warm",
                StyleOffsets.For(StyleKind.Cold).Temperature <
                StyleOffsets.For(StyleKind.Warm).Temperature);

            var style = new AdaptiveGradeConfig { Enabled = false, Style = "Muted", StyleStrength = 1f };
            GradeSample styled = GradeStack.Evaluate(GradeSample.Neutral, EnvironmentState.Clear, style);
            ok("a style still applies when the adaptive stack is off",
                styled.Saturation < GradeSample.Neutral.Saturation);

            var noStyle = new AdaptiveGradeConfig { Enabled = false, Style = "None" };
            ok("no style and no adaptive stack changes nothing at all",
                GradeStack.Evaluate(GradeSample.Neutral, EnvironmentState.Clear, noStyle)
                    .DistanceTo(GradeSample.Neutral) == 0f);
        }

        private static EnvironmentState With(float dayLight = 1f, float rain = 0f, float cloudCover = 0f,
                                             float skyExposure = 1f, float depth = 0f,
                                             float temperature = EnvironmentState.TemperateCelsius,
                                             float humidity = 0.5f, float proximity = 0f)
        {
            return new EnvironmentState(
                dayLight: dayLight, moonLight: 0f, cloudCover: cloudCover,
                windDirection: new Vintagestory.API.MathTools.Vec2f(1f, 0f), windSpeed: 0f,
                precipitation: rain, rain: rain, snow: 0f, wetness: rain,
                temperature: temperature, humidity: humidity,
                skyExposure: skyExposure, depth: depth, underwater: 0f,
                cameraPosition: new Vintagestory.API.MathTools.Vec3f(), proximity: proximity);
        }
    }
}
