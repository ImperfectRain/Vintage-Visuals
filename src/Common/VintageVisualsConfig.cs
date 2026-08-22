using System.Collections.Generic;

namespace VintageVisuals.Common
{
    /// <summary>
    /// The on-disk config, serialized to
    /// <c>VintagestoryData/ModConfig/vintagevisuals.json</c>.
    ///
    /// Public properties rather than fields: Newtonsoft's default contract
    /// resolver handles both, but properties are what every other VS mod uses
    /// and what round-trips predictably if the resolver is ever customised.
    /// </summary>
    public class VintageVisualsConfig
    {
        /// <summary>
        /// Dumps every patched shader to <c>VintagestoryData/ShaderDebug/</c>.
        /// This is the fastest way to answer "did my patch actually land, and
        /// what did the merged GLSL end up looking like".
        /// </summary>
        public bool EnableShaderDebugDump { get; set; } = false;

        /// <summary>
        /// Writes <c>VintageVisuals/scenereport.txt</c>: what the world is
        /// doing, what the scene asked for, who asked, and what each subsystem
        /// was allowed to take.
        ///
        /// Effect provenance. When an image comes out too dark or too grey the
        /// question is never "is something broken", it is which of nine
        /// influences and four claimants did it - and the alternative is
        /// switching subsystems off one at a time.
        ///
        /// Written once when the flag is set, then cleared, like the material
        /// report: a per-frame file write is not a diagnostic, it is a fault.
        /// </summary>
        public bool WriteSceneReport { get; set; } = false;

        public ColorGradeConfig ColorGrade { get; set; } = new ColorGradeConfig();

        public AdaptiveExposureConfig AdaptiveExposure { get; set; } = new AdaptiveExposureConfig();

        public AdaptiveGradeConfig AdaptiveGrade { get; set; } = new AdaptiveGradeConfig();

        public PseudoPbrConfig PseudoPBR { get; set; } = new PseudoPbrConfig();

        public WeatherConfig Weather { get; set; } = new WeatherConfig();

        /// <summary>
        /// Clamps every value into its supported range, returning a description
        /// of anything that had to be corrected.
        ///
        /// Config is hand-edited, and an out-of-range value here means a fully
        /// white or fully black screen — a state in which the player cannot
        /// read the log to find out why. Clamping keeps the game usable and
        /// says what it did.
        /// </summary>
        public List<string> ClampToValidRanges()
        {
            var corrections = new List<string>();
            ColorGrade.ClampToValidRanges(corrections);
            AdaptiveExposure.ClampToValidRanges(corrections);
            AdaptiveGrade.ClampToValidRanges(corrections);
            PseudoPBR.ClampToValidRanges(corrections);
            Weather.ClampToValidRanges(corrections);
            return corrections;
        }
    }

    /// <summary>
    /// How much the world is allowed to grade itself.
    ///
    /// One strength per influence rather than one master dial, because these
    /// are matters of taste that pull in different directions: a player who
    /// wants caves to drain of colour may well not want their deserts tinted,
    /// and vice versa. Every one of them is zero-is-off, so the panel also
    /// works as a way of finding out which influence is responsible for
    /// something the player does not like.
    /// </summary>
    public class AdaptiveGradeConfig
    {
        /// <summary>
        /// Master toggle. Off means the player's own grading settings reach the
        /// screen unmodified - not that grading stops.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// A named look the whole stack leans toward: None, Filmic, Muted,
        /// Vivid, Cold or Warm.
        ///
        /// Applied AFTER the world, not before, so weather and biome read
        /// through the style rather than fighting it. It is one more capped
        /// contributor rather than a preset that replaces the player's
        /// settings - a style with the force of a preset would flatten every
        /// weather response underneath it, which is the failure mode of most
        /// shader packs and the thing this project's stated goal rules out.
        /// </summary>
        public string Style { get; set; } = "None";

        /// <summary>How far toward the style to lean. 0 is off.</summary>
        public float StyleStrength { get; set; } = 0.6f;

        /// <summary>Golden hour and the Purkinje shift at night.</summary>
        public float TimeOfDayStrength { get; set; } = 1.0f;

        /// <summary>Rain and cloud cover draining colour and contrast.</summary>
        public float WeatherStrength { get; set; } = 1.0f;

        /// <summary>Heat, cold, aridity and lushness at the player's position.</summary>
        public float BiomeStrength { get; set; } = 0.7f;

        /// <summary>Firelight warmth and harder shadows in an enclosed space.</summary>
        public float IndoorStrength { get; set; } = 0.8f;

        /// <summary>Colour draining out with depth below sea level.</summary>
        public float DepthStrength { get; set; } = 0.8f;

        /// <summary>The blue-green shift of being submerged.</summary>
        public float UnderwaterStrength { get; set; } = 0.6f;

