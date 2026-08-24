using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace VintageVisuals.Reflections
{
    /// <summary>
    /// The render-stage bridge: a low-resolution copy of the finished frame,
    /// plus the matrix that frame was drawn with.
    ///
    /// WHY THIS EXISTS. The terrain shader knows the material's texture grid but
    /// cannot see the scene - chunkopaque.fsh is a forward opaque pass, and the
    /// frame it would sample is the one it is still drawing. The post-process
    /// pass can see the scene but has no idea which texture texel a fragment
    /// belongs to. Neither pass can produce a pixel-art mirror alone.
    ///
    /// So the scene is carried ACROSS A FRAME instead of across a pass. At the
    /// end of each frame the composed image is copied here, shrunk, with linear
    /// depth packed into its alpha. The next frame's terrain pass samples that
    /// copy. The reflection is therefore one frame stale, which is the price of
    /// the bridge and is stated plainly rather than hidden - see Limits in
    /// STATUS.md for why a quantised reflection tolerates it far better than a
    /// smooth one would.
    ///
    /// The capture is HALF resolution in each axis, so a quarter of the pixels.
    /// It started at a quarter in each axis on the reasoning that a 16x16
    /// destination cannot express more, which confused two resolutions: the
    /// destination decides how many LOOKUPS there are, the source decides
    /// whether each lookup lands on the right thing. See CaptureScale.
    ///
    /// EVERYTHING HERE IS FAIL-SAFE. Every step that could fail - the shader,
    /// the framebuffer, the engine's texture ids - is checked, and any failure
    /// disables the feature and logs rather than throwing. The terrain shader
    /// reads a validity uniform which is 0 until a real capture exists, and 0
    /// means "use the analytic fallback", which is the behaviour that shipped
    /// before this feature.
    /// </summary>
    public sealed class SceneCaptureRenderer : IRenderer
    {
        /// <summary>
        /// Texture unit for the captured scene.
        ///
        /// 15 and 14 are the two material atlases. 13 is the next one down and
        /// still inside the 0..15 range OpenGL 3.3 guarantees for a fragment
        /// shader, which is the range this project is required to stay in.
        /// </summary>
        public const int TextureUnit = 13;

        /// <summary>
        /// Fraction of the screen the capture is rendered at.
        ///
        /// HALF, not a quarter. The first version reasoned that a 16x16
        /// destination cannot express more than a coarse source, which confuses
        /// two different resolutions: the destination decides how many LOOKUPS
        /// there are, the source decides whether each lookup lands on the right
        /// thing. A block filling 600 screen pixels is served fine by a quarter
        /// capture; the same block 30 pixels away gets about eight source pixels
        /// to reflect a whole world into, and the image is destroyed before it
        /// ever reaches the material grid.
        ///
        /// Half is still a quarter of the pixels and, with nearest sampling
        /// below, keeps the reflected colour a real pixel of the captured world.
        private const float CaptureScale = 0.5f;

        /// <summary>
        /// How thick a surface the reflection march is willing to accept, in
        /// blocks. Pinned to VV_SSR_THICKNESS in pseudopbr.glsl by a smoke
        /// check, because the whole point of the report below is comparing the
        /// two and a stale copy would compare against a number nobody ships.
        /// </summary>
        private const float ReflectionThicknessBlocks = 0.5f;

        /// <summary>Values an RGBA8 channel can hold, minus one: the divisor of its quantum.</summary>
        private const float DepthChannelLevels = 255f;

        private bool _reportedDepthResolution;
        private bool _reportedCaptureSource;

        /// <summary>
        /// Says, once, whether the capture can answer the question the march
        /// asks of it.
        ///
        /// THE DEPTH IS ONE BYTE. The capture packs linear view depth into the
        /// alpha of an RGBA8 target as `linear / zFar`, so the smallest depth
        /// difference it can represent anywhere in the world is zFar/255 blocks.
        /// The march then decides whether a refined crossing landed within
        /// VV_SSR_THICKNESS - half a block - of the surface it hit.
        ///
        /// Those two numbers cross over at zFar = 128. Above it the tolerance is
        /// finer than the data, and the accept/reject decision is being made
        /// inside the quantisation noise rather than on geometry: correct hits
        /// are discarded and wrong ones are kept, in a pattern that follows
        /// depth rather than anything in the scene. Five bisection passes cannot
        /// help, because they refine the ray parameter against the same
        /// quantised sample - the precision was lost in the capture, not in the
        /// search.
        ///
        /// Vintage Story's far plane follows the player's view distance and is
        /// routinely several hundred blocks, so this is expected to fire. It is
        /// logged rather than fixed here: changing the capture format is a real
        /// change with a real cost, and it should be made against a measured
        /// number rather than an assumed one.
        /// </summary>
        private void ReportDepthResolution(float zFar)
        {
            if (_reportedDepthResolution) return;
            _reportedDepthResolution = true;

            float quantum = zFar / DepthChannelLevels;

            if (quantum <= ReflectionThicknessBlocks)
            {
                _capi.Logger.Notification(
                    "[VintageVisuals] reflections: capture depth quantum is " +
                    quantum.ToString("0.000") + " blocks at zFar " + zFar.ToString("0") +
                    ", inside the " + ReflectionThicknessBlocks.ToString("0.00") +
                    " block hit tolerance. The march is deciding on geometry.");
                return;
            }

            _capi.Logger.Warning(
                "[VintageVisuals] reflections: capture depth quantum is " +
                quantum.ToString("0.000") + " blocks at zFar " + zFar.ToString("0") +
                ", which is " + (quantum / ReflectionThicknessBlocks).ToString("0.0") +
                "x COARSER than the " + ReflectionThicknessBlocks.ToString("0.00") +
                " block hit tolerance that judges a crossing. Depth is packed into one byte of " +
                "an RGBA8 capture, so the tolerance is finer than the data and hits are being " +
                "accepted or rejected inside the quantisation noise. Refinement cannot recover " +
                "this - it searches the ray against the same quantised sample. Reflections will " +
                "read as environmental colour rather than as recognizable geometry until the " +
                "capture carries more depth precision.");
        }


        private readonly ICoreClientAPI _capi;
        private readonly Action<string> _log;
        private readonly Func<bool> _capturePaused;

        private IShaderProgram _program;
        private FrameBufferRef _target;
        private MeshRef _quad;

        private int _width;
        private int _height;

        private bool _failed;
        private bool _hasCapture;
        private int _skips;
        private bool _reportedDebugFreeze;

        /// <summary>The view-projection the capture was drawn with.</summary>
        public float[] CaptureViewProjection { get; } = new float[16];

        /// <summary>
        /// Where the camera was when the capture was taken, in the game's own
        /// absolute coordinates.
        ///
        /// The terrain shader works in camera-relative space, and the camera has
        /// MOVED between the capture and the frame that reads it. Without this
        /// the reflection would be projected as though the player had not moved
        /// since, and would slide across every surface as they walk - the exact
        /// failure the pixel grid is meant to rule out.
        /// </summary>
        public Vec3d CapturePosition { get; private set; } = new Vec3d();

        public bool HasCapture => _hasCapture && !_failed;

        public int TextureId => _target != null && _target.ColorTextureIds.Length > 0
            ? _target.ColorTextureIds[0]
            : 0;

        public double RenderOrder => 1.0;
        public int RenderRange => 0;

        public SceneCaptureRenderer(ICoreClientAPI capi, Action<string> log, Func<bool> capturePaused)
        {
            _capi = capi;
            _log = log;
            _capturePaused = capturePaused;
        }

        /// <summary>
        /// Compiles the capture shader and allocates the target.
        ///
        /// Returns false rather than throwing on any failure: this is an
        /// optional visual feature and it has no business taking the client
        /// down with it.
        /// </summary>
        /// <summary>
        /// Rebuilds the capture's own shader program after the game has
        /// reloaded shaders.
        ///
        /// This is NOT a nicety. A registered program is disposed and recreated
        /// by the game on every shader reload, and this mod FORCES one at
        /// startup so its patches reach the already-compiled programs. The
        /// capture's own program was disposed by that reload, the next frame
        /// threw "Can't use a disposed shader!", and Fail() switched reflections
        /// off for the whole session - so the feature could never work at all,
        /// on any machine, and the log said so every time.
        ///
        /// A disposed program after a reload is a RECOVERABLE state, not a
        /// fault. Recompiling is what the game expects a registered program's
        /// owner to do.
        /// </summary>
        public void OnShadersReloaded()
        {
            if (_failed) return;

            try
            {
                if (!Compile())
                {
                    Fail("the scene capture shader did not recompile after a shader reload");
                }
            }
            catch (Exception e)
            {
                Fail("the scene capture shader could not be rebuilt: " + e.Message);
            }
        }

        /// <summary>Registers and compiles the program. Shared by first setup and rebuild.</summary>
        private bool Compile()
        {
            _program = _capi.Shader.NewShaderProgram();
            _program.AssetDomain = "vintagevisuals";
            _capi.Shader.RegisterFileShaderProgram("vvscenecapture", _program);

            return _program.Compile();
        }

        public bool TryInitialise()
        {
            if (_failed) return false;

            try
            {
                _program = _capi.Shader.NewShaderProgram();
                _program.AssetDomain = "vintagevisuals";
                _capi.Shader.RegisterFileShaderProgram("vvscenecapture", _program);

                if (!_program.Compile())
                {
                    Fail("the scene capture shader did not compile");
                    return false;
                }

                if (!EnsureTarget()) return false;

                _quad = _capi.Render.UploadMesh(BuildFullscreenQuad());
                return true;
            }
            catch (Exception e)
            {
                Fail("scene capture could not be set up: " + e.Message);
                return false;
            }
        }

        /// <summary>
        /// A clip-space quad covering the screen.
        ///
        /// Built by hand rather than borrowed from the game because the capture
        /// shader takes positions already in clip space - see its vertex stage.
        /// </summary>
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

        /// <summary>
        /// Allocates or reallocates the capture target for the current window
        /// size. A resize destroys the old one first, or the driver keeps both.
        /// </summary>
        private bool EnsureTarget()
        {
            int want = Math.Max(16, (int)(_capi.Render.FrameWidth * CaptureScale));
            int wantH = Math.Max(16, (int)(_capi.Render.FrameHeight * CaptureScale));

            if (_target != null && _width == want && _height == wantH) return true;

            if (_target != null)
            {
                _capi.Render.DestroyFrameBuffer(_target);
                _target = null;
                _hasCapture = false;
            }

            var attrs = new FramebufferAttrs("vintagevisuals:scenecapture", want, wantH)
            {
                Attachments = new[]
                {
                    new FramebufferAttrsAttachment
                    {
                        AttachmentType = EnumFramebufferAttachment.ColorAttachment0,

                        // ClampToEdge is load-bearing, not a default carried
                        // over. A reflected ray that leaves the screen must be
                        // REJECTED and fall back to the analytic environment;
                        // with Repeat it would instead sample the far side of
                        // the frame and paint unrelated geometry onto the
                        // surface, which is the classic screen-space artefact
                        // and reads far worse than a plain sky.
                        Texture = new RawTexture
                        {
                            Width = want,
                            Height = wantH,
                            PixelInternalFormat = EnumTextureInternalFormat.Rgba8,
                            PixelFormat = EnumTexturePixelFormat.Rgba,
                            // NEAREST, not linear. Bilinear filtering blends
                            // four captured pixels into every lookup, so the
                            // colour a texel receives is an interpolation of
                            // things that are not there - a blurry
                            // reconstruction wearing a pixel grid. The whole
                            // visual language depends on the reflected colour
                            // being a real pixel of the captured world.
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
                Fail("the scene capture framebuffer could not be created");
                return false;
            }

            _width = want;
            _height = wantH;
            return true;
        }

        /// <summary>
        /// Copies the finished frame into the capture target.
        ///
        /// Runs at AfterPostProcessing, which is the first stage where the
        /// primary framebuffer holds a composed scene rather than a partly drawn
        /// one. Reading it any earlier is reading a frame mid-render.
        /// </summary>
        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            if (_failed || _program == null) return;

            try
            {
                if (CapturePaused())
                {
                    if (!_reportedDebugFreeze)
                    {
                        _reportedDebugFreeze = true;
                        _log("reflections: scene capture frozen while PseudoPBR debug view is active - "
                           + "reflection diagnostics inspect the last normal rendered frame instead of "
                           + "capturing their own replacement output.");
                    }

                    if (!_hasCapture)
                    {
                        Skip("scene capture is waiting for a normal frame because PseudoPBR debug view is active");
                    }

                    return;
                }

                if (!EnsureTarget()) return;

                FrameBufferRef primary = FrameBuffer(EnumFrameBuffer.Primary);
                FrameBufferRef current = _capi.Render.CurrentFrameBuffer;
                CaptureSource source = ChooseCaptureSource(current, primary);

                if (source.Color == null)
                {
                    Skip("no framebuffer with a colour texture is available for scene capture");
                    return;
                }

                if (source.Depth == null)
                {
                    Skip("no framebuffer with a depth texture is available for scene capture");
                    return;
                }

                ReportCaptureSource(source, current, primary);

                FrameBufferRef previous = current;

                _capi.Render.CurrentFrameBuffer = _target;
                _capi.Render.GlViewport(0, 0, _width, _height);
                _capi.Render.GLDisableDepthTest();
                _capi.Render.GlToggleBlend(false);

                _program.Use();
                _program.BindTexture2D("sceneColor", source.Color.ColorTextureIds[0], 0);
                _program.BindTexture2D("sceneDepth", source.Depth.DepthTextureId, 1);
                _program.Uniform("zNear", _capi.Render.ShaderUniforms.ZNear);
                _program.Uniform("zFar", _capi.Render.ShaderUniforms.ZFar);

                ReportDepthResolution(_capi.Render.ShaderUniforms.ZFar);

                _capi.Render.RenderMesh(_quad);
                _program.Stop();

                _capi.Render.CurrentFrameBuffer = previous;
                _capi.Render.GlViewport(0, 0, _capi.Render.FrameWidth, _capi.Render.FrameHeight);
                _capi.Render.GlToggleBlend(true);

                RecordCameraState();
                _hasCapture = true;
                _skips = 0;
            }
            catch (Exception e)
            {
                Fail("scene capture failed while rendering: " + e.Message);
            }
        }

        /// <summary>
        /// Stores the transform the capture was drawn with, so the next frame
        /// can project a reflected point into it.
        /// </summary>
        private void RecordCameraState()
        {
            float[] projection = _capi.Render.CurrentProjectionMatrix;
            float[] view = _capi.Render.CameraMatrixOriginf;

            if (projection == null || view == null) return;

            Mat4f.Mul(CaptureViewProjection, projection, view);

            EntityPos camera = _capi.World?.Player?.Entity?.Pos;
            if (camera != null) CapturePosition = new Vec3d(camera.X, camera.Y, camera.Z);
        }

        private FrameBufferRef FrameBuffer(EnumFrameBuffer kind)
        {
            var buffers = _capi.Render.FrameBuffers;
            int index = (int)kind;

            if (buffers == null || index < 0 || index >= buffers.Count) return null;

            FrameBufferRef buffer = buffers[index];

            if (buffer == null || buffer.Disposed) return null;
            return buffer;
        }

        private bool CapturePaused()
        {
            try
            {
                return _capturePaused != null && _capturePaused();
            }
            catch
            {
                return false;
            }
        }

        private CaptureSource ChooseCaptureSource(FrameBufferRef current, FrameBufferRef primary)
        {
            FrameBufferRef color = HasColorTexture(current) ? current : null;

            if (color == null && HasColorTexture(primary)) color = primary;

            FrameBufferRef depth = HasDepthTexture(color) ? color : null;

            if (depth == null && HasDepthTexture(primary)) depth = primary;

            return new CaptureSource(color, depth);
        }

        private static bool HasColorTexture(FrameBufferRef buffer)
        {
            return buffer != null
                && !buffer.Disposed
                && buffer.ColorTextureIds != null
                && buffer.ColorTextureIds.Length > 0
                && buffer.ColorTextureIds[0] != 0;
        }

        private static bool HasDepthTexture(FrameBufferRef buffer)
        {
            return buffer != null && !buffer.Disposed && buffer.DepthTextureId != 0;
        }

        private void ReportCaptureSource(CaptureSource source, FrameBufferRef current, FrameBufferRef primary)
        {
            if (_reportedCaptureSource) return;
            _reportedCaptureSource = true;

            _log("reflections: scene capture source at AfterPostProcessing"
               + " order " + RenderOrder.ToString("0.00")
               + " current=" + DescribeBuffer(current)
               + " primary=" + DescribeBuffer(primary)
               + " chosenColor=" + DescribeBuffer(source.Color)
               + " chosenDepth=" + DescribeBuffer(source.Depth)
               + " target=" + DescribeBuffer(_target)
               + " viewport=" + _capi.Render.FrameWidth + "x" + _capi.Render.FrameHeight
               + " -> " + _width + "x" + _height
               + " all=" + DescribeFrameBuffers());
        }

        private string DescribeFrameBuffers()
        {
            var buffers = _capi.Render.FrameBuffers;
            if (buffers == null) return "none";

            var parts = new List<string>();

            for (int i = 0; i < buffers.Count; i++)
            {
                string name = Enum.IsDefined(typeof(EnumFrameBuffer), i)
                    ? ((EnumFrameBuffer)i).ToString()
                    : i.ToString();

                parts.Add(name + "=" + DescribeBuffer(buffers[i]));
            }

            return string.Join("; ", parts);
        }

        private static string DescribeBuffer(FrameBufferRef buffer)
        {
            if (buffer == null) return "null";
            if (buffer.Disposed) return "disposed";

            string colors = buffer.ColorTextureIds == null
                ? "none"
                : string.Join(",", buffer.ColorTextureIds.Select(id => id.ToString()));

            return "fbo " + buffer.FboId
                 + " " + buffer.Width + "x" + buffer.Height
                 + " color[" + colors + "]"
                 + " depth " + buffer.DepthTextureId;
        }

        private sealed class CaptureSource
        {
            public readonly FrameBufferRef Color;
            public readonly FrameBufferRef Depth;

            public CaptureSource(FrameBufferRef color, FrameBufferRef depth)
            {
                Color = color;
                Depth = depth;
            }
        }

        /// <summary>
        /// A capture that never happens is indistinguishable from one that works
        /// but reflects nothing, so the skips are counted and reported. This is
        /// the same lesson the shader binders learned: a binder that silently
        /// returns looks exactly like a binder that succeeded.
        /// </summary>
        private void Skip(string why)
        {
            _skips++;
            if (_skips == 60) _log("reflections: " + why + " - 60 frames with no capture, reflections are on the fallback");
        }

        private void Fail(string why)
        {
            _failed = true;
            _hasCapture = false;
            _log("reflections: DISABLED - " + why + ". Reflective surfaces fall back to the analytic environment.");
        }

        public void Dispose()
        {
            if (_target != null)
            {
                _capi.Render.DestroyFrameBuffer(_target);
                _target = null;
            }

            if (_quad != null)
            {
                _quad.Dispose();
                _quad = null;
            }

            _hasCapture = false;
        }
    }
}
