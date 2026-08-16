using System.Collections.Generic;

namespace VintageVisuals.Common.Patching
{
    /// <summary>
    /// All patches belonging to one subsystem, treated as a single unit.
    ///
    /// Grouping exists so a broken patch degrades to "this subsystem is off"
    /// rather than "the game renders garbage". Half-applying a group is the
    /// worst outcome available: the GLSL would reference functions the rest of
    /// the group never injected, and the resulting compile error surfaces
    /// somewhere far away from the actual cause.
    /// </summary>
    public sealed class ShaderPatchGroup
    {
        public string Name { get; }

        public List<ShaderPatch> Patches { get; } = new List<ShaderPatch>();

        /// <summary>
        /// Set once a patch in this group fails. The group is then skipped for
        /// every subsequent shader, and subsystems that depend on it must
        /// disable themselves — see <see cref="ShaderPatcher.IsGroupHealthy"/>.
        /// </summary>
        public bool Failed { get; private set; }

        public string FailureReason { get; private set; }

        /// <summary>Shader files this group successfully patched, for the summary log.</summary>
        public List<string> PatchedFiles { get; } = new List<string>();

        public ShaderPatchGroup(string name)
        {
            Name = name;
        }

        public void MarkFailed(string reason)
        {
            Failed = true;
            FailureReason = reason;
        }

        /// <summary>Clears run state, keeping the loaded patches. Called on shader reload.</summary>
        public void ResetRunState()
        {
            Failed = false;
            FailureReason = null;
            PatchedFiles.Clear();
        }
    }
}
