using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;

namespace VintageVisuals.Common.Patching
{
    /// <summary>
    /// Applies loaded patch groups to shader source as the game loads it.
    ///
    /// Failure policy, which is the whole reason this class is not three lines:
    /// a group is applied to a *copy* of the source and only committed if every
    /// patch in it succeeded. A failure rolls the file back to vanilla, marks
    /// the group dead for the rest of the session, and logs once at error
    /// level. The game keeps rendering; the affected subsystem does not.
    /// </summary>
    public sealed class ShaderPatcher
    {
        private readonly ILogger _logger;
        private readonly List<ShaderPatchGroup> _groups = new List<ShaderPatchGroup>();

        public ShaderPatcher(ILogger logger)
        {
            _logger = logger;
        }

        public IReadOnlyList<ShaderPatchGroup> Groups => _groups;

        /// <summary>Replaces the loaded patch set. Called on (re)load of the YAML assets.</summary>
        /// <summary>
        /// Which files each group has ever patched, surviving both SetPatches
        /// and ResetRunState.
        ///
        /// <see cref="ShaderPatchGroup.PatchedFiles"/> cannot answer "did this
        /// work" because the groups are rebuilt from disk on every shader
        /// reload, and the game recompiles its shaders BEFORE the reload event
        /// reaches us — so the per-run record is wiped moments after it is
        /// filled. The summary read that record and reported "loaded but not
        /// applied to any shader yet" 52 times in one session for groups that
        /// were demonstrably working, which sent this project hunting a patch
        /// failure that never happened.
        /// </summary>
        private readonly Dictionary<string, HashSet<string>> _everApplied =
            new Dictionary<string, HashSet<string>>();

        /// <summary>
        /// Every filename the hook has ever handed this patcher, and how many
        /// patches were applicable to it.
        ///
        /// THIS EXISTS BECAUSE "loaded but not applied" NAMES A SYMPTOM AND NOT
        /// A CAUSE. A group reports it in two completely different situations:
        /// the hook never saw the group's target shader at all, or the hook saw
        /// it under a name no patch matches. Those have opposite fixes, and the
        /// summary could not tell them apart - which cost a full round of
        /// guessing at a session where chunkopaque.fsh, chunktopsoil.fsh,
        /// final.fsh and particlesquad.fsh were all inert while
        /// entityanimated.fsh and particlescube.fsh worked.
        ///
        /// The census answers it directly: here is every name that reached the
        /// patcher, and here are the targets that never did.
        /// </summary>
        private readonly Dictionary<string, int> _seen = new Dictionary<string, int>();

        /// <summary>Filenames the hook saw but whose source was null or empty.</summary>
        private readonly HashSet<string> _emptySource = new HashSet<string>();

        /// <summary>
        /// What happened to one target file on its way from the game's loader
        /// to the GLSL compiler.
        ///
        /// SIX STATES, NOT ONE. A shader can be seen but unpatched, patched but
        /// not written back, written back but not compiled, compiled but not
        /// used, or used with its group disabled. The summary collapsed all of
        /// them into "OK" or "loaded but not applied", which is why two rounds
        /// of investigation asked the wrong question. These fields keep them
        /// apart, and the log prints one line per target - eight lines, not a
        /// shader dump.
        /// </summary>
        private sealed class Delivery
        {
            public string Program;
            public string ShaderType;
            public int OriginalLength;
            public int PatchedLength;
            public bool Assigned;
            public int Applicable;
            public int Applied;
        }

        private readonly Dictionary<string, Delivery> _delivery =
            new Dictionary<string, Delivery>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Called by the source hook once it knows whether it wrote the patched
        /// source back. Everything before the write-back is visible from inside
        /// this class; the write-back itself is not.
        /// </summary>
        public void RecordDelivery(string filename, string program, string shaderType,
                                   int originalLength, int patchedLength, bool assigned)
        {
            if (string.IsNullOrEmpty(filename)) return;

            Delivery d;
            if (!_delivery.TryGetValue(filename, out d))
            {
                d = new Delivery();
                _delivery[filename] = d;
            }

            d.Program = program;
            d.ShaderType = shaderType;
            d.OriginalLength = originalLength;
            d.PatchedLength = patchedLength;
            d.Assigned = assigned;
        }

