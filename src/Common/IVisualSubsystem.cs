namespace VintageVisuals.Common
{
    /// <summary>
    /// One independently shippable visual feature (ColorGrade, Weather,
    /// Reflections, PseudoPBR).
    ///
    /// The contract is deliberately tiny. Subsystems own their own uniforms and
    /// GLSL; the mod core only tells them when the world changed under them —
    /// config edited, shaders recompiled — and they re-derive their state.
    /// Nothing is cached across <see cref="Apply"/>, so there is no stale state
    /// to reason about when debugging a look that will not update.
    /// </summary>
    public interface IVisualSubsystem
    {
        /// <summary>
        /// Identifier, matching both the shader patch group name and the config
        /// section. Keeping the three in sync by convention is what lets a log
        /// line like "patch group 'colorgrade' FAILED" point straight at the
        /// config section and the source folder.
        /// </summary>
        string Name { get; }

        /// <summary>Called once, after shader patching is available (or known to be unavailable).</summary>
        void Initialize(VintageVisualsModSystem mod);

        /// <summary>
        /// Pushes current config into the render pipeline. Called after shaders
        /// load, after they are reloaded, and whenever the config changes.
        /// Must be safe to call repeatedly and safe to call when the
        /// subsystem's shader patches failed to apply.
        /// </summary>
        void Apply();

        void Dispose();
    }
}
