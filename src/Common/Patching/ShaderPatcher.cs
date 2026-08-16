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
            if (string.IsNullOrEmpty(code)) return code;

            foreach (ShaderPatchGroup group in _groups)
            {
                if (group.Failed) continue;

                List<ShaderPatch> applicable = group.Patches.Where(p => p.AppliesTo(filename)).ToList();
                if (applicable.Count == 0) continue;

                try
                {
                    // Staged in a local so a mid-group failure cannot leave the
                    // shader half-patched.
                    string staged = code;
                    foreach (ShaderPatch patch in applicable) staged = patch.Apply(filename, staged);

                    code = staged;
                    if (!group.PatchedFiles.Contains(filename)) group.PatchedFiles.Add(filename);

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
                else if (group.PatchedFiles.Count == 0)
                {
                    // Not an error: the group's target shaders may simply not
                    // have been loaded yet, or at all on this render path.
                    _logger.Notification("[VintageVisuals] patch group '" + group.Name +
                                         "': loaded but not applied to any shader yet.");
                }
                else
                {
                    _logger.Notification("[VintageVisuals] patch group '" + group.Name + "': OK (" +
                                         string.Join(", ", group.PatchedFiles) + ")");
                }
            }
        }
    }
}
