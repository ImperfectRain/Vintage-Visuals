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
        public const int NoForest = 6;
        public const int NoBlockLight = 7;
        public const int NoMaterial = 8;
        public const int NoEntities = 9;

        /// <summary>The run order: all on, each feature off in turn, then all on again to show drift.</summary>
        public static readonly int[] Order =
        {
            Normal, NoShafts, NoCanopy, NoReflections, NoFoliage, NoRain,
            NoForest, NoBlockLight, NoEntities, NoMaterial, Normal
        };

        public static int Mode { get; set; } = Normal;

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
                case NoForest: return "gusts + plant shade + damp off";
                case NoBlockLight: return "torch specular off";
                case NoEntities: return "entity lighting off";
                case NoMaterial: return "whole terrain material off";
                default: return "everything on";
            }
        }
    }
}
