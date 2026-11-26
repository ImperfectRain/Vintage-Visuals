using System;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using static VintageVisuals.PseudoPBR.BeamPassSource;

namespace VintageVisuals.PseudoPBR
{
    /// <summary>
    /// D87. The air beams under a canopy, drawn once per screen pixel at half
    /// resolution instead of inside every terrain fragment.
    ///
    /// WHY. The in-shader march (vvTerrainBeamInscatter, called from
    /// chunkopaque and chunktopsoil) runs for every fragment the terrain draws,
    /// and a forest is mostly overdrawn leaves: the bench put it at about 13 ms
    /// of opaque GPU time, the largest single cost in the mod. Its per-pixel
    /// sample offset, recomputed in each overdrawn leaf, is also the crosshatch
    /// seen in the forest screenshots.
    ///
    /// HOW. Two passes at the end of the opaque stage, after every opaque
    /// thing (terrain, entities, the textured leaves) has written depth:
    ///   1. March: a half-by-half target (a quarter of the pixels), one
    ///      fragment per pixel, no overdraw. The position is rebuilt from the
    ///      primary depth in the same feet-origin space the terrain uses, and
    ///      the SAME vvTerrainBeamInscatter runs on it - the program is built
    ///      from the shipped snippets (BeamPassSource), not a copy.
    ///   2. Composite: full resolution, a 3x3 depth-aware filter of the march,
    ///      added onto the primary colour. Depth test and depth writes off,
    ///      alpha masked, every other attachment gets nothing.
    ///
    /// TOGGLE. PseudoPBR.FastBeams (and '.vvbeams'). Off by default until the
    /// player has compared the two in game. When this pass draws, the binder
    /// uploads vv_pbrBeamPass = 1 and the terrain skips its own beams, so the
    /// two never add together; when the pass cannot draw (off, not compiled,
    /// failed) the uniform is 0 and the old beams are exactly as before.
    ///
    /// RENDER TARGET (atmosphere rule 8). One RGBA16F colour target at half the
    /// primary buffer's width and height: rgb the beam light, alpha linear
    /// depth. Created on the first frame the pass draws, recreated when the
    /// primary size changes, destroyed in Dispose and when the toggle goes off.
    /// At 2560x1440 that is 1280x720 x 8 bytes = about 7 MB. Cost not measured
    /// in game; '.vvbench beams' measures it.
    ///
    /// FAIL-SAFE. A compile, framebuffer or draw failure disables the pass for
    /// the session and logs; the binder then reports 0 and the terrain draws
    /// the old beams.
    /// </summary>
    public sealed class BeamPassRenderer : IRenderer
    {
        public const string MarchProgramName = "vvbeammarch";
        public const string CompositeProgramName = "vvbeamcomposite";

        /// <summary>After every opaque renderer the mod or the game is known to use.</summary>
        public const double Order = 0.98;

        private const float Scale = 0.5f;

        private readonly ICoreClientAPI _capi;
        private readonly Func<string, string> _snippet;
        private readonly Func<bool> _wanted;
        private readonly Func<bool> _live;
        private readonly Action<IShaderProgram> _uploadScene;

        private IShaderProgram _march;
        private IShaderProgram _composite;
        private FrameBufferRef _target;
        private MeshRef _quad;
        private int _width;
        private int _height;
        private bool _failed;
        private bool _compiled;
        private bool _reportedActive;
        private int _skips;

        private readonly float[] _viewProjection = new float[16];
        private readonly float[] _inverse = new float[16];
        private readonly Vec4f _fog = new Vec4f();
        private readonly Vec2f _z = new Vec2f();
        private readonly Vec2f _lowSize = new Vec2f();

        public double RenderOrder => Order;
        public int RenderRange => 0;

        /// <param name="snippet">Reads a shipped snippet by file name, null when missing.</param>
        /// <param name="wanted">Whether the player (or a bench) wants the fast beams this frame.</param>
        /// <param name="uploadScene">The binder's beam uniforms, uploaded to the bound march program.</param>
        /// <param name="live">Whether the terrain received this frame's uniforms (the pass reads the same values).</param>
        public BeamPassRenderer(ICoreClientAPI capi, Func<string, string> snippet, Func<bool> wanted,
                                Func<bool> live, Action<IShaderProgram> uploadScene)
        {
            _live = live;
            _capi = capi;
            _snippet = snippet;
            _wanted = wanted;
            _uploadScene = uploadScene;
        }

        /// <summary>
        /// True when this frame's beams come from this pass, so the terrain
        /// must not draw its own. Asked by the binder before the terrain draws.
        /// </summary>
        public bool WillDraw
        {
            get
            {
                // Never compiles from here: the binder asks with a program bound.
                return _compiled && !_failed && Wanted();
            }
        }

        private bool Wanted()
        {
            try { return _wanted != null && _wanted(); }
            catch { return false; }
        }

