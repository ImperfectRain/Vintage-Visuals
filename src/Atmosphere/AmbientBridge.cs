using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageVisuals.Atmosphere
{
    /// <summary>
    /// Drives Vintage Story's own atmosphere instead of drawing a second one.
    ///
    /// The game blends a stack of named <see cref="AmbientModifier"/>s into the
    /// fog it actually renders. That stack is public, it is writable, and every
    /// shading program in the game already consumes its result - including the
    /// sky, the water and the entities, none of which this mod patches and none
    /// of which any GLSL it could inject would reach.
    ///
    /// So a value this class can express belongs here and NOT in a shader. That
    /// is the whole of the height-haze feature: vanilla already computes
    /// <c>1 - 1/exp((worldY - flatFogStart) * flatFogDensity)</c> in seven
    /// shading programs and takes the max of it and the distance term. Writing
    /// those two numbers gets a height-banded haze that is consistent across
    /// every surface in the frame, for no shader risk at all. Reimplementing it
    /// would get a haze that stops at the edge of whatever this mod patched.
    ///
    /// Everything is expressed so that OFF MEANS VANILLA at the strongest
    /// possible level: with the feature off, the modifier is removed from the
    /// stack entirely rather than left in place with a zero value.
    /// </summary>
    public sealed class AmbientBridge
    {
        /// <summary>
        /// The key this mod's modifier lives under.
        ///
        /// Namespaced because the dictionary is shared with the game and with
        /// every other mod, and a collision would not error - it would silently
        /// replace someone else's weather.
        /// </summary>
        public const string ModifierKey = "vintagevisuals:atmosphere";

        /// <summary>
        /// How far below the camera the haze band's top sits, in blocks, at
        /// full strength.
        ///
        /// Vanilla's flat fog fills the space BELOW <c>flatFogStart</c> when the
        /// density is negative - that sign convention is the game's, not a
        /// trick: the sky shader branches on <c>flatFogDensity &lt; 0</c> to add
        /// an earth-curvature bias, which only makes sense for a layer lying on
        /// the ground.
        ///
        /// Anchored to sea level rather than to the camera. Haze that followed
        /// the camera would keep the player permanently at its surface, so
        /// climbing out of it - the one thing that makes a haze layer read as a
        /// layer - could never happen.
        /// </summary>
        public const float HazeTopAboveSeaLevel = 34f;

        /// <summary>
        /// Density of the haze layer at full strength.
        ///
        /// Negative, per the sign convention above. Small: vanilla's own
        /// distance fog defaults to 0.00125 per block, and this is applied to a
        /// height difference measured in tens of blocks rather than to a
        /// distance measured in hundreds, so it has to be correspondingly
        /// larger to show at all.
        /// </summary>
        public const float HazeDensityAtFullStrength = -0.022f;

        private readonly ICoreClientAPI _capi;
        private readonly ILogger _logger;

        private readonly AmbientModifier _modifier = new AmbientModifier().EnsurePopulated();

        private bool _installed;

        /// <summary>
        /// Whether the reproduction of the game's blend has been checked
        /// against the game's own answer since the last install.
        ///
        /// Checked once rather than every frame: the point is to notice that
        /// the formula moved, and a formula does not move mid-session.
        /// </summary>
        private bool _blendVerified;

        public AmbientBridge(ICoreClientAPI capi, ILogger logger)
        {
            _capi = capi;
            _logger = logger;
        }

        /// <summary>What was last written, so a test and a debug view can see it without a client.</summary>
        public float HazeDensity { get; private set; }

        /// <summary>The world height the haze band is measured from, as last written.</summary>
        public float HazeTop { get; private set; }

        /// <summary>
        /// Sets the haze for this tick.
        ///
        /// <paramref name="strength"/> is already the product of the config and
        /// the grant, so 0 here means the player asked for nothing OR the
        /// budget refused it, and both must look identical.
        /// </summary>
        public void SetHeightHaze(float strength, float seaLevel)
        {
            strength = Clamp01(strength);

            if (strength <= 0f)
            {
                HazeDensity = 0f;
                HazeTop = 0f;
                Remove();
                return;
            }

            HazeDensity = HazeDensityAtFullStrength * strength;
            HazeTop = seaLevel + HazeTopAboveSeaLevel;

            // Weight 1 on the two fields this owns, and weight 0 on every other
            // field of the modifier. A weight of 0 is the documented no-op:
            // blended = 0 * ours + 1 * theirs. So this modifier is invisible to
            // fog colour, cloud density, ambient colour and the rest, which are
            // not this feature's to touch.
            _modifier.FlatFogDensity.Value = HazeDensity;
            _modifier.FlatFogDensity.Weight = 1f;
            _modifier.FlatFogYPos.Value = HazeTop;
            _modifier.FlatFogYPos.Weight = 1f;

            Install();
        }

        /// <summary>
        /// Puts the modifier into the game's stack, and checks once that the
        /// blend this file reasons about is the blend the game performs.
        /// </summary>
        private void Install()
        {
            try
            {
                IAmbientManager ambient = _capi?.Ambient;
                if (ambient?.CurrentModifiers == null) return;

                if (!_installed || !ambient.CurrentModifiers.ContainsKey(ModifierKey))
                {
                    // Re-checked every call, not just on the first. Nothing
                    // documents the lifetime of this dictionary, and a stack
                    // rebuilt on a world change would drop the entry silently -
                    // the feature would simply stop, with nothing in the log.
                    // Re-adding an entry that is already there costs a hash
                    // lookup.
                    ambient.CurrentModifiers[ModifierKey] = _modifier;
                    _installed = true;
                    _blendVerified = false;
                }

                VerifyBlendOnce(ambient);
            }
            catch (Exception e)
            {
                _installed = false;
                _logger?.Warning("[VintageVisuals] atmosphere: could not install the ambient modifier, " +
                    "height haze is off for this session. " + e.Message);
            }
        }

        /// <summary>Takes the modifier back out, so "off" is vanilla's own stack rather than a zeroed entry in it.</summary>
        public void Remove()
        {
            try
            {
                if (!_installed) return;
                _installed = false;
                _blendVerified = false;

                IAmbientManager ambient = _capi?.Ambient;
                if (ambient?.CurrentModifiers == null) return;
                ambient.CurrentModifiers.Remove(ModifierKey);
            }
            catch (Exception)
            {
                // Nothing useful to do. The modifier is inert either way: its
                // weights are only ever set while the feature is on.
            }
        }

        /// <summary>
        /// Reproduces the game's own blend of the modifier stack and compares
        /// it with the game's answer, once.
        ///
        /// This is here because the blend is the one thing this file assumes
        /// and cannot see. <see cref="IAmbientManager"/> documents it as
        /// <c>blended = w * modifier.Value + (1 - w) * blended</c> folded over
        /// the modifiers in order - but a documented formula in an interface
        /// comment is not a tested one, and if it changed, the haze would go
        /// somewhere wrong quietly rather than loudly.
        ///
        /// Compared on fog density rather than on flat fog density, because
        /// that is the field with a non-zero value in a default world and so
        /// the one where a wrong fold shows.
        /// </summary>
        private void VerifyBlendOnce(IAmbientManager ambient)
        {
            if (_blendVerified) return;
            _blendVerified = true;

            try
            {
                float reproduced = BlendFogDensity(ambient);
                float actual = ambient.BlendedFogDensity;

                // Generous: this is checking that the SHAPE of the blend is
                // right, not chasing a float. A fold done differently is out by
                // a lot, not by an epsilon.
                if (Math.Abs(reproduced - actual) > 1e-3f)
                {
                    _logger?.Warning("[VintageVisuals] atmosphere: the ambient blend does not match what " +
                        "IAmbientManager documents. Reproduced " + reproduced.ToString("0.#####") +
                        ", the game says " + actual.ToString("0.#####") +
                        ". Height haze may land at the wrong strength; nothing else reads this.");
                }
            }
            catch (Exception)
            {
                // A failed check is not a reason to fail the feature.
            }
        }

        /// <summary>
        /// The fold, as <see cref="IAmbientManager"/> documents it, over fog
        /// density. Pure and static so tools/smoketest can drive it.
        /// </summary>
        public static float BlendFogDensity(IAmbientManager ambient)
        {
            float blended = ambient.Base == null || ambient.Base.FogDensity == null
                ? 0f
                : ambient.Base.FogDensity.Value;

            foreach (KeyValuePair<string, AmbientModifier> entry in ambient.CurrentModifiers)
            {
                AmbientModifier modifier = entry.Value;
                if (modifier?.FogDensity == null) continue;

                float w = modifier.FogDensity.Weight;
                blended = w * modifier.FogDensity.Value + (1f - w) * blended;
            }

            return blended;
        }

        private static float Clamp01(float v)
        {
            if (float.IsNaN(v)) return 0f;
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }
    }
}
