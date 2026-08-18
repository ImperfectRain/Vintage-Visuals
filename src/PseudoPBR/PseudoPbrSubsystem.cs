using System;
using VintageVisuals.Common;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// Phase 4 subsystem. Currently classification only — it works out what
    /// material every block is made of and can report it. Nothing here changes
    /// how the game renders yet.
    ///
    /// Landing it in this order is deliberate. Classification is what the atlas
    /// and the lighting model will both be built on, and it is far cheaper to
    /// read a report and find that a third of the world is unclassified than to
    /// discover it through a flat-looking build two stages later.
    /// </summary>
    public sealed class PseudoPbrSubsystem : IVisualSubsystem
    {
        public const string GroupName = "pseudopbr";

        private VintageVisualsModSystem _mod;
        private bool _reportWritten;

        public string Name => GroupName;

        public void Initialize(VintageVisualsModSystem mod)
        {
            _mod = mod;
        }

        public void Apply()
        {
            if (_mod == null || _mod.Capi == null) return;

            PseudoPbrConfig config = _mod.ConfigManager.Config.PseudoPBR;

            // Once per session unless the player asks again by toggling it off
            // and on: the block registry does not change mid-session, so
            // rewriting the file on every config change is pure noise.
            if (!config.WriteMaterialReport)
            {
                _reportWritten = false;
                return;
            }

            if (_reportWritten) return;

            try
            {
                MaterialReport.Write(_mod.Capi, _mod.Mod.Logger);
                _reportWritten = true;
            }
            catch (Exception ex)
            {
                // A diagnostic that cannot write a file must not be the reason
                // a player's game misbehaves.
                _mod.Mod.Logger.Warning("[VintageVisuals] pseudopbr: could not write the material report: " + ex.Message);
                _reportWritten = true;
            }
        }

        public void Dispose()
        {
            _mod = null;
        }
    }
}