        /// <summary>
        /// Seconds for the grade to travel most of the way to a new one.
        ///
        /// Slow on purpose. Every influence changes discontinuously somewhere -
        /// a doorway, a shower starting, the camera going under - and the
        /// easing is what makes those read as the world changing rather than as
        /// the renderer glitching. Too fast and stepping through a door is a
        /// flash; too slow and the grade is still catching up with the last
        /// biome.
        /// </summary>
        public float ResponseSeconds { get; set; } = 2.5f;

        internal void ClampToValidRanges(List<string> corrections)
        {
            TimeOfDayStrength = ColorGradeConfig.Clamp(TimeOfDayStrength, 0.0f, 2.0f,
                "AdaptiveGrade.TimeOfDayStrength", corrections);
            WeatherStrength = ColorGradeConfig.Clamp(WeatherStrength, 0.0f, 2.0f,
                "AdaptiveGrade.WeatherStrength", corrections);
            BiomeStrength = ColorGradeConfig.Clamp(BiomeStrength, 0.0f, 2.0f,
                "AdaptiveGrade.BiomeStrength", corrections);
            IndoorStrength = ColorGradeConfig.Clamp(IndoorStrength, 0.0f, 2.0f,
                "AdaptiveGrade.IndoorStrength", corrections);
            DepthStrength = ColorGradeConfig.Clamp(DepthStrength, 0.0f, 2.0f,
                "AdaptiveGrade.DepthStrength", corrections);
            UnderwaterStrength = ColorGradeConfig.Clamp(UnderwaterStrength, 0.0f, 2.0f,
                "AdaptiveGrade.UnderwaterStrength", corrections);
            StyleStrength = ColorGradeConfig.Clamp(StyleStrength, 0.0f, 1.0f,
                "AdaptiveGrade.StyleStrength", corrections);
            ResponseSeconds = ColorGradeConfig.Clamp(ResponseSeconds, 0.1f, 30.0f,
                "AdaptiveGrade.ResponseSeconds", corrections);
        }
    }

    /// <summary>
    /// Phase 2 weather system. Currently one effect: what rain does to how
    /// surfaces respond to light.
    /// </summary>
    public class WeatherConfig
    {
        /// <summary>
        /// Master toggle. Off eases wetness back to dry rather than snapping,
        /// so flipping it mid-storm is not a visible jolt.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// How wet rain makes surfaces look. 0 is off; 1 is the tuned look.
        ///
        /// Above 1 exaggerates past anything physical, which is a legitimate
        /// place to be for a stylised look and a silly one for a realistic one.
        /// </summary>
        public float WetnessStrength { get; set; } = 1.0f;

        /// <summary>
        /// Seconds for a soaked surface to dry once the rain stops.
        ///
        /// The asymmetry is the whole effect. Surfaces wet in seconds and dry
        /// over a minute or more; easing both at the same rate reads as a fade
        /// rather than as weather.
        /// </summary>
        public float DryingSeconds { get; set; } = 60.0f;

        /// <summary>
        /// How exposed to the sky a surface must be before rain reaches it.
        ///
        /// The signal is vanilla's per-vertex sun light, which bleeds sideways
        /// under an overhang - so a linear reading only ever dries out fully
        /// enclosed spaces, and a porch stays as wet as the lawn. Raising this
        /// requires near-full exposure and gets overhangs, tree canopy and
        /// doorways back.
        ///
        /// It is a threshold on a soft signal, not a rain occlusion test. The
        /// game does have one - it is how torches are extinguished - but it
        /// answers per block on the CPU, and this question is per fragment.
        /// </summary>
        public float RainCoverThreshold { get; set; } = 0.82f;

        /// <summary>
        /// How hard rain lands in the water it left. 0 is still water.
        ///
        /// Rides the material shader's normal, like wetness does, rather than
        /// drawing anything: a ripple is a disturbance of the water film, and
        /// what the eye reads is the highlight it breaks up. It needs wetness
        /// to be doing something first - a dry surface has nothing to ripple.
        /// </summary>
        public float RippleStrength { get; set; } = 0.8f;

        /// <summary>
        /// How completely cloud cover diffuses the sun. 0 leaves lighting alone.
        ///
        /// A clear sky is a small, very bright source and makes sharp
        /// highlights; an overcast one is a source the size of the sky, dimmer
        /// and coming from everywhere. So the direct lobe weakens and the sky
        /// term gains. Modelling it as "everything gets darker" is the usual
        /// way of getting an overcast day wrong.
        /// </summary>
        public float OvercastStrength { get; set; } = 0.7f;

        /// <summary>
        /// How much frost changes a surface, on top of vanilla's own frost.
        /// 0 leaves vanilla's frost exactly as it was.
        ///
        /// Vanilla already decides WHERE frost is: `frostAlpha` is a
        /// per-fragment mask built from the block's own frostable bit, the
        /// local temperature, sunlight and value noise, and entities get
        /// `fragFrostAlpha` the same way. What vanilla does with it is tint the
        /// surface white. What it does not do is make the surface behave like
        /// frost - rough, and with the highlight of whatever it covers killed.
        /// That is the whole of what this adds.
        /// </summary>
        public float FrostStrength { get; set; } = 0.8f;

