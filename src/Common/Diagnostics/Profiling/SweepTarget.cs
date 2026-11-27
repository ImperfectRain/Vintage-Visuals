using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VintageVisuals.Common.Diagnostics.Profiling
{
    /// <summary>
    /// D99. A setting the profiler can sweep, as the tuning panel describes it.
    /// Common may not depend on Ui (docs/ARCHITECTURE.md: dependencies point
    /// down), so the mod system adapts every VisualSetting into one of these
    /// and hands over get/set delegates; any slider the panel gains is
    /// sweepable the moment it is registered there.
    /// </summary>
    public sealed class SweepTarget
    {
        public string Code;
        public string Path;
        public string DisplayName;
        public string Tab;
        public float Min, Max, Step;
        public bool IsToggle;
        public bool IsChoice;
        public string[] Choices;
        public bool RequiresShaderReload;
        public bool DebugOnly;

        /// <summary>
        /// The values a sweep measures when none are given: a toggle off/on, a
        /// choice every step, a slider 0/25/50/100 % of its range (with the
        /// range's own zero if it has one, since 0 is "off" for most strengths).
        /// </summary>
        public float[] DefaultValues()
        {
            if (IsToggle) return new[] { 0f, 1f };
            if (IsChoice && Choices != null && Choices.Length > 0)
                return Enumerable.Range(0, Choices.Length).Select(i => Min + i * Step).ToArray();
            var list = new List<float>();
            foreach (float f in new[] { 0f, 0.25f, 0.5f, 1f })
            {
                float v = Min + (Max - Min) * f;
                if (Step > 0) v = (float)(Math.Round((v - Min) / Step) * Step + Min);
                if (!list.Any(x => Math.Abs(x - v) < 1e-5f)) list.Add(v);
            }
            return list.ToArray();
        }

        public string Label(float v)
        {
            if (IsToggle) return v >= 0.5f ? "on" : "off";
            if (IsChoice && Choices != null)
            {
                int i = (int)Math.Round((v - Min) / (Step <= 0 ? 1 : Step));
                if (i >= 0 && i < Choices.Length) return Choices[i];
            }
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// What the mod system lends the profiler: the settings it may sweep, the
    /// patch groups it may force, and a way to re-apply the config in memory.
    /// </summary>
    public interface IProfilerHost
    {
        /// <summary>Every shaderpatches/*.yaml group shipped in this build, in file order.</summary>
        IReadOnlyList<string> PatchGroups { get; }

        /// <summary>Whether the player's config (no bench override) compiles this group in.</summary>
        bool GroupEnabledByConfig(string group);

        /// <summary>Whether the last shader load applied any patch of this group.</summary>
        bool GroupApplied(string group);

        /// <summary>Increments on every shader (re)load, so a bench can wait for the one it asked for.</summary>
        int ShaderLoads { get; }

        /// <summary>The mod's patch-gating signature; a change means a shader reload is on its way.</summary>
        string GatingSignature { get; }

        IReadOnlyList<SweepTarget> Targets { get; }
        float Get(string path);
        bool Set(string path, float value);

        /// <summary>Config changed in memory: re-apply, reloading shaders if gating moved. Never saves.</summary>
        void Reapply();

        VintageVisualsConfig Config { get; }
    }
}