        /// <summary>A shader reload disposes registered programs; rebuild them (see SceneCaptureRenderer).</summary>
        public void OnShadersReloaded()
        {
            _compiled = false;
        }

        private void TryCompile()
        {
            if (_failed) return;
            try
            {
                string march = BeamPassSource.March(_snippet);
                if (march == null)
                {
                    Fail("the beam march could not be assembled - a shader snippet is missing");
                    return;
                }

                _march = Build(MarchProgramName, march);
                _composite = Build(CompositeProgramName, BeamPassSource.Composite);
                if (_march == null || _composite == null) return;

                if (_quad == null) _quad = _capi.Render.UploadMesh(BuildFullscreenQuad());
                _compiled = true;
            }
            catch (Exception e)
            {
                Fail("the beam pass could not be built: " + e.Message);
            }
        }

        private IShaderProgram Build(string name, string fragment)
        {
            IShaderProgram program = _capi.Shader.NewShaderProgram();
            program.AssetDomain = "vintagevisuals";
            program.VertexShader = _capi.Shader.NewShader(EnumShaderType.VertexShader);
            program.FragmentShader = _capi.Shader.NewShader(EnumShaderType.FragmentShader);
            program.VertexShader.Code = BeamPassSource.Vertex;
            program.FragmentShader.Code = fragment;
            _capi.Shader.RegisterMemoryShaderProgram(name, program);

            if (!program.Compile())
            {
                Fail("the " + name + " program did not compile");
                return null;
            }
            return program;
        }

        private static MeshData BuildFullscreenQuad()
        {
            var mesh = new MeshData(4, 6, false, true, true, false);
            mesh.AddVertexSkipTex(-1f, -1f, 0f); mesh.Uv[0] = 0f; mesh.Uv[1] = 0f;
            mesh.AddVertexSkipTex( 1f, -1f, 0f); mesh.Uv[2] = 1f; mesh.Uv[3] = 0f;
            mesh.AddVertexSkipTex( 1f,  1f, 0f); mesh.Uv[4] = 1f; mesh.Uv[5] = 1f;
            mesh.AddVertexSkipTex(-1f,  1f, 0f); mesh.Uv[6] = 0f; mesh.Uv[7] = 1f;
            mesh.AddIndex(0); mesh.AddIndex(1); mesh.AddIndex(2);
            mesh.AddIndex(0); mesh.AddIndex(2); mesh.AddIndex(3);
            return mesh;
        }

