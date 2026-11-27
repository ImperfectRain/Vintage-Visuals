using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VintageVisuals.Reflections.Mirrors
{
    /// <summary>
    /// What a mirror camera draws beyond chunk terrain (D97 amendment, content
    /// tiers). Tier 1 is the terrain pass alone (blocks, topsoil, leaves and
    /// plants that are blocks) with the analytic sky. Tier 2 adds entities -
    /// creatures, other players, dropped items, and the player themselves seen
    /// in third person. Tier 3 adds the opaque cube particles.
    ///
    /// Each is the game's own renderer called once more inside the mirror pass,
    /// with the reflected matrices already in place, so it draws in the same
    /// space as the terrain:
    ///
    ///   entities   SystemRenderEntities.OnRenderOpaque3D, after each entity's
    ///              IsRendered is set from the MIRROR's frustum (the game set it
    ///              from the player's, so a creature behind the player would be
    ///              missing) and restored after.
    ///   the self   the player's own renderer is switched to third person for
    ///              the call, so the body draws, and switched back.
    ///   particles  the cube particle pools' meshes, drawn with the game's cube
    ///              particle program. NOT through the system's Render method:
    ///              that also steps the simulation, and a second call would run
    ///              every particle twice as fast. The buffers drawn are the ones
    ///              the game filled last frame.
    ///
    /// Everything is found by reflection once; anything missing turns that tier
    /// off with one log line and the terrain reflection continues.
    /// </summary>
    internal sealed class MirrorContent
    {
        private readonly ICoreClientAPI _capi;
        private bool _resolved;

        private IDictionary _entityRenderers;
        private object _entitySystem;
        private MethodInfo _entityOpaque;
        private bool _entitiesFailed;

        private FieldInfo _selfMode;
        private object _thirdPerson;

        private object _particleSystem;
        private FieldInfo _mainPools, _offPools;
        private IShaderProgram _cubeProgram;
        private Func<IShaderProgram> _cubeProgramGetter;
        private bool _particlesFailed;

        private readonly List<(Entity entity, bool was)> _saved = new List<(Entity, bool)>();
        private readonly object[] _oneArg = new object[1];

        public MirrorContent(ICoreClientAPI capi) { _capi = capi; }

        public string Describe(int tier)
        {
            string t = tier >= 3 ? "terrain, entities and particles" : tier == 2 ? "terrain and entities" : "terrain only";
            if (tier >= 2 && _entitiesFailed) t += " (entities unavailable, see log)";
            if (tier >= 3 && _particlesFailed) t += " (particles unavailable, see log)";
            return t;
        }

        private void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            object world = _capi.World;
            FieldInfo[] fields = world.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            try
            {
                _entityRenderers = fields.FirstOrDefault(f => f.Name == "EntityRenderers")?.GetValue(world) as IDictionary;
                var systems = fields.FirstOrDefault(f => f.Name == "clientSystems")?.GetValue(world) as Array;
                if (systems != null)
                {
                    foreach (object s in systems)
                    {
                        if (s == null) continue;
                        string n = s.GetType().FullName;
                        if (n == "Vintagestory.Client.NoObf.SystemRenderEntities") _entitySystem = s;
                        if (n == "Vintagestory.Client.NoObf.SystemRenderParticles") _particleSystem = s;
                    }
                }
                _entityOpaque = _entitySystem == null ? null : AccessTools.Method(_entitySystem.GetType(), "OnRenderOpaque3D", new[] { typeof(float) });
                if (_entityRenderers == null || _entityOpaque == null)
                {
                    _entitiesFailed = true;
                    _capi.Logger.Warning("[VintageVisuals] mirrors: entity renderer not found; mirrors show terrain only.");
                }
            }
            catch (Exception ex)
            {
                _entitiesFailed = true;
                _capi.Logger.Warning("[VintageVisuals] mirrors: entities in mirrors unavailable: " + ex.Message);
            }

            try
            {
                if (_particleSystem != null)
                {
                    _mainPools = AccessTools.Field(_particleSystem.GetType(), "mainthreadpools");
                    _offPools = AccessTools.Field(_particleSystem.GetType(), "offthreadpools");
                }
                Type programs = AccessTools.TypeByName("Vintagestory.Client.NoObf.ShaderPrograms");
                FieldInfo cube = programs == null ? null : AccessTools.Field(programs, "Particlescube");
                PropertyInfo cubeProp = cube == null && programs != null ? AccessTools.Property(programs, "Particlescube") : null;
                if (cube != null) _cubeProgramGetter = () => cube.GetValue(null) as IShaderProgram;
                else if (cubeProp != null) _cubeProgramGetter = () => cubeProp.GetValue(null) as IShaderProgram;
                if (_mainPools == null || _offPools == null || _cubeProgramGetter == null)
                {
                    _particlesFailed = true;
                    _capi.Logger.Warning("[VintageVisuals] mirrors: particle pools not found; mirrors show no particles.");
                }
            }
            catch (Exception ex)
            {
                _particlesFailed = true;
                _capi.Logger.Warning("[VintageVisuals] mirrors: particles in mirrors unavailable: " + ex.Message);
            }
        }

        /// <summary>Draws the tier's extra content into the current mirror pass. The reflected matrices must already be live.</summary>
        public void Draw(int tier, float dt, FrustumCulling culler, double range)
        {
            if (tier < 2) return;
            Resolve();
            if (!_entitiesFailed) DrawEntities(dt, culler, range);
            if (tier >= 3 && !_particlesFailed) DrawParticles();
        }

        private void DrawEntities(float dt, FrustumCulling culler, double range)
        {
            EntityPlayer self = _capi.World.Player?.Entity;
            if (self == null) return;
            int dim = self.Pos.Dimension;
            double rangeSq = range * range;
            object selfRenderer = null, selfModeWas = null;
            _saved.Clear();
            try
            {
                foreach (DictionaryEntry e in _entityRenderers)
                {
                    if (!(e.Value is EntityRenderer r) || r.entity == null) continue;
                    Entity en = r.entity;
                    bool visible = en.Pos.Dimension == dim
                        && self.Pos.SquareDistanceTo(en.Pos.X, en.Pos.InternalY, en.Pos.Z) < rangeSq
                        && culler.SphereInFrustum(en.Pos.X, en.Pos.InternalY, en.Pos.Z, Math.Max(0.5, en.FrustumSphereRadius));
                    _saved.Add((en, en.IsRendered));
                    if (visible && !en.IsRendered)
                    {
                        // The game skipped this one's per-frame preparation because
                        // the player cannot see it; the mirror can.
                        try { r.BeforeRender(dt); } catch { visible = false; }
                    }
                    en.IsRendered = visible;
                    if (en == self) selfRenderer = r;
                }

                if (selfRenderer != null) selfModeWas = ForceThirdPerson(selfRenderer);

                _oneArg[0] = dt;
                _entityOpaque.Invoke(_entitySystem, _oneArg);
            }
            catch (Exception ex)
            {
                _entitiesFailed = true;
                _capi.Logger.Warning("[VintageVisuals] mirrors: drawing entities in a mirror failed, mirrors show terrain only from now: "
                    + (ex is TargetInvocationException t && t.InnerException != null ? t.InnerException.Message : ex.Message));
            }
            finally
            {
                if (selfRenderer != null && selfModeWas != null) { try { _selfMode.SetValue(selfRenderer, selfModeWas); } catch { } }
                foreach (var (entity, was) in _saved) entity.IsRendered = was;
                _saved.Clear();
            }
        }

        /// <summary>Puts the player's own renderer in third person; returns the mode to restore, or null.</summary>
        private object ForceThirdPerson(object renderer)
        {
            if (_selfMode == null || _selfMode.DeclaringType == null || !_selfMode.DeclaringType.IsInstanceOfType(renderer))
            {
                _selfMode = AccessTools.Field(renderer.GetType(), "renderMode");
                if (_selfMode == null || !_selfMode.FieldType.IsEnum) { _selfMode = null; return null; }
                try { _thirdPerson = Enum.Parse(_selfMode.FieldType, "ThirdPerson"); }
                catch { _selfMode = null; return null; }
            }
            object was = _selfMode.GetValue(renderer);
            _selfMode.SetValue(renderer, _thirdPerson);
            return was;
        }

        private void DrawParticles()
        {
            try
            {
                var main = _mainPools.GetValue(_particleSystem) as Array;
                var off = _offPools.GetValue(_particleSystem) as Array;
                if (main == null || off == null || main.Length < 2 || off.Length < 2) return;
                _cubeProgram = _cubeProgramGetter();
                if (_cubeProgram == null) return;

                IRenderAPI r = _capi.Render;
                _cubeProgram.Use();
                try
                {
                    r.GlToggleBlend(true);
                    _cubeProgram.Uniform("rgbaFogIn", r.FogColor);
                    _cubeProgram.Uniform("rgbaAmbientIn", r.AmbientColor);
                    _cubeProgram.Uniform("fogMinIn", r.FogMin);
                    _cubeProgram.Uniform("fogDensityIn", r.FogDensity);
                    _cubeProgram.UniformMatrix("projectionMatrix", r.CurrentProjectionMatrix);
                    _cubeProgram.UniformMatrix("modelViewMatrix", r.CameraMatrixOriginf);
                    DrawPool(main.GetValue(1));
                    DrawPool(off.GetValue(1));
                }
                finally
                {
                    _cubeProgram.Stop();
                }
            }
            catch (Exception ex)
            {
                _particlesFailed = true;
                _capi.Logger.Warning("[VintageVisuals] mirrors: drawing particles in a mirror failed, off from now: " + ex.Message);
            }
        }

        private void DrawPool(object pool)
        {
            if (pool == null) return;
            Type t = pool.GetType();
            var model = t.GetProperty("Model")?.GetValue(pool) as MeshRef;
            object qty = t.GetProperty("QuantityAlive")?.GetValue(pool);
            int n = qty is int i ? i : 0;
            if (model == null || n <= 0) return;
            _capi.Render.RenderMeshInstanced(model, n);
        }
    }
}
