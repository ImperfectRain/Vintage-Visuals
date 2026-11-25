using System;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// v15. Switches the game's own fireflies off while this mod's are on, and
    /// back on the moment they are not, so the two never draw together.
    ///
    /// Vanilla's fireflies are one <see cref="SimpleParticleProperties"/>,
    /// <c>fireflyParticles</c> on <c>Vintagestory.GameContent.ModSystemAmbientParticles</c>
    /// (VSEssentials). Its quantity (MinQuantity, and AddQuantity 8) is set once
    /// in StartClientSide and never again; the spawn tick only moves MinPos and
    /// spawns. So zeroing both quantities stops vanilla's fireflies and nothing
    /// else - the summer air specks and the water particles on the same system
    /// are separate objects - and putting the saved values back restores them
    /// exactly. No Harmony patch: one field read, two float writes.
    ///
    /// Not a stable API (a VSEssentials internal, read by reflection). Every
    /// failure is logged once, loudly, and leaves vanilla's fireflies alone:
    /// then both draw, which is a visual fault, never a crash.
    /// </summary>
    public sealed class VanillaFireflies
    {
        public const string SystemTypeName = "Vintagestory.GameContent.ModSystemAmbientParticles";
        public const string FieldName = "fireflyParticles";

        private readonly ICoreClientAPI _capi;
        private SimpleParticleProperties _props;
        private float _savedMin;
        private float _savedAdd;
        private bool _saved;
        private bool _suppressed;
        private bool _gaveUp;
        private int _attempts;

        public VanillaFireflies(ICoreClientAPI capi)
        {
            _capi = capi;
        }

        /// <summary>Whether vanilla's fireflies are switched off right now.</summary>
        public bool Suppressed { get { return _suppressed; } }

        public void Suppress(bool suppress)
        {
            if (suppress == _suppressed || _gaveUp) return;
            if (!Find()) return;

            try
            {
                if (suppress)
                {
                    if (!_saved)
                    {
                        _savedMin = _props.MinQuantity;
                        _savedAdd = _props.AddQuantity;
                        _saved = true;
                    }
                    _props.MinQuantity = 0f;
                    _props.AddQuantity = 0f;
                    _suppressed = true;
                    _capi.Logger.Notification("[VintageVisuals] pseudopbr: vanilla fireflies switched off while this mod's are on " +
                        "(were " + _savedMin.ToString("0.##") + " + " + _savedAdd.ToString("0.##") + " per spawn).");
                }
                else
                {
                    if (_saved)
                    {
                        _props.MinQuantity = _savedMin;
                        _props.AddQuantity = _savedAdd;
                    }
                    _suppressed = false;
                    _capi.Logger.Notification("[VintageVisuals] pseudopbr: vanilla fireflies restored.");
                }
            }
            catch (Exception e)
            {
                GiveUp("could not change them: " + e.Message);
            }
        }

        private bool Find()
        {
            if (_props != null) return true;
            // The ambient system starts with the client; give it a few ticks
            // before calling its absence a fault.
            if (++_attempts > 50)
            {
                GiveUp("ModSystemAmbientParticles or its fireflyParticles field was not found");
                return false;
            }
            try
            {
                foreach (ModSystem system in _capi.ModLoader.Systems)
                {
                    if (system == null || system.GetType().FullName != SystemTypeName) continue;
                    FieldInfo field = system.GetType().GetField(FieldName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    var props = field?.GetValue(system) as SimpleParticleProperties;
                    // Not yet started: the quantities are only meaningful once
                    // StartClientSide has built the object.
                    if (props == null || props.MinQuantity + props.AddQuantity <= 0f) return false;
                    _props = props;
                    return true;
                }
            }
            catch (Exception e)
            {
                GiveUp("lookup failed: " + e.Message);
            }
            return false;
        }

        private void GiveUp(string why)
        {
            if (_gaveUp) return;
            _gaveUp = true;
            _capi.Logger.Warning("[VintageVisuals] pseudopbr: could not switch off vanilla fireflies (" + why +
                "). Both the game's and this mod's will draw; set Fireflies to 0 to see only the game's.");
        }
    }
}
