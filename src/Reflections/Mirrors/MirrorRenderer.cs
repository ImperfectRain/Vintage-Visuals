using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageVisuals.Reflections.Mirrors
{
    /// <summary>
    /// The mirror cameras (D97, prototype).
    ///
    /// WHAT. For each mirror plane in view, the game's own terrain pass
    /// (<c>ChunkRenderer.RenderOpaque</c>, the same method the depth prepass
    /// runs twice) is run once more, into a small region of a shared atlas,
    /// with three things changed around it and restored after:
    ///
    ///   the view     CameraMatrixOrigin x the reflection across the plane, so
    ///                every shader input in world space (positions, shadow map
    ///                coordinates, the mod's whole material) is untouched;
    ///   the frustum  a projection cropped to the part of the screen the mirror
    ///                covers, with its near plane replaced by the mirror plane
    ///                (oblique clipping), and the game's frustum culler pointed
    ///                at it, with the view distance cut to the reflection range;
    ///   the target   a sub-rectangle of the atlas sized to the texel grid:
    ///                one capture pixel per reflected cell at the mirror's
    ///                nearest point (times Resolution), never full screen.
    ///
    /// Before the terrain draws, the mirror's own visible rectangles are drawn
    /// into the region's depth as "far" over a "near" clear, so terrain lands
    /// only on pixels the mirror itself covers: a mirror seen half behind a
    /// pillar, or a thin chiselled inlay, shades only its own footprint.
    ///
    /// The terrain shader then reads that region at the screen position of each
    /// texel's centre (or of its 2x2, 4x4 ... cell, by distance), so the
    /// reflection snaps to the same texel grid as everything else.
    ///
    /// MIRRORS IN MIRRORS. A plane seen inside another mirror's view gets its
    /// own pass with both reflections composed, rendered first, into its own
    /// region; the parent pass's shader reads it exactly as the main pass reads
    /// a top-level mirror. Depth and total passes per frame are capped.
    ///
    /// COST WHEN NOTHING IS IN VIEW. A handful of box tests per mirror plane on
    /// the CPU and no GL call at all. With no mirror in the world, or mirrors
    /// switched off, the shader group is not even compiled.
    ///
    /// SAFETY. Every internal it touches is found by reflection and checked
    /// once; anything missing disables the mirrors for the session with a log
    /// line and the frame renders exactly as without them. Everything changed
    /// around the nested draw is restored in a finally.
    /// </summary>
    public sealed class MirrorRenderer : IRenderer
    {
        public const double Order = 0.36;          // after the PBR binder (0.35), before terrain (0.37)
        public const int TextureUnit = 9;          // free in chunkopaque: vanilla 0..6, mod 10..15
        public const int AtlasSize = 1024;
        public const int MaxSlots = 8;
        public const int MaxBoxes = 32;
        public const string SamplerUniform = "vv_mirrorTex";
        public const string CountUniform = "vv_mirrorBoxCount";
        public const string BoxUniform = "vv_mirrorBox";
        public const string PlaneUniform = "vv_mirrorPlane";
        public const string AtlasUniform = "vv_mirrorAtlas";
        public const string CropUniform = "vv_mirrorCrop";
        public const string LookUniform = "vv_mirrorLook";
        public const string ViewProjUniform = "vv_mirrorViewProj";
        private const int LosRaysPerFrame = 64;
        private const int LosRefreshFrames = 8;
        private const double ClipBias = 0.003;     // keeps the mirror's own face out of its reflection

        // --- the uniform context the pool-render hook uploads -----------------

        internal sealed class Context
        {
            public readonly float[] Boxes = new float[MaxBoxes * 2 * 4];
            public readonly float[] Planes = new float[MaxSlots * 4];
            public readonly float[] Atlas = new float[MaxSlots * 4];
            public readonly float[] Crop = new float[MaxSlots * 4];
            public readonly float[] Look = new float[MaxSlots * 4];
            public float[] ViewProj = new float[16];
            public int BoxCount;
            // The eye this pass looks from, in the feet-origin space terrain
            // positions use: the real eye in the main pass, the virtual one in
            // a mirror pass, so view vectors and distance fades in a reflection
            // start where the reflection is seen from.
            public readonly float[] Eye = new float[3];
            public bool EyeValid;

            public void Clear()
            {
                BoxCount = 0;
                EyeValid = false;
                Array.Clear(Atlas, 0, Atlas.Length);
            }
        }

        private static readonly Context Empty = new Context();
        private static Context _current = Empty;
        private static bool _mirrorPassActive;
        private static bool _ranThisFrame;
        private static bool _mainReflectValid;
        private static bool _hookFailed;
        private static ILogger _hookLogger;

        /// <summary>True while a mirror camera is drawing; the depth prepass and the chunk timers stand aside.</summary>
        public static bool MirrorPassActive { get { return _mirrorPassActive; } }

        /// <summary>The atlas texture for the per-draw bind, or 0.</summary>
        public static int AtlasTextureId { get; private set; }

        // --- state -----------------------------------------------------------

        private readonly ICoreClientAPI _capi;
        private readonly MirrorStore _store;
        private readonly Func<bool> _captureValid;
        private readonly Harmony _harmony;
        private readonly Context _main = new Context();
        private MirrorContent _content;

        private bool _failed;
        private bool _initialised;
        private MirrorGl _gl;
        private object _chunkRenderer;
        private MethodInfo _renderOpaque;
        private FrustumCulling _culler;
        private double[] _gamePerspectiveProj, _gamePerspectiveView;
        private FrameBufferRef _atlas;
        private IShaderProgram _mask;
        private MeshRef _quad;
        private readonly object[] _oneArg = new object[1];
        private readonly BlockPos _losPos = new BlockPos(0, 0, 0, 0);

        private readonly Dictionary<string, (bool visible, long frame)> _los = new Dictionary<string, (bool, long)>();
        private int _losRevision = -1;
        private long _frame;
        private int _losRaysThisFrame;

        // Stats for '.vvmirror stats'.
        private int _lastPasses, _lastVisiblePlanes, _lastPixels;
        private double _lastMs, _avgMs;

        /// <summary>
        /// Whether a terrain shader that reads the atlas can be live (PBR on, the
        /// modular material path). Without one, a pass draws a picture nobody
        /// shows, so none runs.
        /// </summary>
        public bool Allowed { get; set; }

        public double RenderOrder { get { return Order; } }
        public int RenderRange { get { return 0; } }

        public MirrorRenderer(ICoreClientAPI capi, MirrorStore store, Func<bool> captureValid, string harmonyId)
        {
            _capi = capi;
            _store = store;
            _captureValid = captureValid;
            _harmony = new Harmony(harmonyId + ".mirrors");
            _hookLogger = capi.Logger;
            _content = new MirrorContent(capi);
        }

        public string Stats()
        {
            return "Mirror cameras last frame: " + _lastPasses + " pass(es) for " + _lastVisiblePlanes + " visible plane(s), "
                 + _lastPixels + " capture pixels, CPU " + _lastMs.ToString("0.00") + " ms (avg " + _avgMs.ToString("0.00")
                 + " ms; GPU time is in '.vvbench' as 'vv mirror cameras')." + (_failed ? " DISABLED after an error, see the log." : "");
        }

        private bool Initialise()
        {
            if (_initialised) return !_failed;
            _initialised = true;
            try
            {
                _gl = MirrorGl.TryCreate(out string why);
                if (_gl == null) return Fail("GL bridge: " + why);

                Type crType = AccessTools.TypeByName("Vintagestory.Client.NoObf.ChunkRenderer");
                _renderOpaque = crType == null ? null : AccessTools.Method(crType, "RenderOpaque", new[] { typeof(float) });
                if (_renderOpaque == null) return Fail("ChunkRenderer.RenderOpaque not found");

                object world = _capi.World;
                FieldInfo[] fields = world.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _chunkRenderer = fields.FirstOrDefault(f => f.FieldType == crType)?.GetValue(world);
                _culler = fields.FirstOrDefault(f => f.FieldType == typeof(FrustumCulling))?.GetValue(world) as FrustumCulling;
                _gamePerspectiveProj = fields.FirstOrDefault(f => f.Name == "PerspectiveProjectionMat")?.GetValue(world) as double[];
                _gamePerspectiveView = fields.FirstOrDefault(f => f.Name == "PerspectiveViewMat")?.GetValue(world) as double[];
                if (_chunkRenderer == null || _culler == null || _gamePerspectiveProj == null || _gamePerspectiveView == null)
                    return Fail("client internals (chunk renderer, frustum culler, perspective matrices) not found");

                if (!CompileMask()) return Fail("the mirror mask shader did not compile");
                _quad = _capi.Render.UploadMesh(UnitQuad());

                _atlas = _capi.Render.CreateFrameBuffer(new FramebufferAttrs("vintagevisuals:mirrors", AtlasSize, AtlasSize)
                {
                    Attachments = new[]
                    {
                        new FramebufferAttrsAttachment
                        {
                            AttachmentType = EnumFramebufferAttachment.ColorAttachment0,
                            Texture = new RawTexture
                            {
                                Width = AtlasSize, Height = AtlasSize,
                                PixelInternalFormat = EnumTextureInternalFormat.Rgba16f,
                                PixelFormat = EnumTexturePixelFormat.Rgba,
                                // Nearest: a reflected cell is one real captured pixel, never a blend.
                                MinFilter = EnumTextureFilter.Nearest, MagFilter = EnumTextureFilter.Nearest,
                                WrapS = EnumTextureWrap.ClampToEdge, WrapT = EnumTextureWrap.ClampToEdge,
                            },
                        },
                        new FramebufferAttrsAttachment
                        {
                            AttachmentType = EnumFramebufferAttachment.DepthAttachment,
                            Texture = new RawTexture
                            {
                                Width = AtlasSize, Height = AtlasSize,
                                PixelFormat = EnumTexturePixelFormat.DepthComponent,
                                PixelInternalFormat = EnumTextureInternalFormat.DepthComponent32,
                            },
                        },
                    },
                });
                if (_atlas == null || _atlas.ColorTextureIds == null || _atlas.ColorTextureIds.Length == 0)
                    return Fail("the mirror atlas framebuffer could not be created");
                AtlasTextureId = _atlas.ColorTextureIds[0];

                MethodInfo poolRender = AccessTools.Method(typeof(MeshDataPoolManager), "Render",
                    new[] { typeof(Vec3d), typeof(string), typeof(EnumFrustumCullMode) });
                if (poolRender == null) return Fail("MeshDataPoolManager.Render not found");
                _harmony.Patch(poolRender, prefix: new HarmonyMethod(typeof(MirrorRenderer), nameof(PoolRenderPrefix)));

                _capi.Logger.Notification("[VintageVisuals] mirrors: camera pass ready (atlas " + AtlasSize + "x" + AtlasSize
                    + " RGBA16F + depth, unit " + TextureUnit + ", about 12 MB of GPU memory while mirrors are on).");
                return true;
            }
            catch (Exception ex)
            {
                return Fail("setup failed: " + ex.Message);
            }
        }

        private bool CompileMask()
        {
            _mask = _capi.Shader.NewShaderProgram();
            _mask.AssetDomain = "vintagevisuals";
            _capi.Shader.RegisterFileShaderProgram("vvmirrormask", _mask);
            return _mask.Compile();
        }

        public void OnShadersReloaded()
        {
            if (_failed || !_initialised) return;
            try { if (!CompileMask()) Fail("the mirror mask shader did not recompile after a shader reload"); }
            catch (Exception ex) { Fail("mask shader rebuild failed: " + ex.Message); }
        }

        private static MeshData UnitQuad()
        {
            var mesh = new MeshData(4, 6, false, true, true, false);
            mesh.AddVertexSkipTex(0f, 0f, 0f); mesh.Uv[0] = 0f; mesh.Uv[1] = 0f;
            mesh.AddVertexSkipTex(1f, 0f, 0f); mesh.Uv[2] = 1f; mesh.Uv[3] = 0f;
            mesh.AddVertexSkipTex(1f, 1f, 0f); mesh.Uv[4] = 1f; mesh.Uv[5] = 1f;
            mesh.AddVertexSkipTex(0f, 1f, 0f); mesh.Uv[6] = 0f; mesh.Uv[7] = 1f;
            mesh.AddIndex(0); mesh.AddIndex(1); mesh.AddIndex(2);
            mesh.AddIndex(0); mesh.AddIndex(2); mesh.AddIndex(3);
            return mesh;
        }

        private bool Fail(string why)
        {
            _failed = true;
            _current = Empty;
            _mirrorPassActive = false;
            AtlasTextureId = 0;
            _capi.Logger.Error("[VintageVisuals] mirrors: DISABLED for this session - " + why
                + ". Mirror surfaces keep their normal reflection; nothing else is affected.");
            return false;
        }

        // --- the per-frame plan -----------------------------------------------

        private sealed class RectGeo
        {
            public double[] C0 = new double[3], Eu = new double[3], Ev = new double[3];
            public string Key;
        }

        private sealed class PlaneGeo
        {
            public MirrorPlane Plane;
            public double Nx, Ny, Nz, W;     // camera-relative plane, n toward the reflecting side
            public List<RectGeo> Rects = new List<RectGeo>();
        }

        private sealed class Node
        {
            public PlaneGeo Geo;
            public Node Parent;
            public double[] T;                // camera-relative -> reflected camera-relative
            public bool Odd;
            public double U0, V0, U1, V1;     // crop in main-screen uv
            public double Distance;           // eye path length to the nearest visible point
            public List<RectGeo> Visible = new List<RectGeo>();
            public int X, Y, Wpx, Hpx;        // atlas region
            public readonly List<Node> Children = new List<Node>();
            public readonly Context Ctx = new Context();
            public int Depth;
        }

        public void OnRenderFrame(float deltaTime, EnumRenderStage stage)
        {
            _frame++;
            _main.Clear();
            _current = _main;
            _ranThisFrame = false;
            _lastPasses = 0; _lastVisiblePlanes = 0; _lastPixels = 0;

            MirrorSettings settings = _store.Settings;
            if (_failed || !Allowed || !settings.Enabled || !_store.HasMirrors) return;
            if (_capi.Render.CurrentActiveShader != null) return;
            if (!Initialise()) return;

            var clock = Stopwatch.StartNew();
            try
            {
                Frame(deltaTime, settings);
            }
            catch (Exception ex)
            {
                Fail("mirror frame failed: " + (ex is TargetInvocationException t && t.InnerException != null ? t.InnerException.Message : ex.Message));
            }
            _lastMs = clock.Elapsed.TotalMilliseconds;
            _avgMs = _avgMs * 0.95 + _lastMs * 0.05;
        }

        private void Frame(float dt, MirrorSettings settings)
        {
            EntityPlayer player = _capi.World?.Player?.Entity;
            if (player == null) return;
            Vec3d cam = player.CameraPos;
            double[] cmo = MirrorMath.Copy(_capi.Render.CameraMatrixOrigin);
            double[] proj = MirrorMath.Copy(_capi.Render.PMatrix.Top);
            double[] cmoInv = MirrorMath.Inverse(cmo);
            double[] eye = { cmoInv[12], cmoInv[13], cmoInv[14] };
            double[] vpMain = MirrorMath.Mul(proj, cmo);
            int frameW = _capi.Render.FrameWidth, frameH = _capi.Render.FrameHeight;
            double focal = frameH * proj[5] * 0.5;

            if (_losRevision != _store.Revision) { _los.Clear(); _losRevision = _store.Revision; }
            _losRaysThisFrame = 0;

            List<PlaneGeo> planes = BuildGeometry(cam);

            // Top-level: what the player can see.
            var roots = new List<Node>();
            foreach (PlaneGeo g in planes)
            {
                Node n = Plan(g, null, MirrorMath.Reflection(g.Nx, g.Ny, g.Nz, g.W), false, eye, vpMain, settings, cam,
                              0, 0, 1, 1, true);
                if (n != null) roots.Add(n);
            }
            _lastVisiblePlanes = roots.Count;
            if (roots.Count == 0) { BuildContext(_main, roots, MirrorMath.Mul(proj, cmo), planes.Count > 0 ? planes : null, cam, eye, settings, true); return; }

            // Budget: nearest first; then mirrors inside them.
            roots.Sort((a, b) => a.Distance.CompareTo(b.Distance));
            int budget = settings.MaxPasses;
            var passes = new List<Node>();
            foreach (Node r in roots) { if (passes.Count >= budget) break; passes.Add(r); }
            if (settings.MaxDepth > 0)
            {
                var frontier = new List<Node>(passes);
                for (int depth = 1; depth <= settings.MaxDepth && passes.Count < budget; depth++)
                {
                    var next = new List<Node>();
                    foreach (Node parent in frontier)
                    {
                        double[] tInv = MirrorMath.Inverse(parent.T);
                        MirrorMath.Transform(tInv, eye[0], eye[1], eye[2], 1, out double ex, out double ey, out double ez, out _);
                        double[] veye = { ex, ey, ez };
                        double[] vp = MirrorMath.Mul(vpMain, parent.T);
                        foreach (PlaneGeo g in planes)
                        {
                            if (passes.Count >= budget) break;
                            if (g == parent.Geo) continue;
                            double[] t = MirrorMath.Mul(parent.T, MirrorMath.Reflection(g.Nx, g.Ny, g.Nz, g.W));
                            Node c = Plan(g, parent, t, !parent.Odd, veye, vp, settings, cam,
                                          parent.U0, parent.V0, parent.U1, parent.V1, false);
                            if (c == null) continue;
                            c.Depth = depth;
                            parent.Children.Add(c);
                            passes.Add(c);
                            next.Add(c);
                        }
                    }
                    frontier = next;
                }
            }
            // Roots not in the budget keep their mirror material but no picture.
            var drawn = new HashSet<Node>(passes);

            // Size and pack: one capture pixel per reflected cell at the nearest point.
            passes.Sort((a, b) => (b.Hpx).CompareTo(a.Hpx));
            int shelfX = 0, shelfY = 0, shelfH = 0;
            foreach (Node n in passes.ToList())
            {
                int lod = Lod(n.Distance, settings.LodStart, settings.FineDistance);
                double cell = Math.Pow(2, lod) / 32.0;
                double ppc = focal * cell / Math.Max(0.05, n.Distance);
                double scale = Math.Max(1.0 / 64.0, Math.Min(1.0, settings.Resolution / Math.Max(1e-3, ppc)));
                n.Wpx = Math.Max(4, Math.Min(512, (int)Math.Ceiling((n.U1 - n.U0) * frameW * scale)));
                n.Hpx = Math.Max(4, Math.Min(512, (int)Math.Ceiling((n.V1 - n.V0) * frameH * scale)));
                if (shelfX + n.Wpx > AtlasSize) { shelfX = 0; shelfY += shelfH; shelfH = 0; }
                if (shelfY + n.Hpx > AtlasSize) { drawn.Remove(n); continue; }
                n.X = shelfX; n.Y = shelfY;
                shelfX += n.Wpx; shelfH = Math.Max(shelfH, n.Hpx);
            }
            passes.RemoveAll(n => !drawn.Contains(n) || (n.Parent != null && !drawn.Contains(n.Parent)));
            foreach (Node n in passes.ToList()) n.Children.RemoveAll(c => !passes.Contains(c));

            // Contexts: what each pass's shader sees of the mirrors inside it.
            foreach (Node n in passes)
            {
                MirrorMath.Transform(MirrorMath.Inverse(n.T), eye[0], eye[1], eye[2], 1, out double vx, out double vy, out double vz, out _);
                BuildContext(n.Ctx, n.Children, MirrorMath.Mul(vpMain, n.T), null, cam, new[] { vx, vy, vz }, settings, false);
            }
            BuildContext(_main, roots.Where(drawn.Contains).ToList(), vpMain, planes, cam, eye, settings, true);

            // Draw deepest first, so a parent's pass can read its children.
            passes.Sort((a, b) => b.Depth.CompareTo(a.Depth));
            RenderPasses(dt, passes, proj, cmo, cam, settings, player);
            _lastPasses = passes.Count;
            _lastPixels = passes.Sum(p => p.Wpx * p.Hpx);
        }

        /// <summary>-1 (half a texel) within the fine distance, 0 (one texel) within the start, then one octave per doubling. Mirrored in pbrterrainmirror.glsl.</summary>
        private static int Lod(double distance, float start, float fine)
        {
            if (fine > 0 && distance <= fine) return -1;
            if (distance <= start) return 0;
            return (int)Math.Min(6, Math.Floor(Math.Log(distance / start, 2)) + 1);
        }

        private List<PlaneGeo> BuildGeometry(Vec3d cam)
        {
            var list = new List<PlaneGeo>();
            foreach (MirrorPlane p in _store.World.Planes)
            {
                if (p.Rects.Count == 0) continue;
                var g = new PlaneGeo { Plane = p };
                double nx = p.Axis == 0 ? p.Sign : 0, ny = p.Axis == 1 ? p.Sign : 0, nz = p.Axis == 2 ? p.Sign : 0;
                double planeWorld = p.C16 / 16.0;
                double camAxis = p.Axis == 0 ? cam.X : p.Axis == 1 ? cam.Y : cam.Z;
                g.Nx = nx; g.Ny = ny; g.Nz = nz;
                g.W = -p.Sign * (planeWorld - camAxis);   // n.x + w = 0 at the plane, camera-relative
                int ua = MirrorSelector.U(p.Axis), va = MirrorSelector.V(p.Axis);
                double[] camv = { cam.X, cam.Y, cam.Z };
                for (int i = 0; i < p.Rects.Count; i++)
                {
                    MirrorRect r = p.Rects[i];
                    var rg = new RectGeo { Key = p.Key + "#" + i };
                    rg.C0[p.Axis] = planeWorld - camv[p.Axis];
                    rg.C0[ua] = r.U0 / 16.0 - camv[ua];
                    rg.C0[va] = r.V0 / 16.0 - camv[va];
                    rg.Eu[ua] = (r.U1 - r.U0) / 16.0;
                    rg.Ev[va] = (r.V1 - r.V0) / 16.0;
                    g.Rects.Add(rg);
                }
                list.Add(g);
            }
            return list;
        }

        /// <summary>
        /// Whether a plane is worth a camera as seen from <paramref name="eye"/>
        /// through <paramref name="vp"/> (the main view, or a parent mirror's),
        /// and where on screen: the crop is the bounding box of its visible
        /// rectangles, intersected with the parent's.
        /// </summary>
        private Node Plan(PlaneGeo g, Node parent, double[] t, bool odd, double[] eye, double[] vp, MirrorSettings s,
                          Vec3d cam, double pu0, double pv0, double pu1, double pv1, bool lineOfSight)
        {
            double side = g.Nx * eye[0] + g.Ny * eye[1] + g.Nz * eye[2] + g.W;
            if (side <= 0.01) return null;   // seen from behind or edge-on

            var node = new Node { Geo = g, Parent = parent, T = t, Odd = odd, Distance = double.MaxValue };
            double u0 = 1, v0 = 1, u1 = 0, v1 = 0;
            foreach (RectGeo r in g.Rects)
            {
                double d = DistanceToRect(eye, r);
                if (d > s.CullDistance) continue;
                if (parent != null && !InFrontOf(parent.Geo, r)) continue;
                if (!ProjectRect(vp, r, out double ru0, out double rv0, out double ru1, out double rv1)) continue;
                ru0 = Math.Max(ru0, pu0); rv0 = Math.Max(rv0, pv0); ru1 = Math.Min(ru1, pu1); rv1 = Math.Min(rv1, pv1);
                if (ru1 <= ru0 || rv1 <= rv0) continue;
                if (lineOfSight && !LineOfSight(r, cam, eye)) continue;
                node.Visible.Add(r);
                node.Distance = Math.Min(node.Distance, d);
                u0 = Math.Min(u0, ru0); v0 = Math.Min(v0, rv0); u1 = Math.Max(u1, ru1); v1 = Math.Max(v1, rv1);
            }
            if (node.Visible.Count == 0 || u1 <= u0 || v1 <= v0) return null;
            if (parent != null) node.Distance += 0; // the eye passed in is already the parent's virtual eye
            node.U0 = u0; node.V0 = v0; node.U1 = u1; node.V1 = v1;
            return node;
        }

        private static bool InFrontOf(PlaneGeo g, RectGeo r)
        {
            for (int i = 0; i < 4; i++)
            {
                double s = (i & 1), t = (i >> 1);
                double x = r.C0[0] + r.Eu[0] * s + r.Ev[0] * t;
                double y = r.C0[1] + r.Eu[1] * s + r.Ev[1] * t;
                double z = r.C0[2] + r.Eu[2] * s + r.Ev[2] * t;
                if (g.Nx * x + g.Ny * y + g.Nz * z + g.W > 0.01) return true;
            }
            return false;
        }

        private static double DistanceToRect(double[] p, RectGeo r)
        {
            double sum = 0;
            for (int a = 0; a < 3; a++)
            {
                double lo = r.C0[a] + Math.Min(0, r.Eu[a]) + Math.Min(0, r.Ev[a]);
                double hi = r.C0[a] + Math.Max(0, r.Eu[a]) + Math.Max(0, r.Ev[a]);
                double c = Math.Max(lo, Math.Min(hi, p[a]));
                sum += (p[a] - c) * (p[a] - c);
            }
            return Math.Sqrt(sum);
        }

        /// <summary>Screen-uv bounding box of a rect through vp, clipped at the near side (w > eps).</summary>
        private static bool ProjectRect(double[] vp, RectGeo r, out double u0, out double v0, out double u1, out double v1)
        {
            u0 = v0 = 1; u1 = v1 = 0;
            var poly = new List<double[]>(8);
            for (int i = 0; i < 4; i++)
            {
                double s = (i == 1 || i == 2) ? 1 : 0, t = (i >= 2) ? 1 : 0;
                double x = r.C0[0] + r.Eu[0] * s + r.Ev[0] * t;
                double y = r.C0[1] + r.Eu[1] * s + r.Ev[1] * t;
                double z = r.C0[2] + r.Eu[2] * s + r.Ev[2] * t;
                MirrorMath.Transform(vp, x, y, z, 1, out double cx, out double cy, out double cz, out double cw);
                poly.Add(new[] { cx, cy, cz, cw });
            }
            const double eps = 1e-3;
            var clipped = new List<double[]>(8);
            for (int i = 0; i < poly.Count; i++)
            {
                double[] a = poly[i], b = poly[(i + 1) % poly.Count];
                bool ain = a[3] > eps, bin = b[3] > eps;
                if (ain) clipped.Add(a);
                if (ain != bin)
                {
                    double f = (eps - a[3]) / (b[3] - a[3]);
                    clipped.Add(new[] { a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f, a[2] + (b[2] - a[2]) * f, eps });
                }
            }
            if (clipped.Count < 3) return false;
            double minx = double.MaxValue, miny = double.MaxValue, maxx = double.MinValue, maxy = double.MinValue;
            foreach (double[] c in clipped)
            {
                double nx = c[0] / c[3], ny = c[1] / c[3];
                minx = Math.Min(minx, nx); maxx = Math.Max(maxx, nx);
                miny = Math.Min(miny, ny); maxy = Math.Max(maxy, ny);
            }
            u0 = Math.Max(0, minx * 0.5 + 0.5); u1 = Math.Min(1, maxx * 0.5 + 0.5);
            v0 = Math.Max(0, miny * 0.5 + 0.5); v1 = Math.Min(1, maxy * 0.5 + 0.5);
            return u1 > u0 && v1 > v0;
        }

        /// <summary>
        /// Can the eye see any of this rect through the blocks? A few rays per
        /// rect (more on big ones), through full opaque blocks only, cached for
        /// a few frames and refreshed under a per-frame ray budget, so a mirror
        /// behind a wall costs a few block lookups and no GL at all.
        /// </summary>
        private bool LineOfSight(RectGeo r, Vec3d cam, double[] eye)
        {
            if (_los.TryGetValue(r.Key, out var cached) && _frame - cached.frame < LosRefreshFrames) return cached.visible;
            if (cached.frame != 0 && _losRaysThisFrame >= LosRaysPerFrame) return cached.visible;

            double lu = Math.Sqrt(r.Eu[0] * r.Eu[0] + r.Eu[1] * r.Eu[1] + r.Eu[2] * r.Eu[2]);
            double lv = Math.Sqrt(r.Ev[0] * r.Ev[0] + r.Ev[1] * r.Ev[1] + r.Ev[2] * r.Ev[2]);
            int ku = Math.Max(1, Math.Min(4, (int)Math.Ceiling(lu / 2))), kv = Math.Max(1, Math.Min(4, (int)Math.Ceiling(lv / 2)));
            bool visible = false;
            for (int i = 0; i <= ku && !visible; i++)
            {
                for (int j = 0; j <= kv && !visible; j++)
                {
                    double s = ku == 0 ? 0.5 : (0.02 + 0.96 * i / ku), t = kv == 0 ? 0.5 : (0.02 + 0.96 * j / kv);
                    double[] p = new double[3];
                    for (int a = 0; a < 3; a++) p[a] = r.C0[a] + r.Eu[a] * s + r.Ev[a] * t;
                    _losRaysThisFrame++;
                    if (RayClear(eye, p, cam)) visible = true;
                }
            }
            _los[r.Key] = (visible, _frame);
            return visible;
        }

        private bool RayClear(double[] from, double[] to, Vec3d cam)
        {
            IBlockAccessor ba = _capi.World.BlockAccessor;
            double dx = to[0] - from[0], dy = to[1] - from[1], dz = to[2] - from[2];
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-4) return true;
            // Stop a little short: the end point lies on the mirror's own block.
            double end = Math.Max(0, len - 0.02);
            double step = 0.25;
            int lastX = int.MinValue, lastY = int.MinValue, lastZ = int.MinValue;
            for (double d = 0.3; d < end; d += step)
            {
                double f = d / len;
                int x = (int)Math.Floor(cam.X + from[0] + dx * f);
                int y = (int)Math.Floor(cam.Y + from[1] + dy * f);
                int z = (int)Math.Floor(cam.Z + from[2] + dz * f);
                if (x == lastX && y == lastY && z == lastZ) continue;
                lastX = x; lastY = y; lastZ = z;
                _losPos.Set(x, y, z);
                Block b = ba.GetBlock(_losPos);
                if (b != null && b.Id != 0 && b.AllSidesOpaque) return false;
            }
            return true;
        }

        private void BuildContext(Context ctx, List<Node> children, double[] vp, List<PlaneGeo> alsoMaterialOnly,
                                  Vec3d cam, double[] eye, MirrorSettings s, bool main)
        {
            ctx.Clear();
            ctx.ViewProj = MirrorMath.ToFloat(vp);
            ctx.Eye[0] = (float)eye[0]; ctx.Eye[1] = (float)eye[1]; ctx.Eye[2] = (float)eye[2];
            ctx.EyeValid = true;
            var slots = new List<(PlaneGeo geo, Node node)>();
            foreach (Node c in children) if (slots.Count < MaxSlots) slots.Add((c.Geo, c));
            if (alsoMaterialOnly != null)
            {
                // Mirrors in view but over budget, or out of range, keep their polished material.
                foreach (PlaneGeo g in alsoMaterialOnly.OrderBy(g => g.Rects.Min(r => DistanceToRect(eye, r))))
                {
                    if (slots.Count >= MaxSlots) break;
                    if (slots.Any(x => x.geo == g)) continue;
                    if (g.Nx * eye[0] + g.Ny * eye[1] + g.Nz * eye[2] + g.W <= 0.01) continue;
                    if (g.Rects.Min(r => DistanceToRect(eye, r)) > s.CullDistance * 1.5) continue;
                    slots.Add((g, null));
                }
            }

            int boxes = 0;
            for (int k = 0; k < slots.Count; k++)
            {
                PlaneGeo g = slots[k].geo;
                Node n = slots[k].node;
                ctx.Planes[k * 4 + 0] = (float)g.Nx; ctx.Planes[k * 4 + 1] = (float)g.Ny;
                ctx.Planes[k * 4 + 2] = (float)g.Nz; ctx.Planes[k * 4 + 3] = (float)g.W;
                ctx.Look[k * 4 + 0] = g.Plane.Polish; ctx.Look[k * 4 + 1] = g.Plane.Strength;
                ctx.Look[k * 4 + 2] = s.LodStart; ctx.Look[k * 4 + 3] = s.FineDistance;
                if (n != null)
                {
                    ctx.Atlas[k * 4 + 0] = n.X / (float)AtlasSize; ctx.Atlas[k * 4 + 1] = n.Y / (float)AtlasSize;
                    ctx.Atlas[k * 4 + 2] = n.Wpx / (float)AtlasSize; ctx.Atlas[k * 4 + 3] = n.Hpx / (float)AtlasSize;
                    ctx.Crop[k * 4 + 0] = (float)n.U0; ctx.Crop[k * 4 + 1] = (float)n.V0;
                    ctx.Crop[k * 4 + 2] = (float)n.U1; ctx.Crop[k * 4 + 3] = (float)n.V1;
                }
                IEnumerable<RectGeo> rects = n != null ? n.Visible.Concat(g.Rects.Except(n.Visible)) : g.Rects;
                foreach (RectGeo r in rects)
                {
                    if (boxes >= MaxBoxes) break;
                    int o = boxes * 8;
                    for (int a = 0; a < 3; a++)
                    {
                        double lo = r.C0[a] + Math.Min(0, r.Eu[a]) + Math.Min(0, r.Ev[a]);
                        double hi = r.C0[a] + Math.Max(0, r.Eu[a]) + Math.Max(0, r.Ev[a]);
                        bool normalAxis = a == g.Plane.Axis;
                        double pad = normalAxis ? 0.01 : 0.0005;
                        ctx.Boxes[o + a] = (float)(lo - pad);
                        ctx.Boxes[o + 4 + a] = (float)(hi + pad);
                    }
                    ctx.Boxes[o + 3] = k;
                    ctx.Boxes[o + 7] = 0;
                    boxes++;
                }
            }
            ctx.BoxCount = boxes;
        }

        // --- drawing -----------------------------------------------------------

        private void RenderPasses(float dt, List<Node> passes, double[] proj, double[] cmo, Vec3d cam, MirrorSettings s, EntityPlayer player)
        {
            if (passes.Count == 0) return;
            IRenderAPI r = _capi.Render;
            FrameBufferRef previous = r.CurrentFrameBuffer;
            double[] liveCmo = r.CameraMatrixOrigin;   // the array RenderOpaque reads; overwritten per pass, restored after
            double[] savedCmo = MirrorMath.Copy(liveCmo);
            // The float copy entities and particles read; kept in step with the double one.
            float[] liveCmof = r.CameraMatrixOriginf;
            float[] savedCmof = (float[])liveCmof.Clone();
            int savedViewSq = _culler.ViewDistanceSq;
            BlockPos playerBlock = player.Pos.AsBlockPos;
            double[] toCamRel = Mat4d.Create();
            Mat4d.Translate(toCamRel, toCamRel, -cam.X, -cam.Y, -cam.Z);
            _mainReflectValid = SafeCaptureValid();

            try
            {
                r.CurrentFrameBuffer = _atlas;
                r.GlScissorFlag(false);
                r.ClearFrameBuffer(_atlas, new float[] { 0f, 0f, 0f, 0f }, true, true);
                r.GlScissorFlag(true);

                foreach (Node n in passes)
                {
                    double[] crop = MirrorMath.Crop(n.U0, n.V0, n.U1, n.V1);
                    double[] pCrop = MirrorMath.Mul(crop, proj);
                    double[] mv = MirrorMath.Mul(cmo, n.T);
                    double[] cView = MirrorMath.TransformPlane(mv, n.Geo.Nx, n.Geo.Ny, n.Geo.Nz, n.Geo.W - ClipBias);
                    double[] pFinal = MirrorMath.Oblique(pCrop, cView);

                    r.GlViewport(n.X, n.Y, n.Wpx, n.Hpx);
                    r.GlScissor(n.X, n.Y, n.Wpx, n.Hpx);

                    // Depth: "near" everywhere, "far" on the mirror's own visible rects.
                    _gl.ClearDepthTo(0.0);
                    DrawMask(n, pCrop, n.Parent == null ? cmo : MirrorMath.Mul(cmo, n.Parent.T));

                    double range = s.ReflectionRange + n.Distance;
                    _culler.ViewDistanceSq = (int)Math.Min(savedViewSq, range * range + 400);
                    _culler.CalcFrustumEquations(playerBlock, pFinal, MirrorMath.Mul(mv, toCamRel));
                    Array.Copy(mv, liveCmo, 16);
                    for (int i = 0; i < 16; i++) liveCmof[i] = (float)mv[i];
                    r.PMatrix.Push(pFinal);
                    if (n.Odd) _gl.FrontFace(MirrorGl.Cw);
                    _current = n.Ctx;
                    _mirrorPassActive = true;
                    _ranThisFrame = true;
                    try
                    {
                        _oneArg[0] = dt;
                        _renderOpaque.Invoke(_chunkRenderer, _oneArg);
                        if (s.Content >= 2) _content.Draw(s.Content, dt, _culler, range);
                    }
                    finally
                    {
                        _mirrorPassActive = false;
                        _current = _main;
                        _gl.FrontFace(MirrorGl.Ccw);
                        r.PMatrix.Pop();
                        Array.Copy(savedCmo, liveCmo, 16);
                        Array.Copy(savedCmof, liveCmof, 16);
                    }
                }
            }
            finally
            {
                _mirrorPassActive = false;
                _current = _main;
                Array.Copy(savedCmo, liveCmo, 16);
                Array.Copy(savedCmof, liveCmof, 16);
                _culler.ViewDistanceSq = savedViewSq;
                _culler.CalcFrustumEquations(playerBlock, _gamePerspectiveProj, _gamePerspectiveView);
                try { _gl.DepthFunc(MirrorGl.Less); _gl.FrontFace(MirrorGl.Ccw); } catch { }
                r.GlScissorFlag(false);
                r.GlColorMask(true, true, true, true);
                r.GLDepthMask(true);
                r.GlToggleBlend(true);
                r.GlEnableCullFace();
                r.CurrentFrameBuffer = previous;
                r.GlViewport(0, 0, r.FrameWidth, r.FrameHeight);
            }
        }

        private void DrawMask(Node n, double[] pCrop, double[] parentMv)
        {
            IRenderAPI r = _capi.Render;
            double[] vp = MirrorMath.Mul(pCrop, parentMv);
            r.GlDisableCullFace();
            r.GlColorMask(false, false, false, false);
            r.GLDepthMask(true);
            r.GLEnableDepthTest();
            _gl.DepthFunc(MirrorGl.Always);
            _mask.Use();
            try
            {
                foreach (RectGeo g in n.Visible)
                {
                    double[] a = Mat4d.Create();
                    a[0] = g.Eu[0]; a[1] = g.Eu[1]; a[2] = g.Eu[2];
                    a[4] = g.Ev[0]; a[5] = g.Ev[1]; a[6] = g.Ev[2];
                    a[12] = g.C0[0]; a[13] = g.C0[1]; a[14] = g.C0[2];
                    _mask.UniformMatrix("vv_mvp", MirrorMath.ToFloat(MirrorMath.Mul(vp, a)));
                    r.RenderMesh(_quad);
                }
            }
            finally
            {
                _mask.Stop();
                _gl.DepthFunc(MirrorGl.Less);
                r.GlColorMask(true, true, true, true);
                r.GlEnableCullFace();
            }
        }

        private bool SafeCaptureValid()
        {
            try { return _captureValid != null && _captureValid(); } catch { return false; }
        }

        // --- the per-draw uniform upload (Harmony prefix) ---------------------

        public static void PoolRenderPrefix()
        {
            if (_hookFailed) return;
            try
            {
                IShaderProgram sh = ModSystemCapi?.Render.CurrentActiveShader;
                if (sh == null || !sh.HasUniform(CountUniform)) return;
                Context c = _current ?? Empty;
                sh.Uniform(CountUniform, (float)c.BoxCount);
                if (c.BoxCount > 0)
                {
                    sh.Uniforms4(BoxUniform, MaxBoxes * 2, c.Boxes);
                    sh.Uniforms4(PlaneUniform, MaxSlots, c.Planes);
                    sh.Uniforms4(AtlasUniform, MaxSlots, c.Atlas);
                    sh.Uniforms4(CropUniform, MaxSlots, c.Crop);
                    sh.Uniforms4(LookUniform, MaxSlots, c.Look);
                    sh.UniformMatrix(ViewProjUniform, c.ViewProj);
                }
                if (c.EyeValid && sh.HasUniform("vv_pbrEye"))
                {
                    _eyeScratch.Set(c.Eye[0], c.Eye[1], c.Eye[2]);
                    sh.Uniform("vv_pbrEye", _eyeScratch);
                }
                // The scene capture was taken from the PLAYER's camera; marching
                // it from a mirror camera would paint unrelated pixels. Mirror
                // passes use the analytic sky for anything not in a mirror.
                if (sh.HasUniform("vv_reflectValid"))
                {
                    if (_mirrorPassActive) sh.Uniform("vv_reflectValid", 0f);
                    else if (_ranThisFrame) sh.Uniform("vv_reflectValid", _mainReflectValid ? 1f : 0f);
                }
            }
            catch (Exception ex)
            {
                _hookFailed = true;
                _hookLogger?.Error("[VintageVisuals] mirrors: uniform upload failed, mirror pictures off for this session: " + ex.Message);
            }
        }

        /// <summary>Set by the subsystem; the static hook needs the API.</summary>
        internal static ICoreClientAPI ModSystemCapi { get; set; }
        private static readonly Vec3f _eyeScratch = new Vec3f();

        public void Dispose()
        {
            try { _harmony.UnpatchAll(_harmony.Id); } catch { }
            if (_atlas != null) { _capi.Render.DestroyFrameBuffer(_atlas); _atlas = null; }
            if (_quad != null) { _quad.Dispose(); _quad = null; }
            AtlasTextureId = 0;
            _current = Empty;
            _mirrorPassActive = false;
        }
    }
}