        public void SetPatches(IEnumerable<ShaderPatch> patches)
        {
            _groups.Clear();

            foreach (ShaderPatch patch in patches)
            {
                ShaderPatchGroup group = _groups.FirstOrDefault(g => g.Name == patch.Group);
                if (group == null)
                {
                    group = new ShaderPatchGroup(patch.Group);
                    _groups.Add(group);
                }

                group.Patches.Add(patch);
            }
        }

        /// <summary>
        /// Whether a subsystem's GLSL actually made it into the shaders.
        ///
        /// Subsystems must consult this before claiming to be active: uploading
        /// uniforms to a shader that never received the matching code is
        /// harmless but misleading, and reporting "enabled" in the log when the
        /// patch silently failed is exactly the debugging dead end this mod is
        /// trying to avoid.
        /// </summary>
        public bool IsGroupHealthy(string groupName)
        {
            ShaderPatchGroup group = _groups.FirstOrDefault(g => g.Name == groupName);
            return group != null && !group.Failed && group.PatchedFiles.Count > 0;
        }

        /// <summary>Clears per-run state on all groups so a shader reload re-evaluates failures.</summary>
        public void ResetRunState()
        {
            foreach (ShaderPatchGroup group in _groups) group.ResetRunState();
        }

        /// <summary>
        /// Applies every healthy group that targets <paramref name="filename"/>.
        /// Never throws — a patch failure is reported through the log and the
        /// group's failed state, because the caller is the game's shader loader.
        /// </summary>
        public string Patch(string filename, string code)
        {
            if (string.IsNullOrEmpty(code))
            {
                if (!string.IsNullOrEmpty(filename)) _emptySource.Add(filename);
                return code;
            }

            if (!string.IsNullOrEmpty(filename) && !_seen.ContainsKey(filename)) _seen[filename] = 0;

            foreach (ShaderPatchGroup group in _groups)
            {
                if (group.Failed) continue;

                List<ShaderPatch> applicable = group.Patches.Where(p => p.AppliesTo(filename)).ToList();
                if (applicable.Count == 0) continue;

                if (!string.IsNullOrEmpty(filename))
                {
                    _seen[filename] = _seen[filename] + applicable.Count;

                    Delivery counted;
                    if (!_delivery.TryGetValue(filename, out counted))
                    {
                        counted = new Delivery();
                        _delivery[filename] = counted;
                    }
                    counted.Applicable += applicable.Count;
                }

                try
                {
                    // Staged in a local so a mid-group failure cannot leave the
                    // shader half-patched.
                    string staged = code;
                    foreach (ShaderPatch patch in applicable) staged = patch.Apply(filename, staged);

                    code = staged;
                    if (!group.PatchedFiles.Contains(filename)) group.PatchedFiles.Add(filename);

                    Delivery applied;
                    if (_delivery.TryGetValue(filename, out applied)) applied.Applied += applicable.Count;

                    HashSet<string> everApplied;
                    if (!_everApplied.TryGetValue(group.Name, out everApplied))
                    {
                        everApplied = new HashSet<string>();
                        _everApplied[group.Name] = everApplied;
                    }

                    everApplied.Add(filename);

                    _logger.VerboseDebug("[VintageVisuals] applied " + applicable.Count +
                                         " patch(es) from group '" + group.Name + "' to " + filename);
                }
                catch (ShaderPatchException ex)
                {
                    group.MarkFailed(ex.Message);
                    _logger.Error("[VintageVisuals] CRITICAL shader patch failure in group '" + group.Name +
                                  "' while patching " + filename + ": " + ex.Message);
                    _logger.Error("[VintageVisuals] group '" + group.Name + "' is now disabled for this session. " +
                                  "The rest of the mod keeps working; " + group.Name + " will have no visual effect.");
                }
                catch (Exception ex)
                {
                    group.MarkFailed(ex.Message);
                    _logger.Error("[VintageVisuals] CRITICAL unexpected error patching " + filename +
                                  " in group '" + group.Name + "'. Group disabled for this session.");
                    _logger.LogException(EnumLogType.Error, ex);
                }
            }

            return code;
        }