        /// <summary>
        /// How much snow dusts surfaces the sky can see. 0 is off.
        ///
        /// Deliberately thin, and it does not compete with vanilla: the game
        /// places snow BLOCKS and accumulates real depth. This is the film on
        /// everything else - a fence rail, a crate, a rock - gated the same way
        /// rain is, because it falls out of the sky. Cubed against the surface
        /// normal rather than squared, because snow slides off a slope where
        /// rain merely runs down it.
        /// </summary>
        public float SnowDusting { get; set; } = 0.6f;

        /// <summary>How much rain thickens the air. 0 leaves vanilla fog alone.</summary>
        public float FogStrength { get; set; } = 0.35f;

        /// <summary>How much rain drains colour from the fog and cools it.</summary>
        public float FogTint { get; set; } = 0.6f;

        /// <summary>
        /// Depth of cloud shadows on the ground. 0 is off.
        ///
        /// Scaled by daylight before it reaches the shader, so this is the
        /// depth at noon rather than a constant darkening.
        /// </summary>
        public float CloudShadowStrength { get; set; } = 0.35f;

        /// <summary>
        /// Read cloud positions from the game's own cloud renderer, so shadows
        /// land under the clouds that cast them.
        ///
        /// Off asks instead for a noise field of the mod's own, which moves and
        /// covers correctly but cannot line up with the sky.
        ///
        /// Leaving this ON and having the read FAIL now draws no cloud shadows
        /// at all, rather than quietly substituting that noise field. The
        /// substitution was the single most expensive decision in this
        /// subsystem: an invented field that covers plausibly looks exactly like
        /// a working effect in need of tuning, so four rounds of debugging went
        /// into tuning shadows that had never once read the game's clouds. The
        /// log says which path is live, and says it loudly.
        /// </summary>
        public bool CloudsFromGame { get; set; } = true;

        /// <summary>
        /// Which cloud diagnostic to draw instead of shading. 0 is off.
        ///
        /// Not a look control, and no longer a yes/no. Cloud shadows have
        /// failed four times and each round blamed a different multiplied term,
        /// so the views are separated by the question they answer:
        ///
        ///   1  the field the shadow uses, thrown along the light - is the GLSL
        ///      running, and is it merely too faint?
        ///   2  the tile field straight down, no throw, no fade - does the
        ///      window describe the sky that is actually overhead? Stand still,
        ///      look up, look down, compare.
        ///   3  the window's own tile grid and edge - where is it sampling?
        ///
        /// 2 is the one that has never been answered. Everything else is
        /// downstream of it.
        /// </summary>
        public float CloudDebugView { get; set; } = 0f;

        /// <summary>Blocks across one cloud cell. Larger means broader, slower shadows.</summary>
        public float CloudScale { get; set; } = 190.0f;

        /// <summary>How fast cloud shadows drift, in cells per minute, along the world's wind.</summary>
        public float CloudDriftSpeed { get; set; } = 0.9f;

        /// <summary>
        /// World height the shadow-casting cloud deck sits at, in blocks.
        ///
        /// This is what decides how far a shadow slides from the thing casting
        /// it as the sun drops, so it is the control for whether shadows sit
        /// under the clouds or somewhere off to the side of them. Vanilla's
        /// clouds are much higher than this default; using their real altitude
        /// moves the shadow the better part of a kilometre at a low sun, which
        /// reads as a bug rather than as evening.
        /// </summary>
        public float CloudHeight { get; set; } = 160.0f;

        internal void ClampToValidRanges(List<string> corrections)
        {
            WetnessStrength = ColorGradeConfig.Clamp(WetnessStrength, 0.0f, 2.0f,
                "Weather.WetnessStrength", corrections);
            DryingSeconds = ColorGradeConfig.Clamp(DryingSeconds, 1.0f, 600.0f,
                "Weather.DryingSeconds", corrections);
            RainCoverThreshold = ColorGradeConfig.Clamp(RainCoverThreshold, 0.0f, 1.0f,
                "Weather.RainCoverThreshold", corrections);
            FrostStrength = ColorGradeConfig.Clamp(FrostStrength, 0.0f, 1.0f,
                "Weather.FrostStrength", corrections);
            SnowDusting = ColorGradeConfig.Clamp(SnowDusting, 0.0f, 1.0f,
                "Weather.SnowDusting", corrections);
            RippleStrength = ColorGradeConfig.Clamp(RippleStrength, 0.0f, 1.0f,
                "Weather.RippleStrength", corrections);
            OvercastStrength = ColorGradeConfig.Clamp(OvercastStrength, 0.0f, 1.0f,
                "Weather.OvercastStrength", corrections);
            FogStrength = ColorGradeConfig.Clamp(FogStrength, 0.0f, 1.0f,
                "Weather.FogStrength", corrections);
            FogTint = ColorGradeConfig.Clamp(FogTint, 0.0f, 1.0f,
                "Weather.FogTint", corrections);
            CloudShadowStrength = ColorGradeConfig.Clamp(CloudShadowStrength, 0.0f, 1.0f,
                "Weather.CloudShadowStrength", corrections);
            CloudScale = ColorGradeConfig.Clamp(CloudScale, 32.0f, 512.0f,
                "Weather.CloudScale", corrections);
            CloudDriftSpeed = ColorGradeConfig.Clamp(CloudDriftSpeed, 0.0f, 8.0f,
                "Weather.CloudDriftSpeed", corrections);
            CloudHeight = ColorGradeConfig.Clamp(CloudHeight, 40.0f, 400.0f,
                "Weather.CloudHeight", corrections);
            CloudDebugView = ColorGradeConfig.Clamp(CloudDebugView, 0.0f, 3.0f,
                "Weather.CloudDebugView", corrections);
        }
    }

