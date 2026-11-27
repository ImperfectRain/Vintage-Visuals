using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace VintageVisuals.Common.Diagnostics.Profiling
{
    /// <summary>
    /// D99. The hook every feature declares itself through, so the profiler
    /// measures it without anyone editing the profiler.
    ///
    /// Implement this on a small class with a parameterless constructor (or on
    /// the subsystem itself, if it has one) and the registry finds it by
    /// reflection: there is no central list to forget. Declarations are pure
    /// data - no game API, no GL - so <c>tools/smoketest</c> builds the whole
    /// catalogue offline and fails when a patch group, a panel setting or a
    /// per-frame renderer belongs to nobody.
    /// </summary>
    public interface IProfileDeclarer
    {
        void DeclareProfile(ProfileRegistry registry);
    }

    /// <summary>A setting a system wants swept, with the values worth measuring.</summary>
    public sealed class SweepSpec
    {
        /// <summary>The tuning panel code (VisualSetting.Code), which is also the ConfigLib code.</summary>
        public string SettingCode;

        /// <summary>Values to step through; null means "derive from the setting's range".</summary>
        public float[] Values;
    }

    /// <summary>One measurable system: what it compiles, what tunes it, what runs it on the CPU and GPU.</summary>
    public sealed class ProfiledSystem
    {
        public string Id;
        public string DisplayName;

        /// <summary>Patch groups (shaderpatches/*.yaml file names) this system owns.</summary>
        public readonly List<string> PatchGroups = new List<string>();

        /// <summary>
        /// Groups that must be compiled in for this system's own groups to
        /// apply (their anchors live in the other group's snippet). A compiled-
        /// cost bench holds them in for both halves, so the difference is this
        /// system's alone.
        /// </summary>
        public readonly List<string> RequiresGroups = new List<string>();

        /// <summary>Config path prefixes ("Atmosphere.") whose panel settings belong to this system.</summary>
        public readonly List<string> SettingPrefixes = new List<string>();

        /// <summary>Exact config paths owned here, for systems sharing a config section.</summary>
        public readonly List<string> SettingPaths = new List<string>();

        /// <summary>Settings worth a cost curve, in the order a suite runs them.</summary>
        public readonly List<SweepSpec> Sweeps = new List<SweepSpec>();

        /// <summary>Names of the per-frame renderers (ModRenderers.Register name) whose CPU time is this system's.</summary>
        public readonly List<string> Renderers = new List<string>();

        /// <summary>CpuSpans scope names (code inside hooks, not a renderer) that are this system's.</summary>
        public readonly List<string> CpuScopes = new List<string>();

        /// <summary>FrameProfiler segment keys ("vv.mirrors") that time this system on the GPU.</summary>
        public readonly List<string> GpuSpans = new List<string>();

        /// <summary>Saved scene names (VISUAL-TESTS letters) where this system shows its cost.</summary>
        public readonly List<string> Scenes = new List<string>();

        /// <summary>Free-text caveats printed with its results ("needs a world with a mirror").</summary>
        public string Notes;

        /// <summary>Whether the player's config currently has it on. Null = always.</summary>
        public Func<VintageVisualsConfig, bool> Active;

        public bool IsActive(VintageVisualsConfig config)
        {
            try { return Active == null || config == null || Active(config); }
            catch { return false; }
        }

        public bool OwnsSettingPath(string path)
        {
            if (path == null) return false;
            if (SettingPaths.Contains(path)) return true;
            foreach (string p in SettingPrefixes)
            {
                if (path.StartsWith(p, StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }

    /// <summary>Fluent declaration of one <see cref="ProfiledSystem"/>.</summary>
    public sealed class ProfileBuilder
    {
        private readonly ProfiledSystem _s;
        internal ProfileBuilder(ProfiledSystem s) { _s = s; }

        public ProfiledSystem System { get { return _s; } }

        public ProfileBuilder Groups(params string[] groups) { _s.PatchGroups.AddRange(groups); return this; }
        public ProfileBuilder Requires(params string[] groups) { _s.RequiresGroups.AddRange(groups); return this; }
        public ProfileBuilder SettingsUnder(params string[] prefixes) { _s.SettingPrefixes.AddRange(prefixes); return this; }
        public ProfileBuilder Settings(params string[] paths) { _s.SettingPaths.AddRange(paths); return this; }

        /// <summary>A slider to sweep; values null = the setting's own range at 0, 25, 50, 100 %.</summary>
        public ProfileBuilder Sweep(string settingCode, params float[] values)
        {
            _s.Sweeps.Add(new SweepSpec { SettingCode = settingCode, Values = values != null && values.Length > 0 ? values : null });
            return this;
        }

        public ProfileBuilder Renderers(params string[] names) { _s.Renderers.AddRange(names); return this; }
        public ProfileBuilder CpuScopes(params string[] names) { _s.CpuScopes.AddRange(names); return this; }
        public ProfileBuilder GpuSpans(params string[] keys) { _s.GpuSpans.AddRange(keys); return this; }
        public ProfileBuilder Scenes(params string[] names) { _s.Scenes.AddRange(names); return this; }
        public ProfileBuilder Note(string text) { _s.Notes = text; return this; }
        public ProfileBuilder ActiveWhen(Func<VintageVisualsConfig, bool> active) { _s.Active = active; return this; }
    }

    /// <summary>
    /// D99. Every measurable system of the mod, collected from the
    /// <see cref="IProfileDeclarer"/>s in this assembly. The benches read this
    /// and nothing else to decide what to measure, so a feature that declares
    /// itself is in every group bench, sweep, suite and coverage report from
    /// its first build.
    ///
    /// Anything NOT declared is still measured - a patch group nobody claims is
    /// benched under its own name, any panel slider can be swept, every renderer
    /// registered through <see cref="ModRenderers"/> is timed - and the coverage
    /// report names it as unclaimed. Declaring is what attaches a name, its
    /// prerequisites and its scenes; forgetting costs labels, never coverage.
    /// </summary>
    public sealed class ProfileRegistry
    {
        private readonly List<ProfiledSystem> _systems = new List<ProfiledSystem>();
        private readonly List<string> _errors = new List<string>();

        public IReadOnlyList<ProfiledSystem> Systems { get { return _systems; } }

        /// <summary>Declaration problems: duplicate ids, a group claimed twice. Smoke-checked to be empty.</summary>
        public IReadOnlyList<string> Errors { get { return _errors; } }

        /// <summary>The declarer types found, for the coverage report and the smoketest.</summary>
        public readonly List<Type> DeclarerTypes = new List<Type>();

        public ProfileBuilder Declare(string id, string displayName)
        {
            if (_systems.Any(s => s.Id == id)) _errors.Add("profile id '" + id + "' declared twice");
            var s2 = new ProfiledSystem { Id = id, DisplayName = displayName };
            _systems.Add(s2);
            return new ProfileBuilder(s2);
        }

        private static ProfileRegistry _shared;

        /// <summary>The catalogue built from this assembly, once.</summary>
        public static ProfileRegistry Shared
        {
            get
            {
                if (_shared == null) _shared = Build(typeof(ProfileRegistry).Assembly);
                return _shared;
            }
        }

        /// <summary>Finds every IProfileDeclarer with a parameterless constructor and runs it.</summary>
        public static ProfileRegistry Build(Assembly assembly)
        {
            var registry = new ProfileRegistry();
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }

            foreach (Type t in types.OrderBy(t => t.FullName, StringComparer.Ordinal))
            {
                if (t.IsAbstract || t.IsInterface || !typeof(IProfileDeclarer).IsAssignableFrom(t)) continue;
                ConstructorInfo ctor = t.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                                                        null, Type.EmptyTypes, null);
                if (ctor == null)
                {
                    registry._errors.Add(t.FullName + " implements IProfileDeclarer but has no parameterless constructor");
                    continue;
                }
                registry.DeclarerTypes.Add(t);
                try
                {
                    ((IProfileDeclarer)ctor.Invoke(null)).DeclareProfile(registry);
                }
                catch (Exception ex)
                {
                    registry._errors.Add(t.FullName + ".DeclareProfile threw: " + ex.Message);
                }
            }

            registry.Validate();
            return registry;
        }

        private void Validate()
        {
            var groupOwner = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ProfiledSystem s in _systems)
            {
                foreach (string g in s.PatchGroups)
                {
                    if (groupOwner.TryGetValue(g, out string other))
                        _errors.Add("patch group '" + g + "' claimed by both '" + other + "' and '" + s.Id + "'");
                    else groupOwner[g] = s.Id;
                }
            }
            var rendererOwner = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (ProfiledSystem s in _systems)
            {
                foreach (string r in s.Renderers.Concat(s.CpuScopes))
                {
                    if (rendererOwner.TryGetValue(r, out string other))
                        _errors.Add("CPU span '" + r + "' claimed by both '" + other + "' and '" + s.Id + "'");
                    else rendererOwner[r] = s.Id;
                }
            }
        }

        public ProfiledSystem ById(string id)
        {
            return _systems.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        public ProfiledSystem ForGroup(string group)
        {
            return _systems.FirstOrDefault(s => s.PatchGroups.Contains(group));
        }

        public ProfiledSystem ForSettingPath(string path)
        {
            // Exact path claims beat prefixes, so a system can carve one setting
            // out of another's section (sunflecks inside PseudoPBR.).
            ProfiledSystem exact = _systems.FirstOrDefault(s => s.SettingPaths.Contains(path));
            if (exact != null) return exact;
            ProfiledSystem best = null;
            int bestLength = -1;
            foreach (ProfiledSystem s in _systems)
            {
                foreach (string p in s.SettingPrefixes)
                {
                    if (path != null && path.StartsWith(p, StringComparison.Ordinal) && p.Length > bestLength)
                    {
                        best = s;
                        bestLength = p.Length;
                    }
                }
            }
            return best;
        }

        public ProfiledSystem ForCpuSpan(string name)
        {
            return _systems.FirstOrDefault(s => s.Renderers.Contains(name) || s.CpuScopes.Contains(name));
        }

        public ProfiledSystem ForGpuSpan(string key)
        {
            return _systems.FirstOrDefault(s => s.GpuSpans.Contains(key));
        }

        /// <summary>
        /// Groups that need <paramref name="group"/> (directly or not), so a
        /// bench compiling it out must compile them out too: their anchors live
        /// in its snippet and they would fail to apply without it.
        /// </summary>
        public List<string> DependentsOf(string group)
        {
            var result = new List<string>();
            bool grew = true;
            while (grew)
            {
                grew = false;
                foreach (ProfiledSystem s in _systems)
                {
                    foreach (string g in s.PatchGroups)
                    {
                        if (g == group || result.Contains(g)) continue;
                        List<string> pre = PrerequisitesOf(g);
                        if (pre.Contains(group) || pre.Any(result.Contains)) { result.Add(g); grew = true; }
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// The groups a compiled-cost bench must hold in alongside
        /// <paramref name="group"/>, transitively, excluding the group itself.
        /// </summary>
        public List<string> PrerequisitesOf(string group)
        {
            var result = new List<string>();
            var queue = new Queue<string>();
            queue.Enqueue(group);
            while (queue.Count > 0)
            {
                ProfiledSystem owner = ForGroup(queue.Dequeue());
                if (owner == null) continue;
                foreach (string r in owner.RequiresGroups)
                {
                    if (r == group || result.Contains(r)) continue;
                    result.Add(r);
                    queue.Enqueue(r);
                }
            }
            return result;
        }
    }
}
