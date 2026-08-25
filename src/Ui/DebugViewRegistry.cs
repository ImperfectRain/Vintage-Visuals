using System.Collections.Generic;
using System.Linq;

namespace VintageVisuals.Ui
{
    /// <summary>One selectable diagnostic, named for what it shows rather than for its number.</summary>
    public sealed class DebugView
    {
        /// <summary>Which config property carries this view's number.</summary>
        public string OwnerPath { get; }

        /// <summary>The number the shader actually tests. An implementation detail the player never types.</summary>
        public int Value { get; }

        public string DisplayName { get; }
        public string Description { get; }

        /// <summary>Heading in the dropdown, e.g. "Canopy audit".</summary>
        public string Group { get; }

        public DebugView(string ownerPath, int value, string displayName, string description, string group)
        {
            OwnerPath = ownerPath;
            Value = value;
            DisplayName = displayName;
            Description = description;
            Group = group;
        }

        /// <summary>What the dropdown row reads, group included so a long list stays navigable.</summary>
        public string Label
        {
            get { return Value == 0 ? DisplayName : Group + ": " + DisplayName; }
        }
    }

    /// <summary>
    /// Friendly names for every diagnostic the shaders implement.
    ///
    /// "DEBUG VIEW 13" IS NOT A USER INTERFACE. It is a number the player has to
    /// look up in a JSON comment, and the only people who can use it are the
    /// people who already know the answer. A player who has stumbled into a
    /// magenta world needs to find "Normal rendering", not to remember that zero
    /// is off.
    ///
    /// THE NUMBERING STAYS PER SHADER. Each shader numbers its views in its own
    /// terms - that is a deliberate decision recorded in CLAUDE.md, taken
    /// because one global list stopped making sense as soon as two subsystems
    /// wanted the same number. This table does not flatten them: every entry
    /// carries the config property that owns it, so the UI translates a chosen
    /// name into (that property, that number) and nothing about how the shaders
    /// read a debug view changes.
    ///
    /// tools/smoketest compares this table against the `mode == N` arms actually
    /// present in the shipped GLSL, in both directions. A view added to a shader
    /// and not named here fails; a name here with no shader behind it fails too.
    /// That is what keeps a friendly label from quietly pointing at nothing.
    /// </summary>
    public static class DebugViewRegistry
    {
        public const string PbrPath = "PseudoPBR.DebugView";
        public const string EntityPath = "PseudoPBR.EntityDebugView";
        public const string AtmospherePath = "Atmosphere.AirDebugView";
        public const string CloudPath = "Weather.CloudDebugView";

        private static readonly List<DebugView> _all = Build();

        public static IReadOnlyList<DebugView> All { get { return _all; } }

        public static IEnumerable<DebugView> For(string ownerPath)
        {
            return _all.Where(v => v.OwnerPath == ownerPath).OrderBy(v => v.Value);
        }

        /// <summary>The view a property is currently set to, or null if the number has no name.</summary>
        public static DebugView Current(string ownerPath, int value)
        {
            return _all.FirstOrDefault(v => v.OwnerPath == ownerPath && v.Value == value);
        }