    /// <summary>
    /// Phase 4 material system: what each block face is made of, and how that
    /// changes the way light lands on it.
    /// </summary>
    public class PseudoPbrConfig
    {
        /// <summary>
        /// Master switch for the rendering half. False leaves the atlas build
        /// and the reports alone — they are diagnostics that cost nothing at
        /// runtime — and stops chunkopaque.fsh being patched at all, so the
        /// world renders from vanilla source.
        ///
        /// Defaults to FALSE, unlike every other feature in this mod. Not
        /// because the code is believed wrong, but because of what it costs
        /// when it is: this is the only subsystem that patches the shader
        /// drawing the world, and its two failures so far were a sepia screen
        /// and missing terrain, neither of which a player could diagnose. The
        /// other subsystems degrade to "no effect"; this one degrades to "no
        /// world". Until it has been seen working on a real GPU, opting in is
        /// the right default. Flip it once someone has looked.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// How much emitting surfaces read as HOT rather than merely bright.
        /// 0 is vanilla.
        ///
        /// This game is about fire. Forges, bloomeries, firepits, lamps and
        /// lava are what a player builds a life around, and the loop is leaving
        /// a warm lit shelter for a cold dark wilderness - so light sources
        /// matter more here than in a game that is not built on darkness.
        ///
        /// Driven entirely by vanilla's own glowLevel, which is the game's
        /// per-fragment answer to "does this emit". Nothing is inferred from
        /// pixel brightness: a white marble block is not a lamp, and a
        /// brightness heuristic cannot tell the difference.
        /// </summary>
        public float EmissiveStrength { get; set; } = 0.8f;

        /// <summary>
        /// How much a bright emitter shifts toward white at its core.
        ///
        /// Real emitters do: a forge's centre is paler than its edge and iron
        /// at welding heat is nearly white. This is most of what makes
        /// something read as hot rather than as painted orange.
        /// </summary>
        public float EmissiveTemperature { get; set; } = 0.55f;

        /// <summary>
        /// How much a flame is allowed to breathe.
        ///
        /// Position-seeded, so two torches on the same wall do not flicker in
        /// step - that is the detail separating "the room is flickering" from
        /// "several fires are burning in it". Only downward: a flame dips and
        /// recovers, it does not periodically burn brighter than it burns.
        /// </summary>
        public float EmissiveFlicker { get; set; } = 0.5f;

        /// <summary>
        /// How much emitters add to vanilla's own bloom pass. 0 is vanilla.
        ///
        /// Driven by the emission the material system computed rather than by
        /// how bright the pixel came out, which is the difference between a
        /// bloom that finds light sources and one that finds snow. It feeds
        /// vanilla's existing findbright/blur pass rather than adding a second
        /// one, which also keeps it restrained by construction.
        /// </summary>
        public float EmissiveBloom { get; set; } = 0.35f;

        /// <summary>
        /// Light passing through leaves, grass and crops. 0 is vanilla.
        ///
        /// A leaf is thin: light hitting its far side scatters through rather
        /// than stopping, so a canopy with the sun behind it glows. Vanilla
        /// shades foliage with the same opaque diffuse it uses for stone, so
        /// this is a gap rather than a re-tint.
        ///
        /// Which fragments count as foliage is the game's own answer - the
        /// wind-mode bits it already sets on anything that bends - rather than
        /// a second guess that could disagree with it.
        /// </summary>
        public float FoliageTranslucency { get; set; } = 0.7f;

        /// <summary>
        /// Occlusion in the grooves, derived from the material normal. 0 is
        /// vanilla.
        ///
        /// The scale nothing else in the frame covers. Vanilla ships SSAO and
        /// SSAO works on geometry: it knows a block sits in a corner. It has no
        /// idea the mortar line between two bricks is a groove, because at the
        /// depth buffer's resolution it is not one.
        ///
        /// Costs four extra texture samples, which is why it has its own
        /// control and rides the same distance fade the relief does.
        /// </summary>
        public float CavityStrength { get; set; } = 0.6f;

