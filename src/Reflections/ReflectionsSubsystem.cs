using System;
using Vintagestory.API.Client;
using VintageVisuals.Common;

namespace VintageVisuals.Reflections
{
    /// <summary>
    /// Owns the scene capture that lets reflective surfaces see the world.
    ///
    /// Deliberately thin. It does not shade anything, has no GLSL of its own and
    /// no shader patch group: the reflection is evaluated inside the material
    /// system, because that is where the texture grid, the normal, the roughness
    /// and the metalness already live and none of them should be duplicated. All
    /// this subsystem does is produce the source image and hand it over.
    ///
    /// OFF BY DEFAULT, and for the same reason PseudoPBR is: it costs a
    /// framebuffer and a full-screen copy every frame whether or not anything
    /// reflective is in view, and it is the newest and least proven thing in the
    /// mod. A player who has not asked for it should not pay for it.
    ///
    /// The capture is torn down whenever the feature is switched off, rather than
    /// left allocated and unused - a framebuffer nothing reads is pure cost, and
    /// leaving it around makes "is this feature running" unanswerable from a log.
    /// </summary>
    public sealed class ReflectionsSubsystem : IVisualSubsystem
    {
        public const string SubsystemName = "reflections";

        private VintageVisualsModSystem _mod;
        private ICoreClientAPI _capi;

        private SceneCaptureRenderer _capture;
        private bool _registered;
        private bool _reported;

        public string Name => SubsystemName;

        /// <summary>The capture, or null when the feature is off or has failed.</summary>
        public SceneCaptureRenderer Capture => _capture;

        public void Initialize(VintageVisualsModSystem mod)
        {
            _mod = mod;
            _capi = mod.Capi;
        }

        public void Apply()
        {
            if (_capi == null) return;

            bool wanted = _mod.ConfigManager.Config.Reflections.SceneReflections
                       && _mod.ConfigManager.Config.PseudoPBR.Enabled
                       && _mod.ConfigManager.Config.PseudoPBR.PixelReflection > 0.001f;

            if (wanted) Start(); else Stop();
        }

        /// <summary>
        /// Brings the capture up, once.
        ///
        /// Registered at AfterPostProcessing: the first stage where the primary
        /// framebuffer holds a composed scene rather than one still being drawn.
        /// Reading it any earlier reads a frame mid-render, which is the mistake
        /// that makes a screen-space effect sample its own output.
        /// </summary>
        private void Start()
        {
            if (_capture != null) return;

            var capture = new SceneCaptureRenderer(_capi, Log);

            if (!capture.TryInitialise())
            {
                capture.Dispose();
                return;
            }

            _capi.Event.RegisterRenderer(capture, EnumRenderStage.AfterPostProcessing,
                                         "vintagevisuals-scenecapture");
            _registered = true;
            _capture = capture;

            if (!_reported)
            {
                _reported = true;
                Log("reflections: scene capture active - reflective surfaces read the previous "
                  + "frame at quarter resolution, with depth packed into its alpha. Rays that "
                  + "leave the screen fall back to the analytic environment.");
            }
        }

        private void Stop()
        {
            if (_capture == null) return;

            if (_registered)
            {
                _capi.Event.UnregisterRenderer(_capture, EnumRenderStage.AfterPostProcessing);
                _registered = false;
            }

            _capture.Dispose();
            _capture = null;
        }

        private void Log(string message)
        {
            _capi?.Logger?.Notification("[VintageVisuals] " + message);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
