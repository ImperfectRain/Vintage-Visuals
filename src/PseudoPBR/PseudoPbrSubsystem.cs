using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using VintageVisuals.Common;
using VintageVisuals.Common.Scene;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// Phase 4 subsystem: works out what material every block is made of,
    /// derives a material atlas from the block textures, and feeds that atlas
    /// to the chunk shader so surfaces catch light according to what they are.
    ///
    /// Landing it in stages was deliberate, and each stage produced something
    /// inspectable on its own: the material report caught a mis-tuned Ceramic
    /// profile covering 14% of the world before any pixel was drawn, and the
    /// preview images answered "do the grooves read" before a shader existed
    /// to render them.
    ///
    /// What reaches the screen today is surface relief only — the derived
    /// normals replace the flat per-face block normal in vanilla's own
    /// lighting call. Roughness and specular are in the atlas and are not yet
    /// read by any shader; they need a light direction chunkopaque.fsh does
    /// not currently hand us.
    /// </summary>
    public sealed class PseudoPbrSubsystem : IVisualSubsystem
    {
        public const string GroupName = "pseudopbr";

        /// <summary>
        /// Grass and soil tops are drawn by their own program, so they get their
        /// own patch group. Groups succeed or fail as a unit, and one shared
        /// group would mean a reworded chunktopsoil line switching relief off
        /// for every wall, floor and log in the world too.
        /// </summary>
        public const string EntityGroupName = "pbrentity";

        public const string ParticleGroupName = "pbrparticle";

        public const string TopsoilGroupName = "pseudopbrtopsoil";

        /// <summary>Where the cache and preview images live, under VintagestoryData.</summary>
        public const string DataDirectory = "VintageVisuals";

        private VintageVisualsModSystem _mod;
        private bool _reportWritten;
        private bool _atlasBuilt;

        private MaterialAtlasSet _atlasTexture;
        private TerrainTextureBindInterceptor _bindInterceptor;
        private PbrShaderBinder _binder;

        /// <summary>
        /// False when the block atlas needs more pages than this build can
        /// serve. See <see cref="CheckAtlasPagesSupported"/>.
        /// </summary>
        private bool _atlasPagesSupported;

        /// <summary>How many pages the block atlas actually has, as seen when the atlas was derived.</summary>
        private int _atlasPages = 1;

        /// <summary>Last reported reason the subsystem is inactive, so it is logged on change rather than per call.</summary>
        private string _inactiveReason;

        public string Name => GroupName;

        public void Initialize(VintageVisualsModSystem mod)
        {
            _mod = mod;

            _atlasTexture = new MaterialAtlasSet();

            // Installed up front, before anything knows how many pages the
            // block atlas will need. Its success is what decides whether a
            // multi-page atlas is supported at all, and that answer has to
            // exist by the time the atlas is derived.
            _bindInterceptor = new TerrainTextureBindInterceptor(mod.HarmonyId, mod.Mod.Logger);
            _bindInterceptor.Install();

            _binder = new PbrShaderBinder(mod.Capi, _atlasTexture, BuildPageMap, ReadScene);

            // Registered up front and left registered. It is a no-op until the
            // atlas is uploaded, and registering it here rather than at upload
            // time keeps renderer lifetime tied to subsystem lifetime, which is
            // the only pairing Dispose() can honour.
            mod.Capi.Event.RegisterRenderer(_binder, EnumRenderStage.Opaque, "vintagevisuals-pbr");
        }

        public void Apply()
        {
            if (_mod == null || _mod.Capi == null) return;

            PseudoPbrConfig config = _mod.ConfigManager.Config.PseudoPBR;

            WriteReportOnce(config);
            BuildAtlasOnce(config);
            UpdateShaderState(config);
        }

        /// <summary>
        /// Pushes the current config to the renderer. Cheap and idempotent, so
        /// it runs on every Apply rather than trying to detect changes.
        ///
        /// Three things must all hold before the shader is allowed to do
        /// anything, and each of them fails to "vanilla normals" rather than to
        /// something broken.
        /// </summary>
        private void UpdateShaderState(PseudoPbrConfig config)
        {
            if (_binder == null) return;

            bool active = config.Enabled && _atlasTexture != null && _atlasTexture.HasPixels && _atlasPagesSupported;

            // Says why, once per reason, when the answer is no. "Enabled is
            // ticked but nothing happens" has been the shape of every problem
            // this subsystem has had, and each time the log was silent about
            // which precondition failed.
            if (!active && config.Enabled)
            {
                string reason = _atlasTexture == null || !_atlasTexture.HasPixels
                    ? "no material atlas has been derived (see the pseudopbr lines above)"
                    : "the block texture atlas needs " + _atlasPages + " pages and the terrain bind hook " +
                      "could not be installed, so only a single-page atlas can be served";

                if (_inactiveReason != reason)
                {
                    _inactiveReason = reason;
                    _mod.Mod.Logger.Warning("[VintageVisuals] pseudopbr: enabled in config but inactive — " + reason + ".");
                }
            }
            else
            {
                _inactiveReason = null;
            }

            _binder.SetState(active, config);
        }

        /// <summary>
        /// What the world is doing, in the terms this shader thinks in.
        ///
        /// Read from the shared environment state rather than from the weather
        /// subsystem. Rain changes how surfaces respond to light and that
        /// response is modelled here, but whether it is raining is not this
        /// subsystem's business to decide - and asking the world rather than
        /// another subsystem means the material system keeps working whether or
        /// not that subsystem is loaded.
        ///
        /// The config scaling happens HERE and not in the shared state, because
        /// a strength slider is a statement about what the player wants rather
        /// than about the world.
        /// </summary>
        private SceneInputs ReadScene()
        {
            if (_mod.Environment == null) return SceneInputs.None;

            EnvironmentState world = _mod.Environment.Current;
            SceneIntent intent = _mod.Environment.Intent;
            WeatherConfig weather = _mod.ConfigManager.Config.Weather;

            if (!weather.Enabled)
            {
                return new SceneInputs(world.DayLight, 0f, SceneInputs.None.RainCover, 0f, 0f, 0f,
                                       world.CameraPosition,
                                       intent[IntentChannel.Enclosure],
                                       intent[IntentChannel.ArtificialLight],
                                       intent[IntentChannel.Restraint],
                                       intent[IntentChannel.Readability],
                                       world.Autumn, world.Winter, 0f, 0f);
            }

            return new SceneInputs(
                world.DayLight,
                world.Wetness * weather.WetnessStrength,
                weather.RainCoverThreshold,

                // Driven by the rain FALLING, not by the wetness left behind.
                // Ripples stop the moment the shower does; the ground stays wet
                // for another minute, which is the half that should linger.
                world.Rain * weather.RippleStrength,
                _mod.Environment.RippleClock,

                // Arbitrated: the overcast term is a SECONDARY claim on scene
                // light, and it was removing light on the same overcast day the
                // cloud shadows and the grade were.
                world.CloudCover * weather.OvercastStrength * _mod.Environment.Grants.Overcast,
                world.CameraPosition,
                intent[IntentChannel.Enclosure],
                intent[IntentChannel.ArtificialLight],
                intent[IntentChannel.Restraint],
                intent[IntentChannel.Readability],
                world.Autumn,
                world.Winter,
                weather.FrostStrength,

                // Snow dusting rides the falling snow rather than the season.
                // A dry cold snap in winter should not frost the world over -
                // vanilla's frostAlpha covers that case, and it covers it per
                // block, knowing which of them can frost at all.
                world.Snow * weather.SnowDusting);
        }

        private void WriteReportOnce(PseudoPbrConfig config)
        {
            if (!config.WriteMaterialReport) { _reportWritten = false; return; }
            if (_reportWritten) return;

            try
            {
                MaterialReport.Write(_mod.Capi, _mod.Mod.Logger);
            }
            catch (Exception ex)
            {
                _mod.Mod.Logger.Warning("[VintageVisuals] pseudopbr: could not write the material report: " + ex.Message);
            }

            _reportWritten = true;
        }

        private void BuildAtlasOnce(PseudoPbrConfig config)
        {
            if (!config.BuildMaterialAtlas)
            {
                // Say so rather than returning quietly. A feature that does
                // nothing AND logs nothing is indistinguishable from a feature
                // that is broken, and that ambiguity has already cost this
                // project a debugging round once.
                if (_atlasBuilt)
                {
                    _mod.Mod.Logger.Notification("[VintageVisuals] pseudopbr: material atlas disabled by config " +
                                                 "(PseudoPBR.BuildMaterialAtlas is false).");
                }
                _atlasBuilt = false;
                return;
            }

            if (_atlasBuilt) return;

            var diagnostics = new PbrDiagnostics(_mod.Mod.Logger);
            string directory = Path.Combine(GamePaths.DataPath, DataDirectory);

            // Noted before the work, so the transcript shows the attempt even if
            // the build hangs or the process dies partway through.
            diagnostics.Note("building material atlas…");

            try
            {
                // Latching on the OUTCOME, not on the attempt. An earlier
                // version set this before the work with the reasoning that a
                // throw should not be retried — which is right, and which it
                // also applied to the transient early returns below it. If the
                // very first Apply() ran before the block atlas had a size, the
                // build gave up permanently and the subsystem spent the rest of
                // the session enabled, silent and doing nothing. Apply() runs
                // several times over the first seconds and again on every config
                // change, so a genuinely transient failure now gets those tries.
                _atlasBuilt = BuildAtlas(config, diagnostics, directory);

                if (!_atlasBuilt)
                {
                    diagnostics.Note("material atlas not built this time; will try again on the next config " +
                                     "change or startup retry.");
                }
            }
            catch (Exception ex)
            {
                // A throw is not transient. Latch so it is not retried every
                // time a slider moves.
                _atlasBuilt = true;
                diagnostics.Error("material atlas build failed. Surface relief stays off and the world " +
                                  "renders as vanilla; nothing else in the mod is affected.", ex);
            }

            diagnostics.WriteTo(directory);
        }

        /// <summary>Returns whether the atlas is now derived and ready to upload.</summary>
        private bool BuildAtlas(PseudoPbrConfig config, PbrDiagnostics diagnostics, string directory)
        {
            int width = _mod.Capi.BlockTextureAtlas.Size.Width;
            int height = _mod.Capi.BlockTextureAtlas.Size.Height;

            if (width <= 0 || height <= 0)
            {
                diagnostics.Warn("block texture atlas reports a zero size — too early to derive anything from it.");
                return false;
            }

            _atlasPagesSupported = CheckAtlasPagesSupported(diagnostics);

            bool builtAny = false;

            for (int page = 0; page < _atlasPages; page++)
            {
                if (!BuildAtlasPage(page, config, diagnostics, directory, width, height)) return false;
                builtAny = true;
            }

            return builtAny;
        }

        /// <summary>Derives, caches and previews one page. Returns false if it could not be built yet.</summary>
        private bool BuildAtlasPage(int page, PseudoPbrConfig config, PbrDiagnostics diagnostics,
                                    string directory, int width, int height)
        {
            int skipped;
            List<AtlasRegion> regions = MaterialAtlasSource.Collect(_mod.Capi, page, diagnostics, out skipped);

            if (regions.Count == 0)
            {
                // An empty page is not necessarily a failure: the game can
                // allocate a page it has barely filled. Only page 0 being empty
                // means we were called too early to derive anything.
                if (page == 0)
                {
                    diagnostics.Warn("no block textures were collected for page 0; the material atlas would be empty.");
                    return false;
                }

                diagnostics.Note("atlas page " + page + " has no block textures; filling it with neutral material.");
                _atlasTexture.SetPending(page, width, height, MaterialAtlasBuilder.Build(width, height,
                    new List<AtlasRegion>(), out skipped, out skipped));
                return true;
            }

            string cachePath = Path.Combine(directory, "material-atlas-" + page + ".bin");

            var stopwatch = Stopwatch.StartNew();
            ulong fingerprint = MaterialAtlasBuilder.Fingerprint(width, height, regions,
                                                                MaterialAtlasCache.FormatVersion);

            MaterialAtlasCache.CachedAtlas cached = MaterialAtlasCache.TryLoad(cachePath, fingerprint);

            int[] pixels;
            if (cached != null && cached.Width == width && cached.Height == height)
            {
                pixels = cached.Pixels;
                diagnostics.Note("reused cached material atlas page " + page + " (" + width + "x" + height +
                                 ") in " + stopwatch.ElapsedMilliseconds + "ms.");
            }
            else
            {
                int written, outOfBounds;
                pixels = MaterialAtlasBuilder.Build(width, height, regions, out written, out outOfBounds);

                diagnostics.Note("built material atlas page " + page + " (" + width + "x" + height + ") from " +
                                 written + " texture(s) in " + stopwatch.ElapsedMilliseconds + "ms" +
                                 (outOfBounds > 0 ? ", " + outOfBounds + " outside the atlas bounds" : "") + ".");

                try
                {
                    MaterialAtlasCache.Save(cachePath, width, height, fingerprint, pixels);
                }
                catch (Exception ex)
                {
                    // Losing the cache costs startup time on the next launch,
                    // nothing else.
                    diagnostics.Warn("could not cache atlas page " + page + ": " + ex.Message);
                }
            }

            // Handed over, not uploaded. The GL upload happens on the render
            // thread inside PbrShaderBinder - creating a texture binds it to
            // the active unit as a side effect, and doing that here, during an
            // asset-load event, means clobbering whatever the game had bound.
            _atlasTexture.SetPending(page, width, height, pixels);

            if (config.WriteAtlasPreview)
            {
                try
                {
                    // Page 0 keeps the original filenames so the images written
                    // by earlier versions, and referred to in the docs, still
                    // line up.
                    string suffix = page == 0 ? "" : "-page" + page;
                    string[] previews = AtlasPreview.WriteAll(directory, width, height, pixels, suffix);
                    diagnostics.Note("preview images written: " + string.Join(", ",
                        Array.ConvertAll(previews, Path.GetFileName)));
                }
                catch (Exception ex)
                {
                    diagnostics.Warn("could not write preview images for page " + page + ": " + ex.Message);
                }
            }

            return true;
        }

        /// <summary>
        /// Whether this build can serve however many pages the block atlas has.
        ///
        /// The chunk shader samples our atlas with the same `uv` it uses for
        /// the diffuse, which is exactly right and works only because both
        /// atlases share a layout. When the game needs more than one page it
        /// binds a different terrain texture per draw call, and `uv` alone no
        /// longer says which page a fragment came from — there is nowhere left
        /// in the vertex format to tell us, since VertexFlags is fully packed.
        ///
        /// <see cref="TerrainTextureBindInterceptor"/> answers that by hooking
        /// the one moment the answer exists: when vanilla selects a page. With
        /// the hook installed any number of pages works. Without it, more than
        /// one page means rendering vanilla — sampling page 0 for every page
        /// would paint each block with some other block's surface, which is
        /// worse than no effect.
        /// </summary>
        private bool CheckAtlasPagesSupported(PbrDiagnostics diagnostics)
        {
            _atlasPages = _mod.Capi.BlockTextureAtlas.AtlasTextures == null
                ? 1
                : Math.Max(1, _mod.Capi.BlockTextureAtlas.AtlasTextures.Count);

            if (_atlasPages <= 1) return true;

            if (_bindInterceptor != null && _bindInterceptor.Installed)
            {
                diagnostics.Note("the block texture atlas needs " + _atlasPages + " pages; a material atlas is " +
                                 "derived for each and the bind hook selects the right one per draw call.");
                return true;
            }

            diagnostics.Warn("the block texture atlas needs " + _atlasPages + " pages but the terrain bind hook " +
                             "is not installed, so surface relief stays off and the world renders exactly as " +
                             "vanilla. The material report and the derived atlases are still written.");
            return false;
        }

        /// <summary>
        /// Maps each block atlas page's GL texture id to our derived page's.
        ///
        /// Rebuilt from the game's own list rather than remembered, because the
        /// game recreates these textures on reload and their ids change with
        /// them. A stale entry would bind one page's material data over
        /// another's — silent, and wrong in a way no log line would catch.
        /// </summary>
        private Dictionary<int, int> BuildPageMap()
        {
            var map = new Dictionary<int, int>();

            if (_mod?.Capi == null || _atlasTexture == null) return map;

            List<LoadedTexture> atlases = _mod.Capi.BlockTextureAtlas.AtlasTextures;
            if (atlases == null) return map;

            for (int page = 0; page < atlases.Count; page++)
            {
                int ours = _atlasTexture.TextureIdFor(page);
                if (ours == 0 || atlases[page] == null || atlases[page].TextureId == 0) continue;

                map[atlases[page].TextureId] = ours;
            }

            return map;
        }

        public void Dispose()
        {
            if (_mod != null && _mod.Capi != null && _binder != null)
            {
                _mod.Capi.Event.UnregisterRenderer(_binder, EnumRenderStage.Opaque);
            }

            if (_bindInterceptor != null)
            {
                _bindInterceptor.Uninstall();
                _bindInterceptor = null;
            }

            if (_atlasTexture != null)
            {
                _atlasTexture.Dispose();
                _atlasTexture = null;
            }

            _binder = null;
            _mod = null;
        }
    }
}