        /// <summary>
        /// Whether mobs, animals and players get the same lighting model as the
        /// world they stand in.
        ///
        /// Off means they keep vanilla's flat diffuse while the ground beneath
        /// them has a specular response, a sky term and torch highlights -
        /// which is what shipped for a long time and is more obvious than it
        /// sounds once noticed.
        ///
        /// Defaults to TRUE unlike the terrain half, because the failure mode
        /// is milder: this patches only entityanimated.fsh, and a failure costs
        /// mobs their highlight rather than costing the world its surfaces.
        /// </summary>
        public bool EntityLighting { get; set; } = true;

        /// <summary>
        /// How matte a creature reads. Entities have no derived material atlas
        /// - deriving normals from a mob skin would read painted-on fur shading
        /// as geometry - so one roughness covers all of them.
        /// </summary>
        public float EntityRoughness { get; set; } = 0.65f;

        /// <summary>How much of the entity specular lobe reaches the image. 0 is vanilla.</summary>
        public float EntitySpecular { get; set; } = 0.8f;

        /// <summary>
        /// Whether falling leaves, pollen, dust, sparks and smoke share the
        /// world's lighting.
        ///
        /// They belonged to no lighting model at all before - the same defect
        /// entities had, where a thing lit by one set of rules drifts past a
        /// world lit by another and never sits in it.
        /// </summary>
        public bool ParticleLighting { get; set; } = true;

        /// <summary>
        /// How much of the particle specular lobe reaches the image.
        ///
        /// Low on purpose. A particle is small, numerous and in motion, and a
        /// highlight that reads as detail on a wall reads as twinkling noise on
        /// a cloud of dust.
        /// </summary>
        public float ParticleSpecular { get; set; } = 0.45f;

        /// <summary>
        /// Entity debug views, numbered in the entity path's own terms:
        /// 0 off, 1 the specular lobe alone, 2 wetness, 3 scene restraint.
        ///
        /// A separate list from the material debug views on purpose. Those
        /// numbers mean material layers and an entity has none of them.
        /// </summary>
        public float EntityDebugView { get; set; } = 0f;

        /// <summary>
        /// Global multiplier on the surface relief, on top of the per-material
        /// strength already baked into the atlas.
        ///
        /// 1.0 is the tuned look. This exists because relief is the one part of
        /// the material system that is purely a matter of taste and cannot be
        /// judged from a texture — some players will want log grooves obvious,
        /// others will want the vanilla flat look back without losing the rest.
        /// 0 flattens everything, which is not the same as Enabled=false: the
        /// shader still samples, so it still costs what it costs.
        /// </summary>
        public float NormalStrength { get; set; } = 1.0f;

        /// <summary>
        /// Global multiplier on the microfacet specular, and on the energy it
        /// takes back out of the diffuse.
        ///
        /// Both, deliberately, so 0 is exactly vanilla. A player who turns the
        /// effect off has to get their old image back, not a darker one.
        /// </summary>
        public float SpecularStrength { get; set; } = 1.0f;

        /// <summary>
        /// Shifts every material's roughness. Negative is glossier, positive is
        /// more matte.
        ///
        /// The single most useful control for style, because roughness is what
        /// separates a look that reads as "wet" from one that reads as "dry" -
        /// and where that line sits is taste, not physics.
        /// </summary>
        public float RoughnessBias { get; set; } = 0.0f;

        /// <summary>
        /// How metallic the reflective materials read. 0 makes every surface a
        /// dielectric with a white highlight; 1 lets metals tint their highlight
        /// by their own albedo, which is what makes copper look like copper.
        ///
        /// Worth turning down for a flatter, more stylised look, since a
        /// coloured specular is one of the strongest "modern renderer" cues.
        /// </summary>
        public float MetalResponse { get; set; } = 1.0f;
        /// <summary>
        /// Sunlight broken into moving patches by the leaves overhead.
        ///
        /// Gated on vanilla's own per-vertex sun light level, which is partial
        /// under a canopy and full or zero everywhere else - so the effect can
        /// only appear where there is genuinely something leafy above. The
        /// pattern is invented; where it is allowed to exist is not.
        ///
        /// Redistributes rather than removes: gaps brighten by as much as the
        /// shade between them darkens, so it never asks VisualBudget for
        /// anything.
        /// </summary>
        /// <summary>
        /// How view-aware the occlusion on reflections is.
        ///
        /// 0 keeps the flat cavity the shader applied to specular before, so it
        /// reproduces the previous image exactly; 1 uses a roughness- and
        /// view-dependent specular occlusion instead. It is not an on/off for
        /// occlusion itself - that is CavityStrength - but for whether
        /// reflections are occluded by the same amount as the ambient.
        /// </summary>
        public float SpecularOcclusion { get; set; } = 1.0f;

        /// <summary>
        /// Energy a single-scatter GGX lobe loses to multiple bounces between
        /// microfacets, put back.
        ///
        /// Only ever adds, only ever to rough surfaces, and only ever a few
        /// percent on the materials this shader draws. 0 is the single-scatter
        /// model that shipped before.
        /// </summary>
        public float EnergyCompensation { get; set; } = 1.0f;

