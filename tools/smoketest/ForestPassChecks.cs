using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VintageVisuals.Atmosphere;
using VintageVisuals.ColorGrade;
using VintageVisuals.Common;
using VintageVisuals.Common.Scene;
using VintageVisuals.PseudoPBR;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// v14 forest pass: gusts, plant-base shade, backlit rim light, damp and
    /// moss, forest haze, ground mist, drips/pollen/fireflies and the seasonal
    /// light grade.
    ///
    /// Each shading term is checked as a COMPOSITION from the shipped GLSL
    /// (GlslEval), never retyped, and each has a mutation in
    /// tools/mutate/mutations.tsv proving the check fails when it breaks.
    /// The C# halves are driven through their real pure functions.
    /// </summary>
    public static class ForestPassChecks
    {
        public static void Run(string repo, Action<string, bool, string> check)
        {
            string glsl = File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/shadersnippets/pbrterrainmaterial.glsl"));
            string atmos = File.ReadAllText(Path.Combine(repo, "assets/vintagevisuals/shadersnippets/atmosphere.glsl"));

            Gusts(glsl, check);
            PlantFoot(glsl, check);
            Rim(glsl, check);
            Damp(glsl, check);
            ForestHaze(atmos, check);
            GroundMist(check);
            Particles(check);
            Season(check);
            FallingLeaves(glsl, check);
            Fireflies(check);
            TexturedLeaves(check);
        }

        private static string Body(string src, string sig)
            => GlslEval.StripComments(GlslEval.FunctionBody(src, sig) ?? "");

        // --- Gusts -----------------------------------------------------------

        private static void Gusts(string glsl, Action<string, bool, string> check)
        {
            try
            {
                string gust = Body(glsl, "float vvTerrainGust(");
                string waExpr = GlslEval.Rhs(GlslEval.Statement(gust, "float wa"));
                string windExpr = GlslEval.Rhs(GlslEval.Statement(gust, "float wind"));
                double Wa(double a) => GlslEval.Scalar(waExpr, GlslEval.Syms("a", a));
                double Wind(double w) => GlslEval.Scalar(windExpr, GlslEval.Syms("vv_sceneWind", w));

                // The breeze clock wraps from 1 to 0: the copy whose phase wraps
                // must have weight 0 on both sides of the wrap, and the two
                // weights must sum to 1 everywhere, or the wrap shows as a jump.
                bool wrapHidden = Math.Abs(Wa(0.0)) < 1e-9 && Math.Abs(Wa(1.0)) < 1e-9 && Math.Abs(Wa(0.5) - 1.0) < 1e-9;
                bool partition = gust.Contains("float wb = 1.0 - wa;") &&
                                 gust.Contains("float b = fract(vv_sceneBreeze + 0.5);") &&
                                 gust.Contains("vvTerrainGustNoise(worldXZ, a, vec2(71.0, 23.0)) * wa") &&
                                 gust.Contains("vvTerrainGustNoise(worldXZ, b, vec2(79.0, 47.0)) * wb");
                double cell = GlslEval.Const(glsl, "VV_TERRAIN_GUST_CELL");
                double period = GlslEval.Const(glsl, "VV_TERRAIN_GUST_PERIOD");
                bool tiles = Math.Abs(cell * period - 4096.0) < 1e-6;
                bool gated = gust.Contains("if (vv_pbrGusts < 0.001) return 0.0;") &&
                             gust.Contains("if (wind <= 0.0) return 0.0;") &&
                             Wind(0.0) == 0.0 && Wind(0.05) == 0.0 && Wind(1.0) > 0.999 &&
                             Regex.IsMatch(gust, @"return smoothstep\([^;]*\) \* wind \* clamp\(vv_pbrGusts, 0\.0, 1\.0\);");
                bool expanded = gust.Contains("inversesqrt(max(wa * wa + wb * wb");
                string noise = Body(glsl, "float vvTerrainGustNoise(");
                bool downwind = noise.Contains("worldXZ - vvTerrainWindDir() * (VV_TERRAIN_GUST_TRAVEL * (phase - 0.5))");
                check("I-gusts travel downwind, the breeze clock's wrap never shows, and still air or a zero slider is calm",
                      wrapHidden && partition && tiles && gated && expanded && downwind,
                      $"weight at wrap {Wa(0.0):0.###}/{Wa(1.0):0.###}, grid {cell}x{period}, wind 0 -> {Wind(0.0):0.###}");

                // Flutter: the flecks read the gust at their own canopy-plane
                // point, and the sheen is exactly 1 in calm air.
                string sheen = Body(glsl, "vec3 vvTerrainGustSheen(");
                string ret = GlslEval.Rhs(GlslEval.Statement(sheen, "return mix(color"));
                double sheenK = GlslEval.Const(glsl, "VV_TERRAIN_GUST_SHEEN");
                GlslEval.Val Sheen(double k) => GlslEval.Eval(ret, GlslEval.Syms(
                    "color", GlslEval.Vec(0.2, 0.5, 0.1), "y", 0.2 * 0.299 + 0.5 * 0.587 + 0.1 * 0.114,
                    "k", k, "VV_TERRAIN_GUST_SHEEN", sheenK));
                var calm = Sheen(0.0);
                var full = Sheen(1.0);
                bool sheenOk = Math.Abs(calm.X - 0.2) < 1e-9 && Math.Abs(calm.Y - 0.5) < 1e-9 && Math.Abs(calm.Z - 0.1) < 1e-9 &&
                               full.Y > 0.5 && full.Y <= 0.5 * (1.0 + sheenK) + 1e-9 && sheenK <= 0.2 &&
                               sheen.Contains("if (gust <= 0.0) return color;");
                check("I-gust sheen is exactly vanilla in calm air and a bounded lift at a full gust",
                      sheenOk, $"calm {calm}, full gust {full}");
            }
            catch (Exception ex) { check("I-gusts are readable", false, ex.Message); }
        }

        // --- Plant foot ------------------------------------------------------

        private static void PlantFoot(string glsl, Action<string, bool, string> check)
        {
            try
            {
                string contact = Body(glsl, "float vvTerrainContactShade(");
                string footExpr = GlslEval.Rhs(GlslEval.Statement(contact, "float foot"));
                string retExpr = GlslEval.Rhs(GlslEval.Statement(contact, "return", "VV_TERRAIN_CONTACT_DEPTH"));
                double reach = GlslEval.Const(glsl, "VV_TERRAIN_CONTACT_REACH");
                double depth = GlslEval.Const(glsl, "VV_TERRAIN_CONTACT_DEPTH");
                double Shade(double height, double slider)
                {
                    string f = GlslEval.StubCall(footExpr, "vvTerrainPlantHeight", "h");
                    double foot = GlslEval.Scalar(f, GlslEval.Syms("h", height, "VV_TERRAIN_CONTACT_REACH", reach));
                    return GlslEval.Scalar(retExpr, GlslEval.Syms("foot", foot, "vv_pbrContactShade", slider,
                                                                  "VV_TERRAIN_CONTACT_DEPTH", depth));
                }
                bool ok = Math.Abs(Shade(0.0, 1.0) - (1.0 - depth)) < 1e-9 &&
                          Math.Abs(Shade(reach, 1.0) - 1.0) < 1e-9 && Math.Abs(Shade(0.9, 1.0) - 1.0) < 1e-9 &&
                          Shade(0.1, 1.0) < Shade(0.3, 1.0) && Shade(0.0, 0.5) > Shade(0.0, 1.0) &&
                          depth <= 0.6 && reach <= 0.6 &&
                          contact.Contains("if (vv_pbrContactShade < 0.001) return 1.0;") &&
                          contact.Contains("if (!vvTerrainIsRootedFlora(vvTerrainFloraClass())) return 1.0;");
                check("I-plant base shade darkens only the foot of a rooted plant, never brightens, and 0 is vanilla", ok,
                      $"root {Shade(0.0, 1.0):0.00}, {reach} blocks up {Shade(reach, 1.0):0.00}");

                // The tissue gradient used windData / 7 alone, 0 for every
                // single-block plant: grass transmitted at its root value from
                // root to tip. The tip of a one-block plant must now be the tip.
                string heightBody = Body(glsl, "float vvTerrainPlantHeight(");
                string tipBody = Body(glsl, "float vvTerrainPlantTip(");
                string tissue = Body(glsl, "float vvTerrainFloraTissue(");
                string tipExpr = GlslEval.StubCall(GlslEval.Rhs(GlslEval.Statement(tipBody, "return")), "vvTerrainPlantHeight", "h");
                double Tip(double h) => GlslEval.Scalar(tipExpr, GlslEval.Syms("h", h));
                bool tipOk = heightBody.Contains("fract(cameraRelativePos.y + playerpos.y)") &&
                             heightBody.Contains("float((renderFlags >> WindDataPosition) & 0x7) + within") &&
                             tissue.Contains("base *= mix(0.55, 1.0, vvTerrainPlantTip(worldPos.xyz));") &&
                             Tip(0.0) == 0.0 && Tip(0.95) > 0.95 && Tip(2.5) == 1.0;
                check("I-a one-block plant's tip transmits as a tip, read from vanilla's own bend height", tipOk,
                      $"tip at 0.95 blocks {Tip(0.95):0.00}");
            }
            catch (Exception ex) { check("I-plant base shade is readable", false, ex.Message); }
        }

        // --- Backlit rim -----------------------------------------------------

        private static void Rim(string glsl, Action<string, bool, string> check)
        {
            try
            {
                string rim = Body(glsl, "vec3 vvTerrainBacklitRim(");
                string backExpr = GlslEval.Rhs(GlslEval.Statement(rim, "float back"));
                double Back(double look) => GlslEval.Scalar(
                    GlslEval.StubCall(backExpr, "dot", "look"), GlslEval.Syms("look", look));
                double gain = GlslEval.Const(glsl, "VV_TERRAIN_RIM_GAIN");
                double far = GlslEval.Const(glsl, "VV_TERRAIN_RIM_FAR");
                string shade = Body(glsl, "vec4 vvTerrainFloraShade(");
                bool ok = rim.Contains("if (!(vv_pbrFoliage >= 0.001)) return vec3(0.0);") &&
                          rim.Contains("float rim = vvTerrainLeafRim();") &&
                          rim.Contains("* vvTerrainClearSky()") &&
                          rim.Contains("if (sun < 0.01) return vec3(0.0);") &&
                          Back(0.0) == 0.0 && Back(0.35) == 0.0 && Back(-1.0) == 0.0 && Back(0.95) > 0.999 &&
                          gain <= 0.6 && far <= 32.0 &&
                          shade.Contains("result += vvTerrainBacklitRim(baseColor, cameraRelativePos, l, sunVisibility) * clearAir;");
                check("I-backlit rim light only looking toward the sun, in direct sun, on the hole rims, capped, and through the foliage slider", ok,
                      $"looking away {Back(-1.0):0.00}, side-on {Back(0.35):0.00}, toward {Back(0.95):0.00}, gain {gain}");
            }
            catch (Exception ex) { check("I-backlit rim light is readable", false, ex.Message); }
        }

        // --- Damp and moss ---------------------------------------------------

        private static void Damp(string glsl, Action<string, bool, string> check)
        {
            try
            {
                string apply = Body(glsl, "vec3 vvTerrainApplyDamp(");
                var st = GlslEval.Statements(apply);
                string mossInit = GlslEval.Rhs(st.Single(s => s.StartsWith("vec3 moss =")));
                string mossScale = GlslEval.Rhs(st.Single(s => s.StartsWith("moss *=")));
                string mixed = GlslEval.Rhs(st.Single(s => s.StartsWith("diffuse = mix(")));
                string ret = GlslEval.Rhs(st.Single(s => s.StartsWith("return diffuse")));
                double share = GlslEval.Const(glsl, "VV_TERRAIN_MOSS_SHARE");
                double darken = GlslEval.Const(glsl, "VV_TERRAIN_DAMP_DARKEN");
                var tint = Regex.Match(glsl, @"const vec3 VV_TERRAIN_MOSS_TINT = vec3\(([0-9.]+), ([0-9.]+), ([0-9.]+)\);");
                var tintV = GlslEval.Vec(double.Parse(tint.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                                         double.Parse(tint.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
                                         double.Parse(tint.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture));
                double Luma(GlslEval.Val v) => v.X * 0.299 + v.Y * 0.587 + v.Z * 0.114;
                GlslEval.Val Apply(GlslEval.Val diffuse, double damp)
                {
                    var s = GlslEval.Syms("diffuse", diffuse, "VV_TERRAIN_MOSS_TINT", tintV, "luma", GlslEval.Vec(0.299, 0.587, 0.114),
                                          "damp", damp, "VV_TERRAIN_MOSS_SHARE", share, "VV_TERRAIN_DAMP_DARKEN", darken);
                    var moss = GlslEval.Eval(mossInit, s);
                    s["moss"] = moss;
                    double k = GlslEval.Scalar(mossScale, s);
                    s["moss"] = GlslEval.Vec(moss.X * k, moss.Y * k, moss.Z * k);
                    s["diffuse"] = GlslEval.Eval(mixed, s);
                    return GlslEval.Eval(ret, s);
                }
                var samples = new[] { GlslEval.Vec(0.5, 0.5, 0.5), GlslEval.Vec(0.6, 0.45, 0.3), GlslEval.Vec(0.2, 0.22, 0.25) };
                bool neverBrighter = samples.All(c => Luma(Apply(c, 1.0)) <= Luma(c) + 1e-9 && Luma(Apply(c, 0.5)) <= Luma(c) + 1e-9);
                bool greener = samples.All(c => { var a = Apply(c, 1.0); return a.Y / Math.Max(1e-6, Luma(a)) > c.Y / Luma(c); });
                var grey = Apply(GlslEval.Vec(0.5, 0.5, 0.5), 1.0);

                string damp = Body(glsl, "float vvTerrainDamp(");
                bool gated = damp.Contains("if (vv_pbrDamp < 0.001 || vv_pbrHumidity <= 0.0) return 0.0;") &&
                             damp.Contains("if (m.metalness > 0.2 || m.perceptualRoughness < 0.45) return 0.0;") &&
                             damp.Contains("if (shelter <= 0.0) return 0.0;") &&
                             damp.Contains("float layer = clamp(1.0 - m.snow - m.frost, 0.0, 1.0);") &&
                             damp.Contains("(1.0 - smoothstep(20.0, 28.0, reach))") &&
                             apply.Contains("if (damp <= 0.0) return diffuse;") &&
                             Math.Abs(GlslEval.Const(glsl, "VV_TERRAIN_MOSS_CELL") * GlslEval.Const(glsl, "VV_TERRAIN_MOSS_PERIOD") - 4096.0) < 1e-6;
                check("I-damp and moss are never brighter, always a little greener, and only on sheltered rough faces in a rainy climate",
                      neverBrighter && greener && gated && darken >= 0.75,
                      $"grey at full damp {grey}, darken {darken}");
            }
            catch (Exception ex) { check("I-damp and moss are readable", false, ex.Message); }
        }

        // --- Forest haze -----------------------------------------------------

        private static AtmosphereState Air(float canopy, float sky, float humidity)
        {
            AtmosphereState c = AtmosphereState.Clear;
            return new AtmosphereState(c.FogColor, c.FogDensity, c.FogMin, c.FogBrightness,
                c.FlatFogDensity, c.FlatFogYPos, c.SunColor, c.SunDirection, c.DayLight,
                c.MoonDirection, c.MoonLight, 0f, 0f, 0f, c.Temperature, humidity,
                c.AmbientColor, c.HeightAboveSeaLevel, c.ViewDistance, sky, canopy);
        }

        private static void ForestHaze(string atmos, Action<string, bool, string> check)
        {
            var on = new AtmosphereConfig { Enabled = true, ForestHaze = 1f };
            float Haze(float canopy, float sky, float humidity, AtmosphereConfig cfg = null)
                => AtmosphereInputs.Derive(Air(canopy, sky, humidity), cfg ?? on, SceneGrants.Full).ForestHaze;
            bool ok = Haze(0f, 1f, 1f) == 0f && Haze(1f, 0f, 1f) == 0f &&
                      Haze(1f, 1f, 1f, new AtmosphereConfig { Enabled = true, ForestHaze = 0f }) == 0f &&
                      Haze(1f, 1f, 1f, new AtmosphereConfig { Enabled = false, ForestHaze = 1f }) == 0f &&
                      Haze(1f, 0.8f, 1f) > 0.9f && Haze(1f, 0.8f, 0f) < Haze(1f, 0.8f, 1f) &&
                      Haze(0.5f, 0.8f, 1f) < Haze(1f, 0.8f, 1f);
            double glslDensity = GlslEval.Const(atmos, "VV_ATMOS_FOREST_DENSITY");
            string added = GlslEval.StripComments(GlslEval.FunctionBody(atmos, "float vvAtmosAddedDensity(") ?? "");
            ok &= Math.Abs(glslDensity - AtmosphereInputs.ForestHazeDensity) < 1e-9 &&
                  AtmosphereInputs.ForestHazeDensity < AtmosphereInputs.MaxAddedDensity &&
                  added.Contains("added += VV_ATMOS_FOREST_DENSITY * vv_atmosForest;") &&
                  added.IndexOf("vv_atmosForest", StringComparison.Ordinal) < added.IndexOf("return min(added, VV_ATMOS_MAX_DENSITY);", StringComparison.Ordinal);
            string compose = GlslEval.StripComments(GlslEval.FunctionBody(atmos, "vec4 vvAtmosphere(") ?? "");
            ok &= compose.Contains("if (vv_atmosForest > 0.0)") &&
                  compose.Contains("inscatter *= mix(vec3(1.0), VV_ATMOS_FOREST_SHADE, clamp(forestDepth / allDepth, 0.0, 1.0));");
            check("I-forest haze is only under a canopy with the sky still reaching the camera, summed under the shared ceiling, and tints only its own share",
                  ok, $"closed crown {Haze(1f, 0.8f, 1f):0.00}, open {Haze(0f, 1f, 1f):0.00}, tunnel {Haze(1f, 0f, 1f):0.00}");
        }

        // --- Ground mist -----------------------------------------------------

        private static EnvironmentState World(float day, float wind, float humidity, float wetness, float sky,
                                              float temperature = 18f, float rain = 0f,
                                              float autumn = 0f, float winter = 0f, float spring = 0f, float summer = 0f,
                                              float leafFall = 0f)
        {
            return new EnvironmentState(
                dayLight: day, moonLight: 0f, cloudCover: 0f,
                windDirection: new Vec2f(1f, 0f), windSpeed: wind,
                precipitation: rain, rain: rain, snow: 0f, wetness: wetness,
                temperature: temperature, humidity: humidity,
                skyExposure: sky, depth: 0f, underwater: 0f,
                cameraPosition: new Vec3f(), proximity: 0f,
                autumn: autumn, winter: winter, spring: spring, summer: summer, leafFall: leafFall);
        }

        private static void GroundMist(Action<string, bool, string> check)
        {
            AmbientBridge.Combine(0.6f, 97f, 0f, 70f, out float hazeOnlyD, out float hazeOnlyTop);
            AmbientBridge.Combine(0.6f, 97f, 0.8f, 70f, out float bothD, out float bothTop);
            AmbientBridge.Combine(0f, 97f, 0.8f, 70f, out float mistD, out float mistTop);
            AmbientBridge.Combine(0f, 97f, 0f, 70f, out float noneD, out float _);
            bool combine = Math.Abs(hazeOnlyD - AmbientBridge.HazeDensityAtFullStrength * 0.6f) < 1e-7 &&
                           Math.Abs(hazeOnlyTop - 97f) < 1e-4 &&
                           bothD < hazeOnlyD && bothTop < 97f && bothTop > 70f &&
                           Math.Abs(mistTop - 70f) < 1e-4 && mistD < 0f && noneD == 0f &&
                           AmbientBridge.MistDepth <= 8f;

            float dawn = SceneArbiter.MistPressure(World(0.2f, 0f, 0.8f, 0.5f, 1f), 0.2f);
            float noon = SceneArbiter.MistPressure(World(1f, 0f, 0.8f, 0.5f, 1f), 0.2f);
            float windy = SceneArbiter.MistPressure(World(0.2f, 0.8f, 0.8f, 0.5f, 1f), 0.2f);
            float cave = SceneArbiter.MistPressure(World(0.2f, 0f, 0.8f, 0.5f, 0f), 0.2f);
            float dry = SceneArbiter.MistPressure(World(0.2f, 0f, 0.1f, 0f, 1f), 0f);
            float water = SceneArbiter.MistPressure(World(0.2f, 0f, 0.1f, 0f, 1f), 1f);
            bool pressure = dawn > 0.4f && noon == 0f && windy == 0f && cave == 0f && dry < dawn && water > dry;

            int[] heights = Enumerable.Repeat(70, 80).Concat(new[] { 40 }).ToArray();
            double floor = GroundMap.LowFloor(heights);
            int[] valley = Enumerable.Repeat(90, 60).Concat(Enumerable.Repeat(64, 21)).ToArray();
            bool low = floor >= 68.0 && Math.Abs(GroundMap.LowFloor(valley) - 64.0) < 1e-9 &&
                       double.IsNaN(GroundMap.LowFloor(new int[81]));

            check("I-ground mist rides the ground haze's one plane: off is the haze exactly, it settles in the low ground, and only on still, moist, cool air outdoors",
                  combine && pressure && low,
                  $"dawn {dawn:0.00}, noon {noon:0.00}, windy {windy:0.00}, dry {dry:0.00}, by water {water:0.00}; one pit floor {floor:0.0}; tops {hazeOnlyTop:0}/{bothTop:0.0}/{mistTop:0}");
        }

        // --- Particles -------------------------------------------------------

        private static void Particles(Action<string, bool, string> check)
        {
            bool drips = ForestLifeSpawner.DripRate(World(0.6f, 0f, 0.5f, 0.8f, 1f)) > 0.5f &&
                         ForestLifeSpawner.DripRate(World(0.6f, 0f, 0.5f, 0.05f, 1f)) == 0f &&
                         ForestLifeSpawner.DripRate(World(0.6f, 0f, 0.5f, 0.8f, 1f, temperature: -5f)) == 0f;
            float springDay = ForestLifeSpawner.PollenRate(World(0.9f, 0.3f, 0.5f, 0f, 1f, 20f, spring: 1f));
            bool pollen = springDay > 0.9f &&
                          ForestLifeSpawner.PollenRate(World(0.1f, 0.3f, 0.5f, 0f, 1f, 20f, spring: 1f)) == 0f &&
                          ForestLifeSpawner.PollenRate(World(0.9f, 0.3f, 0.5f, 0f, 1f, 20f, rain: 0.5f)) == 0f &&
                          ForestLifeSpawner.PollenRate(World(0.9f, 0.3f, 0.5f, 0f, 1f, 5f)) == 0f &&
                          ForestLifeSpawner.PollenRate(World(0.9f, 0.3f, 0.5f, 0f, 1f, 20f, winter: 1f)) == 0f &&
                          ForestLifeSpawner.PollenRate(World(0.9f, 0.3f, 0.5f, 0f, 0f, 20f)) == 0f;
            float summerNight = ForestLifeSpawner.FireflyRate(World(0.05f, 0.1f, 0.9f, 0f, 1f, 22f, summer: 1f));
            bool fireflies = summerNight > 0.8f &&
                             ForestLifeSpawner.FireflyRate(World(0.9f, 0.1f, 0.9f, 0f, 1f, 22f)) == 0f &&
                             ForestLifeSpawner.FireflyRate(World(0.05f, 0.1f, 0.9f, 0f, 1f, 22f, rain: 0.5f)) == 0f &&
                             ForestLifeSpawner.FireflyRate(World(0.05f, 0.1f, 0.9f, 0f, 1f, 8f)) == 0f &&
                             ForestLifeSpawner.FireflyRate(World(0.05f, 0.9f, 0.9f, 0f, 1f, 22f)) == 0f &&
                             ForestLifeSpawner.FireflyRate(World(0.05f, 0.1f, 0.9f, 0f, 1f, 22f, autumn: 1f)) == 0f &&
                             ForestLifeSpawner.FireflyRate(World(0.05f, 0.1f, 0.2f, 0f, 1f, 22f)) < summerNight;
            check("forest particles: drips from wet thawed leaves, pollen on warm dry growing-season days, fireflies on warm calm nights",
                  drips && pollen && fireflies, $"spring day pollen {springDay:0.00}, summer night fireflies {summerNight:0.00}");
        }

        // --- Falling leaves (v15) --------------------------------------------

        private static double GlslConst(string glsl, string name)
        {
            Match m = Regex.Match(glsl, @"const\s+float\s+" + name + @"\s*=\s*([0-9.]+)\s*;");
            return m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : double.NaN;
        }

        private static void FallingLeaves(string glsl, Action<string, bool, string> check)
        {
            // The rate: a trickle on a still spring day, a flurry where a gust
            // crosses an autumn wood, and each of season, wind, gust and day
            // moving it the right way on its own.
            float calmSpring = ForestLifeSpawner.LeafRate(World(0.9f, 0f, 0.5f, 0f, 1f, spring: 1f), 0f);
            float autumnGust = ForestLifeSpawner.LeafRate(World(0.9f, 0.7f, 0.5f, 0f, 1f, leafFall: 1f), 1f);
            float lateAutumn = ForestLifeSpawner.LeafRate(World(0.9f, 0.3f, 0.5f, 0f, 1f, leafFall: 1f), 0f);
            float earlyAutumn = ForestLifeSpawner.LeafRate(World(0.9f, 0.3f, 0.5f, 0f, 1f, leafFall: 0.25f), 0f);
            float springBreeze = ForestLifeSpawner.LeafRate(World(0.9f, 0.3f, 0.5f, 0f, 1f, spring: 1f), 0f);
            float lateAutumnGust = ForestLifeSpawner.LeafRate(World(0.9f, 0.3f, 0.5f, 0f, 1f, leafFall: 1f), 1f);
            float autumnNight = ForestLifeSpawner.LeafRate(World(0.0f, 0.3f, 0.5f, 0f, 1f, leafFall: 1f), 0f);
            bool rate = calmSpring > 0f && calmSpring < 0.01f &&
                        autumnGust > 0.9f &&
                        lateAutumn > springBreeze * 4f &&
                        earlyAutumn > springBreeze * 3f &&
                        lateAutumnGust > lateAutumn * 2f &&
                        autumnNight > 0f && autumnNight < lateAutumn;

            // The leaf-fall year is continuous across every handover - the
            // first cut sat at its spring trickle on 2 September because the
            // season depths are all 0 there - and rises through autumn.
            float F(EnumSeason season, float within) => EnvironmentTracker.LeafFallAt(season, within);
            bool year = Math.Abs(F(EnumSeason.Summer, 1f) - F(EnumSeason.Fall, 0f)) < 1e-6 &&
                        Math.Abs(F(EnumSeason.Fall, 1f) - F(EnumSeason.Winter, 0f)) < 1e-6 &&
                        Math.Abs(F(EnumSeason.Winter, 1f) - F(EnumSeason.Spring, 0f)) < 1e-6 &&
                        Math.Abs(F(EnumSeason.Spring, 1f) - F(EnumSeason.Summer, 0f)) < 1e-6 &&
                        F(EnumSeason.Fall, 0f) >= 0.2f && F(EnumSeason.Fall, 0.9f) > F(EnumSeason.Fall, 0.1f) &&
                        F(EnumSeason.Spring, 0.5f) == 0f;

            // Readable: the first cut drew 0.14 to 0.24, which read in game as
            // a few coloured pixels; the game's quads are not blocks.
            bool size = ForestLifeSpawner.LeafMinSize >= 0.3f && ForestLifeSpawner.NeedleMinSize >= 0.15f;
            bool evergreen = ForestLifeSpawner.IsEvergreen("seasonalNeedles") && ForestLifeSpawner.IsEvergreen(null) &&
                             !ForestLifeSpawner.IsEvergreen("seasonalMaple");
            check("falling leaves: a leaf now and then on a still spring day, a flurry in an autumn gust",
                  rate && year && size && evergreen,
                  $"still spring {calmSpring:0.0000}, autumn gust {autumnGust:0.00}, late autumn {lateAutumn:0.000} " +
                  $"(+gust {lateAutumnGust:0.000}, night {autumnNight:0.000}), early autumn {earlyAutumn:0.000}, " +
                  $"spring breeze {springBreeze:0.000}; year {year}, size {size}");

            // The CPU gust field is a port of the shader's: every constant and
            // the hash are read back out of the shipped GLSL, so a retune of
            // either side alone fails here instead of putting the flurry where
            // the grass is still.
            string hash = Body(glsl, "uint vvTerrainHashU(");
            string cell = Body(glsl, "vec4 vvTerrainCellRandom(");
            string gust = Body(glsl, "float vvTerrainGust(");
            bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;
            var mismatches = new System.Collections.Generic.List<string>();
            if (!Near(GlslConst(glsl, "VV_TERRAIN_GUST_CELL"), GustField.Cell)) mismatches.Add("cell");
            if (!Near(GlslConst(glsl, "VV_TERRAIN_GUST_PERIOD"), GustField.Period)) mismatches.Add("period");
            if (!Near(GlslConst(glsl, "VV_TERRAIN_GUST_TRAVEL"), GustField.Travel)) mismatches.Add("travel");
            if (!Near(GlslConst(glsl, "VV_TERRAIN_GUST_CONTRAST"), GustField.Contrast)) mismatches.Add("contrast");
            if (!hash.Contains("x *= 0x" + GustField.HashMulA.ToString("x8") + "u") ||
                !hash.Contains("x *= 0x" + GustField.HashMulB.ToString("x8") + "u")) mismatches.Add("hash");
            if (!cell.Contains("c.x * 0x" + GustField.CellMulX.ToString("x8") + "u") ||
                !cell.Contains("c.y + 0x" + GustField.CellAddY.ToString("x8") + "u")) mismatches.Add("cell hash");
            if (!gust.Contains("vec2(71.0, 23.0)") || !gust.Contains("vec2(79.0, 47.0)")) mismatches.Add("keys");
            if (!gust.Contains("smoothstep(0.55, 0.8, n)") || !gust.Contains("smoothstep(0.08, 0.5,")) mismatches.Add("cuts");
            check("falling leaves: the CPU gust field carries the shader's constants and hash",
                  mismatches.Count == 0, mismatches.Count == 0 ? "in step" : "differs: " + string.Join(", ", mismatches));

            // And it behaves as the shader's: nothing with gusts off or in
            // still air, patches rather than everywhere, and the same value a
            // whole world wrap away.
            int hot = 0, total = 0;
            double wrapErr = 0;
            for (int i = 0; i < 48; i++)
                for (int j = 0; j < 48; j++)
                {
                    float x = 3f + i * 5.3f, z = 7f + j * 4.7f;
                    float g = GustField.At(x, z, 1f, 0.6f, 0.37f, 0.6f, 0.8f);
                    if (g > 0.5f) hot++;
                    total++;
                    if (i < 6 && j < 6)
                        wrapErr = Math.Max(wrapErr, Math.Abs(g - GustField.At(x + 4096f, z, 1f, 0.6f, 0.37f, 0.6f, 0.8f)));
                }
            double share = hot / (double)total;
            bool off = GustField.At(40f, 40f, 0f, 1f, 0.3f, 1f, 0f) == 0f && GustField.At(40f, 40f, 1f, 0.02f, 0.3f, 1f, 0f) == 0f;
            check("falling leaves: gusts are patches, none with gusts off or in still air, and tile the 4096 wrap",
                  off && share > 0.03 && share < 0.5 && wrapErr < 2e-3,
                  $"gust share {share:0.000}, wrap error {wrapErr:0.0000}, off {off}");
        }

        // --- Fireflies (v15) -------------------------------------------------

        private static void Fireflies(Action<string, bool, string> check)
        {
            // Waves: none when the gate is shut, at least one flash when it is
            // open at all, more at a higher rate, never past the cap.
            bool waves = ForestLifeSpawner.WaveFlashes(0f, 0.5) == 0 &&
                         ForestLifeSpawner.WaveFlashes(0.05f, 0.0) == 1 &&
                         ForestLifeSpawner.WaveFlashes(1f, 1.0) == ForestLifeSpawner.WaveMaxFlashes &&
                         ForestLifeSpawner.WaveFlashes(1f, 0.5) > ForestLifeSpawner.WaveFlashes(0.3f, 0.5) &&
                         ForestLifeSpawner.FireflyHalo > ForestLifeSpawner.FireflyCoreMax * 3f;
            check("fireflies: swarms flash in waves of a few, each a core in a halo", waves,
                  $"waves {ForestLifeSpawner.WaveFlashes(0.3f, 0.5)}/{ForestLifeSpawner.WaveFlashes(1f, 0.5)}");

            // The vanilla fireflies ours replace are where VanillaFireflies
            // reaches for them: the type and field must exist in the game's own
            // VSEssentials, or the switch-off silently fails and both draw.
            string game = Environment.GetEnvironmentVariable("VINTAGE_STORY") ?? "";
            string dll = Path.Combine(game, "Mods", "VSEssentials.dll");
            bool found = false;
            string detail = dll;
            if (File.Exists(dll))
            {
                using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(dll));
                var md = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
                foreach (var th in md.TypeDefinitions)
                {
                    var t = md.GetTypeDefinition(th);
                    if (md.GetString(t.Namespace) + "." + md.GetString(t.Name) != VanillaFireflies.SystemTypeName) continue;
                    foreach (var fh in t.GetFields())
                        if (md.GetString(md.GetFieldDefinition(fh).Name) == VanillaFireflies.FieldName) found = true;
                }
                detail = found ? "found" : VanillaFireflies.SystemTypeName + "::" + VanillaFireflies.FieldName + " missing";
            }
            check("fireflies: vanilla's firefly particles are where the switch-off looks for them", found, detail);
        }

        // --- Textured leaves (v15) -------------------------------------------

        private static void TexturedLeaves(Action<string, bool, string> check)
        {
            // A synthetic leaf texture in the game's manner: greyscale leaves
            // that overlap, each one tone. Light leaves (200) on a lattice,
            // dark ones (110) between them, touching.
            int w = 64, h = 64;
            var tex = new int[w * h];
            void Disc(int cx, int cy, int r, int grey)
            {
                for (int y = cy - r; y <= cy + r; y++)
                    for (int x = cx - r; x <= cx + r; x++)
                    {
                        if ((x - cx) * (x - cx) + (y - cy) * (y - cy) > r * r) continue;
                        int px = ((x % w) + w) % w, py = ((y % h) + h) % h;
                        tex[py * w + px] = unchecked((int)0xFF000000) | (grey << 16) | (grey << 8) | grey;
                    }
            }
            for (int j = 0; j < 4; j++) for (int i = 0; i < 4; i++) Disc(8 + i * 16, 8 + j * 16, 6, 110);
            for (int j = 0; j < 4; j++) for (int i = 0; i < 4; i++) Disc(16 + i * 16, 16 + j * 16, 5, 200);

            int[] sheet = LeafSprites.BuildSheet(tex, w, h, 7, out int cell, out int sprites);
            int size = LeafSprites.Side * cell;
            bool oneTone = sheet != null, connected = sheet != null, inBand = sheet != null;
            for (int sIdx = 0; sheet != null && sIdx < LeafSprites.Side * LeafSprites.Side; sIdx++)
            {
                int ox = (sIdx % LeafSprites.Side) * cell, oy = (sIdx / LeafSprites.Side) * cell;
                var tones = new System.Collections.Generic.HashSet<int>();
                int filled = 0, sx = -1, sy = -1;
                for (int y = 0; y < cell; y++)
                    for (int x = 0; x < cell; x++)
                    {
                        int c = sheet[(oy + y) * size + ox + x];
                        if (LeafSprites.Alpha(c) < LeafSprites.AlphaCut) continue;
                        tones.Add(c & 0xff); filled++; sx = x; sy = y;
                    }
                if (filled == 0) continue;
                // One leaf: one tone (it stopped at the edge of the leaf it
                // overlaps), one 4-connected piece, a leaf-sized share of the cell.
                if (tones.Count != 1) oneTone = false;
                float fill = filled / (float)(cell * cell);
                if (fill < LeafSprites.MinFill || fill > LeafSprites.MaxFill) inBand = false;
                var seen = new bool[cell * cell];
                var stack = new System.Collections.Generic.Stack<int>();
                stack.Push(sy * cell + sx); seen[sy * cell + sx] = true; int reached = 0;
                while (stack.Count > 0)
                {
                    int i = stack.Pop(); reached++;
                    int cx = i % cell, cy = i / cell;
                    foreach (var (nx, ny) in new[] { (cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1) })
                    {
                        if (nx < 0 || ny < 0 || nx >= cell || ny >= cell || seen[ny * cell + nx]) continue;
                        if (LeafSprites.Alpha(sheet[(oy + ny) * size + ox + nx]) < LeafSprites.AlphaCut) continue;
                        seen[ny * cell + nx] = true; stack.Push(ny * cell + nx);
                    }
                }
                if (reached != filled) connected = false;
            }
            check("textured leaves: each sprite is one whole leaf cut from the texture - one tone, one piece, leaf-sized",
                  sheet != null && sprites >= 12 && oneTone && connected && inBand,
                  $"{sprites} sprites of {cell} px; one tone {oneTone}, connected {connected}, in band {inBand}");

            // The tint is the game's colour over the texture's grey.
            int sample = unchecked((int)0xFF000000) | (200 << 16) | (100 << 8) | 50;
            LeafSprites.Tint(new[] { sample, sample }, 2, 100f, out float tr, out float tg, out float tb);
            bool tint = Math.Abs(tr - 2f) < 1e-4 && Math.Abs(tg - 1f) < 1e-4 && Math.Abs(tb - 0.5f) < 1e-4 &&
                        Math.Abs(LeafSprites.MeanGrey(new[] { sample, 0 }) - 350f / 3f) < 1e-3;

            // Flight: slow fall and little drift in still air, carried at about
            // the gale's speed in one, tumbling harder when blown.
            var rnd = new Random(3);
            LeafFlight.State calm = LeafFlight.Start(0, 100, 0, 0, 0, 0, rnd);
            LeafFlight.State gale = calm;
            float calmRoll0 = calm.Roll;
            for (int i = 0; i < 600; i++) { LeafFlight.Step(ref calm, 1f / 60f, 1f, 0f, 0f); LeafFlight.Step(ref gale, 1f / 60f, 1f, 0f, 1f); }
            double calmFall = (100 - calm.Y) / 10.0, galeAlong = gale.X / 10.0;
            float calmSpin = Math.Abs(calm.Roll - calmRoll0), galeSpin = Math.Abs(gale.Roll - calmRoll0);
            bool flight = calmFall > 0.5 && calmFall < 1.0 && Math.Abs(calm.X) < 4.0 &&
                          galeAlong > 0.6 * LeafFlight.GaleDrift && galeSpin > calmSpin * 2f;
            var landed = calm;
            LeafFlight.Land(ref landed, 64.02, false);
            for (int i = 0; i < 60; i++) LeafFlight.Settle(ref landed, 1f / 60f);
            bool rest = landed.Landed && landed.VY == 0f && Math.Abs(landed.Pitch - Math.PI / 2) < 0.05 &&
                        LeafFlight.Shrink(-1f, 1f) == 1f && LeafFlight.Shrink(2f, 1f) == 0f;
            check("textured leaves: tinted by the game's colour, fall slowly, ride the wind, lie flat when down",
                  tint && flight && rest,
                  $"tint {tr:0.00}/{tg:0.00}/{tb:0.00}; still fall {calmFall:0.00} b/s drift {calm.X:0.0}; gale {galeAlong:0.0} b/s; spin {calmSpin:0.0}/{galeSpin:0.0}; rest {rest}");
        }

        // --- Season ----------------------------------------------------------

        private static void Season(Action<string, bool, string> check)
        {
            GradeSample basis = GradeSample.Neutral;
            var only = new AdaptiveGradeConfig
            {
                Enabled = true, TimeOfDayStrength = 0f, WeatherStrength = 0f, BiomeStrength = 0f,
                IndoorStrength = 0f, DepthStrength = 0f, UnderwaterStrength = 0f, SeasonStrength = 1f,
                StyleStrength = 0f,
            };
            var zero = new AdaptiveGradeConfig
            {
                Enabled = true, TimeOfDayStrength = 0f, WeatherStrength = 0f, BiomeStrength = 0f,
                IndoorStrength = 0f, DepthStrength = 0f, UnderwaterStrength = 0f, SeasonStrength = 0f,
                StyleStrength = 0f,
            };
            GradeSample Eval(EnvironmentState w, AdaptiveGradeConfig c) => GradeStack.Evaluate(basis, w, c);
            var winter = Eval(World(0.9f, 0f, 0.5f, 0f, 1f, winter: 1f), only);
            var autumn = Eval(World(0.9f, 0f, 0.5f, 0f, 1f, autumn: 1f), only);
            var summer = Eval(World(0.9f, 0f, 0.5f, 0f, 1f, summer: 1f), only);
            var night = Eval(World(0.0f, 0f, 0.5f, 0f, 1f, winter: 1f), only);
            var indoor = Eval(World(0.9f, 0f, 0.5f, 0f, 0f, winter: 1f), only);
            var off = Eval(World(0.9f, 0f, 0.5f, 0f, 1f, winter: 1f), zero);
            bool ok = winter.Temperature < -0.1f && winter.Saturation < 1f &&
                      autumn.Temperature <= 0f && autumn.Temperature > -0.01f &&
                      summer.Temperature > 0f &&
                      night.DistanceTo(basis) < 1e-6f && indoor.DistanceTo(basis) < 1e-6f &&
                      off.DistanceTo(basis) == 0f &&
                      Math.Abs(winter.Temperature) <= 0.25f && Math.Abs(winter.Saturation - 1f) <= 0.15f;
            check("seasonal light: winter cooler and crisper, autumn never warmer (its palette is the game's), summer warm, none at night or indoors, 0 is vanilla",
                  ok, $"winter T {winter.Temperature:0.00} S {winter.Saturation:0.00}, autumn T {autumn.Temperature:0.00}, summer T {summer.Temperature:0.00}");
        }
    }
}