        /// <summary>
        /// What the hook actually delivered, and what it never did.
        ///
        /// Logged only when something is missing, because in the healthy case
        /// it is forty lines saying nothing. When a group reports "loaded but
        /// not applied", this is the line that says whether its target reached
        /// the patcher at all - which is the difference between a patch that
        /// does not match and a shader the hook never sees, and those have
        /// nothing in common but the symptom.
        /// </summary>
        public void LogCensus()
        {
            var targets = new HashSet<string>(
                _groups.SelectMany(g => g.Patches).Select(p => p.Filename),
                StringComparer.OrdinalIgnoreCase);

            var missing = targets.Where(t => !_seen.ContainsKey(t)).OrderBy(t => t).ToList();
            if (missing.Count == 0) return;

            if (_seen.Count == 0)
            {
                _logger.Error("[VintageVisuals] shader census: the source hook has never been handed a single " +
                              "shader. Nothing is patched, and no patch is at fault - the interceptor is not " +
                              "reaching the game's shader loader at all.");
                return;
            }

            _logger.Warning("[VintageVisuals] shader census: " + missing.Count + " of " + targets.Count +
                            " patch target(s) NEVER reached the patcher: " + string.Join(", ", missing) +
                            ". Their groups are inert, and no patch of theirs has been tried, let alone failed.");

            _logger.Notification("[VintageVisuals] shader census: the hook did deliver " + _seen.Count +
                                 " file(s): " + string.Join(", ", _seen.Keys.OrderBy(k => k)) +
                                 ". If a missing target appears in that list under another name, the patch " +
                                 "filename is wrong; if it does not appear at all, the hook never saw the program.");

            if (_emptySource.Count > 0)
            {
                _logger.Warning("[VintageVisuals] shader census: " + _emptySource.Count +
                                " file(s) arrived with empty source and were skipped: " +
                                string.Join(", ", _emptySource.OrderBy(k => k)) +
                                ". A target here is loaded by a path that fills its code in later.");
            }
        }

        /// <summary>
        /// One line per TARGET FILE describing how far it got.
        ///
        /// The six states a shader passes through are answered here as far as
        /// this side of the boundary can see them: did the source arrive, did a
        /// group match, did the group apply, did the patched text get written
        /// back. Compilation and whether the program is actually used are the
        /// game's side and are stated as unknown rather than guessed at.
        ///
        /// Always logged, unlike the census, because it is bounded by the
        /// number of patch targets - eight lines today - and because a delivery
        /// question has now cost three rounds of investigation.
        /// </summary>
        public void LogDelivery()
        {
            var targets = _groups.SelectMany(g => g.Patches)
                                 .Select(p => p.Filename)
                                 .Distinct(StringComparer.OrdinalIgnoreCase)
                                 .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                 .ToList();

            if (targets.Count == 0) return;

            _logger.Notification("[VintageVisuals] shader delivery, one line per patch target:");

            foreach (string target in targets)
            {
                Delivery d;
                if (!_delivery.TryGetValue(target, out d) || d.Program == null)
                {
                    _logger.Notification("[VintageVisuals]   " + target +
                                         ": NEVER reached the hook. Nothing was tried; no patch of its group " +
                                         "has failed, because none was attempted.");
                    continue;
                }

                _logger.Notification("[VintageVisuals]   " + target +
                                     ": program=" + d.Program +
                                     " type=" + d.ShaderType +
                                     " source=" + d.OriginalLength + " -> " + d.PatchedLength +
                                     " applicable=" + d.Applicable +
                                     " applied=" + d.Applied +
                                     " writtenBack=" + (d.Assigned ? "yes" : "no"));
            }
        }

        /// <summary>
        /// One-line-per-group summary. Worth calling once after shaders finish
        /// loading: it is the fastest way for a user reporting "the mod does
        /// nothing" to tell us whether the patches landed.
        /// </summary>
        public void LogSummary()
        {
            if (_groups.Count == 0)
            {
                _logger.Warning("[VintageVisuals] no shader patches were loaded — every visual subsystem is inert. " +
                                "Check that assets/vintagevisuals/shaderpatches/ shipped with the mod.");
                return;
            }

            foreach (ShaderPatchGroup group in _groups)
            {
                if (group.Failed)
                {
                    _logger.Error("[VintageVisuals] patch group '" + group.Name + "': FAILED — " + group.FailureReason);
                }
                else
                {
                    HashSet<string> everApplied;
                    _everApplied.TryGetValue(group.Name, out everApplied);

                    if (everApplied == null || everApplied.Count == 0)
                    {
                        // Not an error: the group's target shaders may simply
                        // not have been loaded yet, or at all on this render
                        // path.
                        _logger.Notification("[VintageVisuals] patch group '" + group.Name +
                                             "': loaded but not applied to any shader yet.");
                    }
                    else
                    {
                        _logger.Notification("[VintageVisuals] patch group '" + group.Name + "': OK (" +
                                             string.Join(", ", everApplied) + ")");
                    }
                }
            }
        }
    }
}