        /// <summary>
        /// How far a fibrous surface stretches its highlight across the grain.
        ///
        /// Gated on MEASURED coherence rather than on a material label: the
        /// effect scales with how linear the surface's own microstructure
        /// actually is, so wood grain gets it and mottled stone does not,
        /// without the shader needing to know which block it is looking at.
        /// 0 is the isotropic lobe that shipped before.
        /// </summary>
        public float GrainAnisotropy { get; set; } = 0.6f;

        public float SunDapple { get; set; } = 0.35f;

        /// <summary>
        /// Visible beams of light through the canopy.
        ///
        /// Writes into vanilla's OWN god-ray channel rather than drawing
        /// anything, so the beams are the game's pass and inherit the player's
        /// god-ray graphics setting. With god-rays off in the game's settings
        /// this writes a mask nothing reads and the effect is simply absent.
        /// </summary>
        public float SunShafts { get; set; } = 0.45f;


        /// <summary>
        /// Strength of the sky reflection, using vanilla's fog colour as the
        /// environment.
        ///
        /// Without it, the sun is the only light this shader knows about, so a
        /// metal block in shade or indoors has no highlight at all and reads as
        /// dark plastic. It is the cheapest single step toward realism here,
        /// and turning it off is the cheapest step toward a flat look.
        /// </summary>
        public float AmbientSpecular { get; set; } = 0.35f;

        /// <summary>
        /// Geometric specular antialiasing strength (Kaplanyan et al. 2016;
        /// Tokuyoshi and Kaplanyan 2019).
        ///
        /// Defaults to full, and should normally stay there. Derived normals
        /// carry far higher frequencies than a hand-authored map, so without
        /// this a rough surface with a tight highlight sparkles as the camera
        /// moves. Exposed because it is a real trade-off - it widens highlights
        /// slightly - and because being able to turn it off is how anyone
        /// confirms it is doing something.
        /// </summary>
        public float SpecularAntiAliasing { get; set; } = 1.0f;

        /// <summary>
        /// Distance in blocks at which surface relief has faded to nothing.
        /// Full strength is held to a third of it.
        ///
        /// Higher costs nothing in frame time but shows more aliasing, since
        /// the material atlas carries no mipmaps and one screen pixel covers
        /// many texels at range.
        /// </summary>
        public float DetailDistance { get; set; } = 48.0f;

        /// <summary>
        /// Strength of highlights from torches, lava, lanterns and glowing
        /// blocks.
        ///
        /// Until this existed, the sun was the only light the material system
        /// could produce a highlight from - so underground, where every light
        /// is a block light, none of it did anything. Deliberately not scaled
        /// by daylight or shadow: a torch burns in a cave at midnight.
        /// </summary>
        public float BlockLightSpecular { get; set; } = 1.0f;

        /// <summary>
        /// How much to trust the estimated direction of block light.
        ///
        /// Vanilla bakes every light source into one directionless colour, so
        /// the direction is recovered from the gradient of that colour across
        /// the surface - light gets brighter toward whatever is emitting it. At
        /// 0 block light is treated as purely ambient, which is safe and dull;
        /// at 1 the estimate is trusted fully, which gives a highlight that
        /// tracks a torch as you walk past it and can wobble where the light
        /// field is noisy.
        /// </summary>
        public float BlockLightDirectionality { get; set; } = 0.7f;

        /// <summary>
        /// Renders one layer of the material system on its own instead of the
        /// finished image. 0 renders normally.
        ///
        ///   1  normal map as stored (blue forced flat, matches the preview PNG)
        ///   2  roughness
        ///   3  specular mask
        ///   4  relief contribution, biased so "no change" is mid grey
        ///   5  specular highlight on its own
        ///   6  perturbed normal in world space
        ///   7  reflectance at normal incidence (grey = dielectric, coloured = metal)
        ///   8  the roughness the shading model actually uses, after bias and
        ///      specular antialiasing
        ///   9  estimated block-light direction (flat = treated as ambient)
        ///
        /// A float rather than an int because ConfigLib's float settings are
        /// the ones this mod has confirmed working in game; an integer category
        /// would be a second unverified thing in the same change. Stepped by 1
        /// in the GUI, rounded in the shader.
        /// </summary>
        public float DebugView { get; set; } = 0.0f;

        /// <summary>
        /// Writes VintagestoryData/VintageVisuals/material-report.txt listing
        /// how every loaded block was classified.
        ///
        /// On by default while the subsystem is being built: it is the only way
        /// to see whether modded blocks classify sensibly, it costs one file
        /// write per session, and it changes nothing about rendering. Turn it
        /// off once the classification looks right.
        /// </summary>
        public bool WriteMaterialReport { get; set; } = true;

        /// <summary>
        /// Derives the material atlas at world load, caching it to disk.
        ///
        /// Costs seconds of CPU on the first run for a given texture set and
        /// almost nothing afterwards. Nothing consumes the atlas yet, so this
        /// currently only produces the cache and the preview images.
        /// </summary>
        public bool BuildMaterialAtlas { get; set; } = true;

