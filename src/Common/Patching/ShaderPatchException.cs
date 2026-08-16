using System;

namespace VintageVisuals.Common.Patching
{
    /// <summary>
    /// Thrown when a shader patch cannot be applied — the anchor it matches
    /// against is missing, or it matched more times than the patch declared.
    ///
    /// This is expected to happen on game updates, so it is a normal control
    /// flow signal rather than a bug: <see cref="ShaderPatcher"/> catches it,
    /// rolls the whole patch group back and disables that subsystem. It should
    /// never escape into the game's shader loading code.
    /// </summary>
    public class ShaderPatchException : Exception
    {
        /// <summary>Shader file the patch was being applied to, e.g. "final.fsh".</summary>
        public string Filename { get; }

        /// <summary>Patch group (subsystem) the failing patch belongs to.</summary>
        public string Group { get; }

        public ShaderPatchException(string group, string filename, string message)
            : base(message)
        {
            Group = group;
            Filename = filename;
        }
    }
}