        static List<DebugView> Build()
        {
            var v = new List<DebugView>();

            void Pbr(int n, string name, string desc, string group)
                => v.Add(new DebugView(PbrPath, n, name, desc, group));

            const string Off = "Normal rendering";

            Pbr(0, Off, "The world as the mod normally draws it.", "Off");

            const string Layers = "Material layers";
            Pbr(1, "Normal map", "The surface normals the material atlas derived, as colour.", Layers);
            Pbr(2, "Roughness", "How polished or worn each texel was measured to be.", Layers);
            Pbr(3, "Specular mask", "Which parts of a texture are allowed to shine.", Layers);
            Pbr(4, "Relief only", "The surface relief on its own, with the texture removed.", Layers);
            Pbr(5, "Highlight only", "The direct specular lobe with nothing else in the frame.", Layers);
            Pbr(6, "World normal", "The final shading normal in world space.", Layers);
            Pbr(7, "Reflectance", "How much light each surface reflects head-on.", Layers);
            Pbr(8, "Roughness, shaded", "Roughness shown as it affects the highlight rather than as a number.", Layers);
            Pbr(9, "Block-light direction", "Which way the nearest firelight is coming from.", Layers);
            Pbr(10, "Wetness", "How wet each surface currently is.", Layers);
            Pbr(11, "Rain ripples", "The ripple pattern on wet surfaces.", Layers);
            Pbr(12, "Crevice occlusion", "Darkening in the grooves, at the scale the atlas can see.", Layers);
            Pbr(13, "Foliage transmission", "Light coming through leaves. Point it at a backlit canopy.", Layers);
            Pbr(14, "Emission", "What each surface is emitting on its own.", Layers);
            Pbr(15, "Dapple mask", "Where the canopy is taking sunlight away.", Layers);
            Pbr(16, "Raw sun exposure", "Vanilla's own sky-exposure value, unprocessed.", Layers);
            Pbr(17, "Shaft mask", "Where sun shafts are being drawn.", Layers);
            Pbr(18, "Specular occlusion", "Where reflections are being kept out of crevices.", Layers);
            Pbr(19, "Metalness", "Which texels the second atlas classified as metal.", Layers);
            Pbr(20, "Height", "The height map behind the surface relief.", Layers);
            Pbr(21, "Baked occlusion", "Ambient occlusion baked into the material atlas.", Layers);
            Pbr(22, "Emission mask", "Which parts of a texture emit, as opposed to how much.", Layers);
            Pbr(23, "Reflectance from metalness", "The reflectance a texel gets purely from being metal.", Layers);
            Pbr(24, "Wood grain", "The grain direction and how confident the measurement is.", Layers);

            const string Canopy = "Canopy audit";
            Pbr(25, "Vanilla sun visibility", "The game's own answer to how much sun reaches this point.", Canopy);
            Pbr(26, "Shadow breakup", "How broken the shadow overhead is, at three radii at once.", Canopy);
            Pbr(27, "Coarse breakup", "The same measurement at the widest radius alone.", Canopy);
            Pbr(28, "Breeze against sun", "How the wind direction sits relative to the sun.", Canopy);
            Pbr(29, "Canopy gate", "The geometric test the dapple system actually uses.", Canopy);
            Pbr(30, "Occluder count", "How many ring samples are shadowed, at three radii.", Canopy);
            Pbr(31, "Occluder count banded", "The same count in false colour, banded for reading.", Canopy);

            const string Pixel = "Pixel reflection";
            Pbr(32, "Reflection direction", "Which way each texel is reflecting.", Pixel);
            Pbr(33, "Material texel grid", "The texture pixel grid reflections are locked to.", Pixel);
            Pbr(34, "Quantised reflection", "The reflection after it has been snapped to that grid.", Pixel);
            Pbr(35, "Reflection contribution", "How much the reflection is adding to the final pixel.", Pixel);
            Pbr(36, "Fallback mask", "Where the analytic sky is standing in for a real reflection.", Pixel);
            Pbr(37, "Reflection coarseness", "How coarse roughness has made the reflection cells.", Pixel);
            Pbr(52, "Material texels per pixel", "Green is resolvable, yellow is near one material texel per screen pixel, red is undersampled.", Pixel);

            const string Bridge = "Scene reflection bridge";
            Pbr(38, "Captured-frame coordinate", "Where on the captured frame each texel is reading.", Bridge);
            Pbr(39, "Validity traffic light", "Green means this pixel really is reflecting the world. Read this one first.", Bridge);
            Pbr(40, "Raw scene sample", "The captured colour before any material response.", Bridge);
            Pbr(41, "The capture itself", "Last frame, shrunk. Should look like the world, not like a texture atlas.", Bridge);
            Pbr(42, "Camera movement", "How far the camera has moved since the capture.", Bridge);
            Pbr(43, "Capture coordinate", "Red is U, green is V. The view that exposes a wrong reprojection.", Bridge);

            const string Flora = "Flora taxonomy";
            Pbr(44, "Flora class", "Which kind of plant each surface is, by colour.", Flora);
            Pbr(45, "Thinness and height", "How thin the tissue is and how far up the plant this is.", Flora);
            Pbr(46, "Takes canopy dapple", "Which plants receive dapple.", Flora);
            Pbr(47, "Optical role", "Which surfaces can receive canopy-filtered sunlight. NOT the same as the previous view.", Flora);

            const string March = "Reflection march";
            Pbr(48, "Why the ray ended", "Green hit, yellow found the surface and rejected it, red never crossed anything.", March);
            Pbr(49, "Stride against budget", "Red means the ray was walked coarser than the stride constant asks.", March);
            Pbr(50, "Crossing residual", "How far behind the surface the refined crossing landed.", March);
            Pbr(51, "Depth precision", "On measured crossings, red means captured depth is too coarse. Black means no crossing was available to measure.", March);

            const string WorldProof = "World reflection proof";
            Pbr(53, "World trace result", "Green hit, red miss, blue outside the local volume, yellow step or range limit.", WorldProof);
            Pbr(54, "World hit color", "Representative diagnostic block colour for the hit cell.", WorldProof);
            Pbr(55, "World hit distance", "Distance in blocks to the world-volume hit.", WorldProof);
            Pbr(56, "World trace steps", "How many DDA cells this fragment traversed.", WorldProof);

            void Entity(int n, string name, string desc)
                => v.Add(new DebugView(EntityPath, n, name, desc, "Entities"));

            Entity(0, Off, "Entities as the mod normally draws them.");
            Entity(1, "Entity highlight only", "The entity specular lobe with the base colour removed.");
            Entity(2, "Entity wetness", "The shared scene wetness value reaching entities.");
            Entity(3, "Scene restraint", "The gameplay-readability restraint reaching entities.");

            void Air(int n, string name, string desc)
                => v.Add(new DebugView(AtmospherePath, n, name, desc, "Atmosphere"));

            Air(0, Off, "The world as the mod normally draws it.");
            Air(1, "Final atmosphere", "Everything the atmosphere is contributing, combined.");
            Air(2, "Raw state", "The atmospheric state as sampled, before any strength is applied.");
            Air(3, "Transmittance", "How much of a distant surface survives the trip through the air.");
            Air(4, "Horizon colour", "The colour the horizon band is being given.");
            Air(5, "Sun scattering", "Light scattering in the air around the sun.");
            Air(6, "Height factor", "How the air thins with altitude.");
            Air(7, "Weather extinction", "How much rain and snow are thickening the air.");
            Air(8, "Cloud contribution", "What cloud cover is doing to the atmosphere.");
            Air(9, "Godrays", "What is being written into vanilla's own godray channel.");
            Air(10, "Precipitation", "What falling rain and snow are scattering.");
            Air(11, "Moon", "Moonlight in the air on a clear night.");
            Air(12, "Dapple", "The dappled-air term. FOUNDATION ONLY.");
            Air(13, "Combined transport", "Extinction and inscatter together, as one transport result.");

            void Cloud(int n, string name, string desc)
                => v.Add(new DebugView(CloudPath, n, name, desc, "Cloud shadows"));

            Cloud(0, Off, "The world as the mod normally draws it.");
            Cloud(1, "Shadow field", "The cloud shadow field alone at full strength, with vanilla's shadows out of the way.");
            Cloud(2, "Tile calibration", "The game's cloud tiles drawn straight down. Look up, then down: the patterns should match.");
            Cloud(3, "Sampling window", "The tile grid the shadow field is sampling, and its edge.");

            return v;
        }
    }
}