        /// <summary>
        /// Writes the derived atlas as viewable BMPs alongside the cache.
        ///
        /// On while the subsystem is being built: these maps cannot be judged
        /// from numbers, and looking at them is the only way to tell whether
        /// wood grooves read as grooves. Turn it off to save the disk write.
        /// </summary>
        public bool WriteAtlasPreview { get; set; } = true;

        internal void ClampToValidRanges(List<string> corrections)
        {
            // Capped at 2 rather than left open. Above roughly 1.4 the
            // reconstructed Z collapses toward zero and every face starts
            // shading as though lit from its own edge, which reads as a
            // rendering fault rather than as strong relief.
            NormalStrength = ColorGradeConfig.Clamp(NormalStrength, 0.0f, 2.0f,
                "PseudoPBR.NormalStrength", corrections);
            SpecularStrength = ColorGradeConfig.Clamp(SpecularStrength, 0.0f, 2.0f,
                "PseudoPBR.SpecularStrength", corrections);
            SpecularOcclusion = ColorGradeConfig.Clamp(SpecularOcclusion, 0.0f, 1.0f,
                "PseudoPBR.SpecularOcclusion", corrections);
            EnergyCompensation = ColorGradeConfig.Clamp(EnergyCompensation, 0.0f, 1.0f,
                "PseudoPBR.EnergyCompensation", corrections);
            GrainAnisotropy = ColorGradeConfig.Clamp(GrainAnisotropy, 0.0f, 1.0f,
                "PseudoPBR.GrainAnisotropy", corrections);
            SunDapple = ColorGradeConfig.Clamp(SunDapple, 0.0f, 2.0f,
                "PseudoPBR.SunDapple", corrections);
            SunShafts = ColorGradeConfig.Clamp(SunShafts, 0.0f, 2.0f,
                "PseudoPBR.SunShafts", corrections);
            DebugView = ColorGradeConfig.Clamp(DebugView, 0.0f, 30.0f,
                "PseudoPBR.DebugView", corrections);
            RoughnessBias = ColorGradeConfig.Clamp(RoughnessBias, -0.5f, 0.5f,
                "PseudoPBR.RoughnessBias", corrections);
            MetalResponse = ColorGradeConfig.Clamp(MetalResponse, 0.0f, 1.0f,
                "PseudoPBR.MetalResponse", corrections);
            AmbientSpecular = ColorGradeConfig.Clamp(AmbientSpecular, 0.0f, 2.0f,
                "PseudoPBR.AmbientSpecular", corrections);
            SpecularAntiAliasing = ColorGradeConfig.Clamp(SpecularAntiAliasing, 0.0f, 2.0f,
                "PseudoPBR.SpecularAntiAliasing", corrections);
            EmissiveStrength = ColorGradeConfig.Clamp(EmissiveStrength, 0.0f, 2.0f,
                "PseudoPBR.EmissiveStrength", corrections);
            EmissiveTemperature = ColorGradeConfig.Clamp(EmissiveTemperature, 0.0f, 1.0f,
                "PseudoPBR.EmissiveTemperature", corrections);
            EmissiveFlicker = ColorGradeConfig.Clamp(EmissiveFlicker, 0.0f, 1.0f,
                "PseudoPBR.EmissiveFlicker", corrections);
            EmissiveBloom = ColorGradeConfig.Clamp(EmissiveBloom, 0.0f, 1.0f,
                "PseudoPBR.EmissiveBloom", corrections);
            FoliageTranslucency = ColorGradeConfig.Clamp(FoliageTranslucency, 0.0f, 2.0f,
                "PseudoPBR.FoliageTranslucency", corrections);
            CavityStrength = ColorGradeConfig.Clamp(CavityStrength, 0.0f, 2.0f,
                "PseudoPBR.CavityStrength", corrections);
            EntityRoughness = ColorGradeConfig.Clamp(EntityRoughness, 0.04f, 1.0f,
                "PseudoPBR.EntityRoughness", corrections);
            EntitySpecular = ColorGradeConfig.Clamp(EntitySpecular, 0.0f, 2.0f,
                "PseudoPBR.EntitySpecular", corrections);
            ParticleSpecular = ColorGradeConfig.Clamp(ParticleSpecular, 0.0f, 2.0f,
                "PseudoPBR.ParticleSpecular", corrections);
            EntityDebugView = ColorGradeConfig.Clamp(EntityDebugView, 0.0f, 3.0f,
                "PseudoPBR.EntityDebugView", corrections);
            DetailDistance = ColorGradeConfig.Clamp(DetailDistance, 4.0f, 192.0f,
                "PseudoPBR.DetailDistance", corrections);
            BlockLightSpecular = ColorGradeConfig.Clamp(BlockLightSpecular, 0.0f, 2.0f,
                "PseudoPBR.BlockLightSpecular", corrections);
            BlockLightDirectionality = ColorGradeConfig.Clamp(BlockLightDirectionality, 0.0f, 1.0f,
                "PseudoPBR.BlockLightDirectionality", corrections);
        }
    }

