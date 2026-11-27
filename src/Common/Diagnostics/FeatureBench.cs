namespace VintageVisuals.Common.Diagnostics
{
    /// <summary>
    /// The feature a running <c>.vvbench features</c> holds switched off, so one
    /// bench can say what each terrain and entity feature costs on the same
    /// scene. Runtime only, never saved; <see cref="Normal"/> outside a bench.
    ///
    /// It only ever turns a feature OFF, by zeroing the strength uniform the
    /// shader already early-outs on, so a run measures exactly the price of
    /// that feature's work and nothing the player turned off comes on.
    /// The binder applies it after its own uploads (PbrShaderBinder).
    /// </summary>
    public static class FeatureBench
    {
        public const int Normal = 0;
        public const int NoShafts = 1;
        public const int NoCanopy = 2;
        public const int NoReflections = 3;
        public const int NoFoliage = 4;
        public const int NoRain = 5;
        public const int NoGusts = 6;
        public const int NoPlantShade = 10;
        public const int NoDamp = 11;
        public const int NoBlockLight = 7;
        public const int NoMaterial = 8;
        public const int NoEntities = 9;
        public const int OldBeams = 12;
        public const int FastBeams = 13;
        public const int NoBeams = 14;
        public const int NoCaustics = 15;

        /// <summary>The run order: all on, each feature off in turn, then all on again to show drift.</summary>
        public static readonly int[] Order =
        {
            Normal, NoShafts, NoCanopy, NoReflections, NoFoliage, NoRain,
            NoGusts, NoPlantShade, NoDamp, NoBlockLight, NoEntities, NoMaterial, NoCaustics, Normal
        };

        /// <summary>
        /// '.vvbench beams' (D87): the in-shader beams, the fast pass, no air
        /// beams (god-ray shafts kept, so each beam path's own cost is the
        /// difference to this run), no shafts at all, then the in-shader beams
        /// again for drift. The first
        /// two choose WHERE the beams are computed, not whether they are on:
        /// the strength is still the player's SunShafts, and at zero both are off.
        /// </summary>
        public static readonly int[] BeamOrder = { OldBeams, FastBeams, NoBeams, NoShafts, OldBeams };

        public static int Mode { get; set; } = Normal;

        /// <summary>Whether the fast beam pass draws, given the player's setting and any bench override.</summary>
        public static bool UseFastBeams(bool configured)
        {
            if (Mode == OldBeams || Mode == NoBeams) return false;
            if (Mode == FastBeams) return true;
            return configured;
        }

        public static bool Off(int feature) { return Mode == feature; }

        public static string Name(int mode)
        {
            switch (mode)
            {
                case NoShafts: return "sun shafts + air beams off";
                case NoCanopy: return "sunflecks + canopy shade off";
                case NoReflections: return "terrain reflections off";
                case NoFoliage: return "foliage light off";
                case NoRain: return "rain ripples + crevice water off";
                case NoGusts: return "wind gusts off";
                case NoPlantShade: return "plant foot shade off";
                case NoDamp: return "damp + moss off";
                case NoBlockLight: return "torch specular off";
                case NoEntities: return "entity lighting off";
                case NoMaterial: return "whole terrain material off";
                case OldBeams: return "beams in the terrain shader";
                case FastBeams: return "beams in the fast pass (D87)";
                case NoBeams: return "air beams off (shafts kept)";
                case NoCaustics: return "water caustics off (code kept)";
                default: return "everything on";
            }
        }
    }
}