        private bool EnsureTarget(FrameBufferRef primary)
        {
            int w = Math.Max(16, (int)(primary.Width * Scale));
            int h = Math.Max(16, (int)(primary.Height * Scale));
            if (_target != null && _width == w && _height == h) return true;

            DestroyTarget();

            var attrs = new FramebufferAttrs("vintagevisuals:beampass", w, h)
            {
                Attachments = new[]
                {
                    new FramebufferAttrsAttachment
                    {
                        AttachmentType = EnumFramebufferAttachment.ColorAttachment0,
                        Texture = new RawTexture
                        {
                            Width = w,
                            Height = h,
                            PixelInternalFormat = EnumTextureInternalFormat.Rgba16f,
                            PixelFormat = EnumTexturePixelFormat.Rgba,
                            // Read with texelFetch, so the filter is never used.
                            MinFilter = EnumTextureFilter.Nearest,
                            MagFilter = EnumTextureFilter.Nearest,
                            WrapS = EnumTextureWrap.ClampToEdge,
                            WrapT = EnumTextureWrap.ClampToEdge,
                        },
                    },
                },
            };

            _target = _capi.Render.CreateFrameBuffer(attrs);
            if (_target == null || _target.ColorTextureIds == null || _target.ColorTextureIds.Length == 0)
            {
                _target = null;
                Fail("the beam pass framebuffer could not be created");
                return false;
            }

            _width = w;
            _height = h;
            return true;
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (stage != EnumRenderStage.Opaque || _failed) return;

            if (!Wanted())
            {
                // Off: give the memory back rather than hold 7 MB for a toggle.
                if (_target != null) DestroyTarget();
                return;
            }

            if (_capi.Render.CurrentActiveShader != null)
            {
                Skip("another shader program was bound at the end of the opaque stage");
                return;
            }

            if (_compiled && (_march == null || _composite == null || _march.Disposed || _composite.Disposed))
            {
                _compiled = false;
            }

            if (!_compiled)
            {
                // Built here and drawn from the NEXT frame: the terrain already
                // drew this frame's beams, because WillDraw said no.
                TryCompile();
                return;
            }

            bool live;
            try { live = _live == null || _live(); }
            catch { live = false; }
            if (!live) return;

            FrameBufferRef primary = Primary();
            if (primary == null || primary.DepthTextureId == 0)
            {
                Skip("the primary framebuffer has no depth texture");
                return;
            }

            float[] projection = _capi.Render.CurrentProjectionMatrix;
            float[] view = _capi.Render.CameraMatrixOriginf;
            if (projection == null || view == null)
            {
                Skip("no camera matrix");
                return;
            }

            try
            {
                if (!EnsureTarget(primary)) return;

                Mat4f.Mul(_viewProjection, projection, view);
                if (Mat4f.Invert(_inverse, _viewProjection) == null)
                {
                    Skip("the view-projection matrix is not invertible");
                    return;
                }

                var ambient = _capi.Ambient;
                _fog.Set(ambient.BlendedFogDensity, ambient.BlendedFogMin,
                         ambient.BlendedFlatFogDensity, ambient.BlendedFlatFogYPosForShader);
                _z.X = _capi.Render.ShaderUniforms.ZNear;
                _z.Y = _capi.Render.ShaderUniforms.ZFar;
                _lowSize.X = _width;
                _lowSize.Y = _height;

                FrameBufferRef previous = _capi.Render.CurrentFrameBuffer;

                // 1. March, at half resolution.
                _capi.Render.CurrentFrameBuffer = _target;
                _capi.Render.GlViewport(0, 0, _width, _height);
                _capi.Render.GLDisableDepthTest();
                _capi.Render.GLDepthMask(false);
                _capi.Render.GlToggleBlend(false);

                _march.Use();
                _march.BindTexture2D(DepthUniform, primary.DepthTextureId, 0);
                _march.UniformMatrix(InverseViewProjectionUniform, _inverse);
                _march.Uniform(FogUniform, _fog);
                _march.Uniform(ZPlanesUniform, _z);
                _march.Uniform("lightPosition", _capi.Render.ShaderUniforms.LightPosition3D);
                _uploadScene?.Invoke(_march);
                _capi.Render.RenderMesh(_quad);
                _march.Stop();

                // 2. Composite onto the primary buffer, additively, colour only.
                _capi.Render.CurrentFrameBuffer = primary;
                _capi.Render.GlViewport(0, 0, primary.Width, primary.Height);
                _capi.Render.GlToggleBlend(true, EnumBlendMode.Brighten);
                _capi.Render.GlColorMask(true, true, true, false);

                _composite.Use();
                _composite.BindTexture2D(LowResUniform, _target.ColorTextureIds[0], 0);
                _composite.BindTexture2D(DepthUniform, primary.DepthTextureId, 1);
                _composite.Uniform(LowSizeUniform, _lowSize);
                _composite.Uniform(ZPlanesUniform, _z);
                _capi.Render.RenderMesh(_quad);
                _composite.Stop();

                // Back to the state the opaque stage runs in.
                _capi.Render.GlColorMask(true, true, true, true);
                _capi.Render.GlToggleBlend(true, EnumBlendMode.Standard);
                _capi.Render.GLDepthMask(true);
                _capi.Render.GLEnableDepthTest();
                _capi.Render.CurrentFrameBuffer = previous;
                _capi.Render.GlViewport(0, 0, primary.Width, primary.Height);

                _skips = 0;
                if (!_reportedActive)
                {
                    _reportedActive = true;
                    _capi.Logger.Notification("[VintageVisuals] pseudopbr: fast beams active - march at " +
                        _width + "x" + _height + " (RGBA16F), composite at " + primary.Width + "x" + primary.Height +
                        "; the terrain's own beams are off while this pass draws (D87).");
                }
            }
            catch (Exception e)
            {
                try
                {
                    _capi.Render.GlColorMask(true, true, true, true);
                    _capi.Render.GLDepthMask(true);
                    _capi.Render.GLEnableDepthTest();
                    _capi.Render.CurrentFrameBuffer = Primary();
                }
                catch { }
                Fail("the beam pass failed while rendering: " + e.Message);
            }
        }

        private FrameBufferRef Primary()
        {
            var buffers = _capi.Render.FrameBuffers;
            int index = (int)EnumFrameBuffer.Primary;
            if (buffers == null || index >= buffers.Count) return null;
            FrameBufferRef buffer = buffers[index];
            return buffer == null || buffer.Disposed ? null : buffer;
        }

        /// <summary>A pass that silently skips looks exactly like one that draws nothing; say so once.</summary>
        private void Skip(string why)
        {
            _skips++;
            if (_skips == 120)
                _capi.Logger.Warning("[VintageVisuals] pseudopbr: fast beams skipped 120 frames in a row - " + why + ".");
        }

        private void Fail(string why)
        {
            _failed = true;
            _compiled = false;
            _capi.Logger.Warning("[VintageVisuals] pseudopbr: fast beams DISABLED - " + why +
                ". The terrain draws its own beams instead (D87).");
        }

        private void DestroyTarget()
        {
            if (_target == null) return;
            _capi.Render.DestroyFrameBuffer(_target);
            _target = null;
            _width = 0;
            _height = 0;
        }

        public void Dispose()
        {
            DestroyTarget();
            if (_quad != null)
            {
                _quad.Dispose();
                _quad = null;
            }
        }
    }
}