    /// <summary>
    /// Eye adaptation. Multiplies <see cref="ColorGradeConfig.Exposure"/>
    /// rather than replacing it, so a player who has dialled in a manual
    /// exposure keeps it and this rides on top.
    /// </summary>
    public class AdaptiveExposureConfig
    {
        /// <summary>
        /// On by default, unlike the tonemap. The tonemap is off because of an
        /// unresolved correctness question about colour space; this has no such
        /// question — it is bounded, clamped, and cannot blow the image out.
        /// The only risk is taste, and the effect is the point of the feature.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Multiplier in pitch darkness. Above 1 brightens.</summary>
        public float DarkGain { get; set; } = 1.6f;

        /// <summary>Multiplier in full light. 1.0 leaves bright scenes exactly as authored.</summary>
        public float BrightGain { get; set; } = 1.0f;

        /// <summary>
        /// Seconds to adapt toward darkness (the multiplier rising). Slow,
        /// mirroring how long human dark adaptation actually takes.
        /// </summary>
        public float BrightenSeconds { get; set; } = 4.0f;

        /// <summary>Seconds to adapt toward light (the multiplier falling). Fast.</summary>
        public float DarkenSeconds { get; set; } = 1.0f;

        internal void ClampToValidRanges(List<string> corrections)
        {
            DarkGain = ColorGradeConfig.Clamp(DarkGain, 0.25f, 4.0f, "AdaptiveExposure.DarkGain", corrections);
            BrightGain = ColorGradeConfig.Clamp(BrightGain, 0.25f, 4.0f, "AdaptiveExposure.BrightGain", corrections);
            BrightenSeconds = ColorGradeConfig.Clamp(BrightenSeconds, 0.0f, 60.0f, "AdaptiveExposure.BrightenSeconds", corrections);
            DarkenSeconds = ColorGradeConfig.Clamp(DarkenSeconds, 0.0f, 60.0f, "AdaptiveExposure.DarkenSeconds", corrections);
        }
    }

    public class ColorGradeConfig
    {
        /// <summary>Master toggle. When false no color grading uniforms are uploaded and the pass is a no-op.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Linear multiplier applied before the tonemap. 1.0 is unchanged.</summary>
        public float Exposure { get; set; } = 1.0f;

        /// <summary>Contrast pivoting around mid-grey. 1.0 is unchanged, 0 is flat grey.</summary>
        public float Contrast { get; set; } = 1.0f;

        /// <summary>Saturation. 1.0 is unchanged, 0 is greyscale, &gt;1 oversaturates.</summary>
        public float Saturation { get; set; } = 1.0f;

        /// <summary>White balance. Negative is cooler/bluer, positive is warmer/oranger, 0 is neutral.</summary>
        public float Temperature { get; set; } = 0.0f;

        /// <summary>
        /// Blend between the untouched color and the filmic curve. 0 disables
        /// the tonemap only, leaving the other controls working.
        ///
        /// Defaults to 0 — off — on purpose. The ACES curve expects a linear,
        /// scene-referred input, and whether vanilla's final.fsh output is
        /// still linear at the point this mod grades it has NOT been confirmed
        /// against a running game. If it is already display-referred, a curve
        /// applied on top washes the image out, and a mod that looks broken on
        /// first install is worse than one that waits to be switched on. Flip
        /// this to 1.0 once someone has actually looked at it in game, and
        /// update src/ColorGrade/README.md when they have.
        /// </summary>
        public float TonemapStrength { get; set; } = 0.0f;

        internal void ClampToValidRanges(List<string> corrections)
        {
            Exposure = Clamp(Exposure, 0.1f, 4.0f, "ColorGrade.Exposure", corrections);
            Contrast = Clamp(Contrast, 0.0f, 2.0f, "ColorGrade.Contrast", corrections);
            Saturation = Clamp(Saturation, 0.0f, 2.0f, "ColorGrade.Saturation", corrections);
            Temperature = Clamp(Temperature, -1.0f, 1.0f, "ColorGrade.Temperature", corrections);
            TonemapStrength = Clamp(TonemapStrength, 0.0f, 1.0f, "ColorGrade.TonemapStrength", corrections);
        }

        internal static float Clamp(float value, float min, float max, string name, List<string> corrections)
        {
            // NaN fails every comparison, so test for it explicitly rather than
            // letting it slip through and poison the shader uniform.
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                corrections.Add(name + " was not a finite number, reset to " + min.ToString("0.##"));
                return min;
            }

            if (value < min)
            {
                corrections.Add(name + " clamped from " + value.ToString("0.###") + " to " + min.ToString("0.##"));
                return min;
            }

            if (value > max)
            {
                corrections.Add(name + " clamped from " + value.ToString("0.###") + " to " + max.ToString("0.##"));
                return max;
            }

            return value;
        }
    }
}
