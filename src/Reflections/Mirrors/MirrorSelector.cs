using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace VintageVisuals.Reflections.Mirrors
{
    /// <summary>
    /// How a player picks which surfaces mirror: look at a surface and press
    /// the add or remove hotkey (or '.vvmirror add/remove'), in one of four modes.
    ///
    ///   face      the selection box face under the cursor (a whole block face,
    ///             or the face of the one chiselled cuboid hit)
    ///   voxel     the single 1/16 voxel face under the cursor
    ///   material  on a chiselled block: every face in this plane made of the
    ///             material under the cursor, in this block and the chiselled
    ///             (or plain) blocks of that material connected to it
    ///   flood     plain blocks: every connected, exposed face of the same block
    ///             in this plane (a marble floor), up to 1024 blocks
    ///   auto      (default) material on a chiselled block, face otherwise
    ///
    /// Chiselled blocks are read through BlockEntityMicroBlock's public fields
    /// (VoxelCuboids, BlockIds) by reflection, so the mod takes no compile-time
    /// dependency on the survival mod; if their shape changes, material mode
    /// falls back to face mode and says so.
    /// </summary>
    public sealed class MirrorSelector
    {
        public const string AddHotkey = "vintagevisuals-mirror-add";
        public const string RemoveHotkey = "vintagevisuals-mirror-remove";
        private const int HighlightSlot = 74219;
        private const int FloodCap = 1024;
        private const int MaterialCap = 256;

        private static readonly string[] Modes = { "auto", "face", "voxel", "material", "flood" };

        private readonly ICoreClientAPI _capi;
        private readonly MirrorStore _store;
        private readonly Func<string> _stats;
        private readonly Action _settingsChanged;
        private string _mode = "auto";

        public MirrorSelector(ICoreClientAPI capi, MirrorStore store, Func<string> stats, Action settingsChanged)
        {
            _capi = capi;
            _store = store;
            _stats = stats;
            _settingsChanged = settingsChanged;
        }

        public void Register()
        {
            try
            {
                _capi.Input.RegisterHotKey(AddHotkey, "Vintage Visuals: make the looked-at surface a mirror",
                    GlKeys.M, HotkeyType.GUIOrOtherControls, ctrlPressed: true, shiftPressed: true);
                _capi.Input.SetHotKeyHandler(AddHotkey, _ => { Say(Apply(true, _mode)); return true; });
                _capi.Input.RegisterHotKey(RemoveHotkey, "Vintage Visuals: remove the looked-at surface from its mirror",
                    GlKeys.N, HotkeyType.GUIOrOtherControls, ctrlPressed: true, shiftPressed: true);
                _capi.Input.SetHotKeyHandler(RemoveHotkey, _ => { Say(Apply(false, _mode)); return true; });
            }
            catch (Exception ex)
            {
                _capi.Logger.Warning("[VintageVisuals] mirrors: hotkeys not registered: " + ex.Message);
            }

            try
            {
                var p = _capi.ChatCommands.Parsers;
                _capi.ChatCommands.Create("vvmirror")
                    .WithDescription("Vintage Visuals mirrors: add, remove, mode, polish, strength, list, clear, clearall, on, off, set, stats")
                    .BeginSubCommand("add").WithDescription("Make the looked-at surface a mirror")
                        .WithArgs(p.OptionalWord("mode"))
                        .HandleWith(a => Result(Apply(true, (a[0] as string) ?? _mode))).EndSubCommand()
                    .BeginSubCommand("remove").WithDescription("Remove the looked-at surface from its mirror")
                        .WithArgs(p.OptionalWord("mode"))
                        .HandleWith(a => Result(Apply(false, (a[0] as string) ?? _mode))).EndSubCommand()
                    .BeginSubCommand("mode").WithDescription("Selection mode for the hotkeys: auto, face, voxel, material, flood")
                        .WithArgs(p.OptionalWord("mode"))
                        .HandleWith(a => SetMode(a[0] as string)).EndSubCommand()
                    .BeginSubCommand("polish").WithDescription("0..1: how mirror-smooth the looked-at mirror is (1 default)")
                        .WithArgs(p.Float("value"))
                        .HandleWith(a => SetLook((float)a[0], true)).EndSubCommand()
                    .BeginSubCommand("strength").WithDescription("0..1: make a non-metal (marble, glass) mirror like metal (0 default = physical)")
                        .WithArgs(p.Float("value"))
                        .HandleWith(a => SetLook((float)a[0], false)).EndSubCommand()
                    .BeginSubCommand("list").HandleWith(_ => List()).EndSubCommand()
                    .BeginSubCommand("clear").WithDescription("Delete the looked-at mirror plane")
                        .HandleWith(_ => ClearLooked()).EndSubCommand()
                    .BeginSubCommand("clearall").WithDescription("Delete every mirror in this world")
                        .HandleWith(_ => { _store.ClearAll(); return TextCommandResult.Success("All mirrors in this world removed."); }).EndSubCommand()
                    .BeginSubCommand("on").HandleWith(_ => SetEnabled(true)).EndSubCommand()
                    .BeginSubCommand("off").HandleWith(_ => SetEnabled(false)).EndSubCommand()
                    .BeginSubCommand("set").WithDescription("cull, lod, res, depth, passes or range, then a value")
                        .WithArgs(p.Word("name"), p.Float("value"))
                        .HandleWith(a => SetSetting(a[0] as string, (float)a[1])).EndSubCommand()
                    .BeginSubCommand("stats").HandleWith(_ => TextCommandResult.Success(_stats())).EndSubCommand();
            }
            catch (Exception ex)
            {
                _capi.Logger.Warning("[VintageVisuals] mirrors: could not register .vvmirror: " + ex.Message);
            }
        }

        private static TextCommandResult Result(string message) { return TextCommandResult.Success(message); }

        private void Say(string message)
        {
            try { _capi.ShowChatMessage(message); } catch { }
        }

        private TextCommandResult SetMode(string mode)
        {
            if (string.IsNullOrEmpty(mode)) return TextCommandResult.Success("Mirror selection mode: " + _mode + " (" + string.Join(", ", Modes) + ")");
            mode = mode.ToLowerInvariant();
            if (!Modes.Contains(mode)) return TextCommandResult.Error("Unknown mode. Use one of: " + string.Join(", ", Modes));
            _mode = mode;
            return TextCommandResult.Success("Mirror selection mode: " + _mode);
        }

        private TextCommandResult SetEnabled(bool on)
        {
            _store.Settings.Enabled = on;
            _store.SaveSettings();
            _settingsChanged();
            return TextCommandResult.Success(on ? "Mirrors on." : "Mirrors off: no mirror camera runs and the mirror shader code is removed.");
        }

        private TextCommandResult SetSetting(string name, float value)
        {
            MirrorSettings s = _store.Settings;
            switch ((name ?? "").ToLowerInvariant())
            {
                case "cull": s.CullDistance = value; break;
                case "lod": s.LodStart = value; break;
                case "res": s.Resolution = value; break;
                case "depth": s.MaxDepth = (int)Math.Round(value); break;
                case "passes": s.MaxPasses = (int)Math.Round(value); break;
                case "range": s.ReflectionRange = value; break;
                case "fine": s.FineDistance = value; break;
                case "content": s.Content = (int)Math.Round(value); break;
                default: return TextCommandResult.Error("Settings: cull, lod, fine, res, depth, passes, range, content");
            }
            s.Clamp();
            _store.SaveSettings();
            _settingsChanged();
            return TextCommandResult.Success(DescribeSettings());
        }

        public string DescribeSettings()
        {
            MirrorSettings s = _store.Settings;
            return "Mirrors " + (s.Enabled ? "on" : "off") + ": cull " + s.CullDistance.ToString("0.#") + " blocks, full texel detail within "
                 + s.LodStart.ToString("0.##") + " blocks, resolution " + s.Resolution.ToString("0.##") + "x, mirrors in mirrors "
                 + (s.MaxDepth > 0 ? "on (depth " + s.MaxDepth + ")" : "off") + ", at most " + s.MaxPasses + " camera passes, reflected world "
                 + s.ReflectionRange.ToString("0") + " blocks deep, half-texel detail within " + s.FineDistance.ToString("0.##")
                 + " blocks, content tier " + s.Content + " (" + (s.Content >= 3 ? "terrain, entities, particles" : s.Content == 2 ? "terrain and entities" : "terrain") + ").";
        }

        private TextCommandResult List()
        {
            if (!_store.WorldLoaded) return TextCommandResult.Error("The world's mirror file is not loaded yet.");
            if (_store.World.Planes.Count == 0) return TextCommandResult.Success("No mirrors in this world. " + DescribeSettings());
            return TextCommandResult.Success(_store.World.Planes.Count + " mirror plane(s):\n"
                + string.Join("\n", _store.World.Planes.Select(p => "  " + p.Describe() + ", polish " + p.Polish.ToString("0.##")
                                                                     + ", strength " + p.Strength.ToString("0.##")))
                + "\n" + DescribeSettings());
        }

        private TextCommandResult SetLook(float value, bool polish)
        {
            MirrorPlane plane = LookedAtPlane();
            if (plane == null) return TextCommandResult.Error("Look at a mirror first.");
            value = Math.Max(0f, Math.Min(1f, value));
            if (polish) plane.Polish = value; else plane.Strength = value;
            _store.Commit();
            return TextCommandResult.Success("Mirror " + plane.Describe() + ": " + (polish ? "polish " : "strength ") + value.ToString("0.##"));
        }

        private TextCommandResult ClearLooked()
        {
            MirrorPlane plane = LookedAtPlane();
            if (plane == null) return TextCommandResult.Error("Look at a mirror first.");
            _store.RemovePlane(plane);
            return TextCommandResult.Success("Removed the mirror " + plane.Describe() + ".");
        }

        private MirrorPlane LookedAtPlane()
        {
            if (!Hit(out BlockSelection sel, out int axis, out int sign, out long c16, out _)) return null;
            return _store.Find(axis, sign, c16);
        }

        // ------------------------------------------------------------------

        private bool Hit(out BlockSelection sel, out int axis, out int sign, out long c16, out int planeLocal)
        {
            sel = _capi.World?.Player?.CurrentBlockSelection;
            axis = 0; sign = 1; c16 = 0; planeLocal = 0;
            if (sel == null || sel.Position == null || sel.Face == null || sel.HitPosition == null) return false;
            Vec3i n = sel.Face.Normali;
            axis = n.X != 0 ? 0 : n.Y != 0 ? 1 : 2;
            sign = (n.X + n.Y + n.Z) > 0 ? 1 : -1;
            planeLocal = (int)Math.Round(Get(sel.HitPosition, axis) * 16.0);
            planeLocal = Math.Max(0, Math.Min(16, planeLocal));
            c16 = (long)Get(sel.Position, axis) * 16 + planeLocal;
            return true;
        }

        /// <summary>Adds or removes the looked-at selection; returns the chat line.</summary>
        public string Apply(bool add, string mode)
        {
            if (!_store.WorldLoaded) _store.TryLoadWorld();
            if (!_store.WorldLoaded) return "Mirrors: this world's mirror file is not available yet.";
            if (!Hit(out BlockSelection sel, out int axis, out int sign, out long c16, out int pl))
                return "Mirrors: look at a block surface first.";

            mode = (mode ?? "auto").ToLowerInvariant();
            if (!Modes.Contains(mode)) return "Mirrors: unknown mode '" + mode + "'. Use " + string.Join(", ", Modes) + ".";

            IBlockAccessor ba = _capi.World.BlockAccessor;
            BlockPos pos = sel.Position;
            bool micro = MicroCuboids(pos) != null;
            if (mode == "auto") mode = micro ? "material" : "face";
            if (mode == "flood" && micro) mode = "material";

            var rects = new List<MirrorRect>();
            var touched = new List<BlockPos>();
            string note = "";

            switch (mode)
            {
                case "voxel":
                {
                    long u = (long)Math.Floor((Get(pos, U(axis)) + Get(sel.HitPosition, U(axis))) * 16.0);
                    long v = (long)Math.Floor((Get(pos, V(axis)) + Get(sel.HitPosition, V(axis))) * 16.0);
                    u = Clamp16(u, Get(pos, U(axis))); v = Clamp16(v, Get(pos, V(axis)));
                    rects.Add(new MirrorRect(u, v, u + 1, v + 1));
                    touched.Add(pos.Copy());
                    break;
                }
                case "material":
                {
                    if (!MaterialSelect(pos, sel, axis, sign, pl, rects, touched))
                    {
                        note = " (no chiselled material found under the cursor; used the face)";
                        FaceSelect(ba, sel, axis, rects, touched);
                    }
                    break;
                }
                case "flood":
                    FloodSelect(ba, pos, axis, sign, pl, rects, touched);
                    break;
                default:
                    FaceSelect(ba, sel, axis, rects, touched);
                    break;
            }

            if (rects.Count == 0) return "Mirrors: nothing selectable there" + note + ".";

            int changed = add ? _store.Add(axis, sign, c16, rects) : _store.Remove(axis, sign, c16, rects);
            Highlight(touched, add);

            MirrorPlane plane = _store.Find(axis, sign, c16);
            string what = (changed / 256.0).ToString("0.##") + " block face(s)";
            if (add)
                return "Mirror: added " + what + " by " + mode + note + ". Plane " + (plane != null ? plane.Describe() : "?")
                     + ". " + _store.World.Planes.Count + " mirror plane(s) in this world.";
            return "Mirror: removed " + what + " by " + mode + note + ". "
                 + (plane != null ? "Plane " + plane.Describe() + "." : "That plane has no mirror left.");
        }

        private void FaceSelect(IBlockAccessor ba, BlockSelection sel, int axis, List<MirrorRect> rects, List<BlockPos> touched)
        {
            BlockPos pos = sel.Position;
            Block block = ba.GetBlock(pos);
            Cuboidf[] boxes = null;
            try { boxes = block?.GetSelectionBoxes(ba, pos); } catch { }
            Cuboidf box = boxes != null && sel.SelectionBoxIndex >= 0 && sel.SelectionBoxIndex < boxes.Length
                ? boxes[sel.SelectionBoxIndex] : Cuboidf.Default();

            int ua = U(axis), va = V(axis);
            long bu = Get(pos, ua) * 16L, bv = Get(pos, va) * 16L;
            rects.Add(new MirrorRect(bu + Round16(BoxMin(box, ua)), bv + Round16(BoxMin(box, va)),
                                     bu + Round16(BoxMax(box, ua)), bv + Round16(BoxMax(box, va))));
            touched.Add(pos.Copy());
        }

        private void FloodSelect(IBlockAccessor ba, BlockPos start, int axis, int sign, int pl, List<MirrorRect> rects, List<BlockPos> touched)
        {
            Block target = ba.GetBlock(start);
            if (target == null || target.Id == 0) return;
            if (pl != (sign > 0 ? 16 : 0)) return; // flood works on whole block faces only
            int ua = U(axis), va = V(axis);

            var seen = new HashSet<long>();
            var queue = new Queue<BlockPos>();
            queue.Enqueue(start.Copy());
            seen.Add(Key(start));

            while (queue.Count > 0 && touched.Count < FloodCap)
            {
                BlockPos p = queue.Dequeue();
                Block b = ba.GetBlock(p);
                if (b == null || b.Id != target.Id) continue;

                BlockPos front = p.Copy(); Add(front, axis, sign);
                Block fb = ba.GetBlock(front);
                if (fb != null && fb.Id != 0 && fb.AllSidesOpaque) continue; // buried face

                long bu = Get(p, ua) * 16L, bv = Get(p, va) * 16L;
                rects.Add(new MirrorRect(bu, bv, bu + 16, bv + 16));
                touched.Add(p);

                foreach (var (du, dv) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    BlockPos q = p.Copy(); Add(q, ua, du); Add(q, va, dv);
                    if (seen.Add(Key(q))) queue.Enqueue(q);
                }
            }
        }

        private struct Cub { public int X0, Y0, Z0, X1, Y1, Z1, BlockId; }

        private bool MaterialSelect(BlockPos start, BlockSelection sel, int axis, int sign, int pl, List<MirrorRect> rects, List<BlockPos> touched)
        {
            List<Cub> cubs = MicroCuboids(start);
            if (cubs == null) return false;

            // The voxel the cursor is on: half a voxel behind the hit point.
            int[] v = new int[3];
            for (int i = 0; i < 3; i++)
            {
                double c = Get(sel.HitPosition, i) * 16.0;
                if (i == axis) c -= sign * 0.5;
                v[i] = Math.Max(0, Math.Min(15, (int)Math.Floor(c)));
            }
            int targetId = -1;
            foreach (Cub c in cubs)
            {
                if (v[0] >= c.X0 && v[0] < c.X1 && v[1] >= c.Y0 && v[1] < c.Y1 && v[2] >= c.Z0 && v[2] < c.Z1) { targetId = c.BlockId; break; }
            }
            if (targetId < 0) return false;

            IBlockAccessor ba = _capi.World.BlockAccessor;
            int ua = U(axis), va = V(axis);
            var seen = new HashSet<long>();
            var queue = new Queue<BlockPos>();
            queue.Enqueue(start.Copy());
            seen.Add(Key(start));
            bool any = false;

            while (queue.Count > 0 && touched.Count < MaterialCap)
            {
                BlockPos p = queue.Dequeue();
                long bu = Get(p, ua) * 16L, bv = Get(p, va) * 16L;
                var here = new List<MirrorRect>();
                List<Cub> pc = MicroCuboids(p);
                if (pc != null)
                {
                    var cover = new List<MirrorRect>();
                    int frontLayer = sign > 0 ? pl : pl - 1;
                    foreach (Cub c in pc)
                    {
                        int c0 = Lo(c, axis), c1 = Hi(c, axis);
                        if (c.BlockId == targetId && (sign > 0 ? c1 == pl : c0 == pl))
                            here.Add(new MirrorRect(bu + Lo(c, ua), bv + Lo(c, va), bu + Hi(c, ua), bv + Hi(c, va)));
                        if (frontLayer >= 0 && frontLayer < 16 && c0 <= frontLayer && frontLayer < c1)
                            cover.Add(new MirrorRect(bu + Lo(c, ua), bv + Lo(c, va), bu + Hi(c, ua), bv + Hi(c, va)));
                    }
                    if (here.Count > 0) here = MirrorStore.Normalize(here, cover);
                }
                else
                {
                    Block b = ba.GetBlock(p);
                    if (b != null && b.Id == targetId && pl == (sign > 0 ? 16 : 0))
                        here.Add(new MirrorRect(bu, bv, bu + 16, bv + 16));
                }

                if (here.Count == 0 && !(p.X == start.X && p.Y == start.Y && p.Z == start.Z)) continue;
                rects.AddRange(here);
                if (here.Count > 0) { any = true; touched.Add(p); }

                foreach (var (du, dv) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    BlockPos q = p.Copy(); Add(q, ua, du); Add(q, va, dv);
                    if (seen.Add(Key(q))) queue.Enqueue(q);
                }
            }
            return any;
        }

        private static FieldInfo _cuboidsField, _idsField;
        private static Type _microType;
        private static bool _reportedMicro;

        private List<Cub> MicroCuboids(BlockPos pos)
        {
            object be;
            try { be = _capi.World.BlockAccessor.GetBlockEntity(pos); } catch { return null; }
            if (be == null) return null;
            Type t = be.GetType();
            if (t != _microType)
            {
                FieldInfo cf = t.GetField("VoxelCuboids", BindingFlags.Public | BindingFlags.Instance);
                FieldInfo idf = t.GetField("BlockIds", BindingFlags.Public | BindingFlags.Instance);
                if (cf == null || idf == null || idf.FieldType != typeof(int[])) return null;
                _microType = t; _cuboidsField = cf; _idsField = idf;
            }
            try
            {
                var list = _cuboidsField.GetValue(be) as IList;
                int[] ids = _idsField.GetValue(be) as int[];
                if (list == null || ids == null) return null;
                var result = new List<Cub>(list.Count);
                foreach (object o in list)
                {
                    uint val = (uint)o;
                    int mat = (int)((val >> 24) & 0xFF);
                    result.Add(new Cub
                    {
                        X0 = (int)(val & 0xF), Y0 = (int)((val >> 4) & 0xF), Z0 = (int)((val >> 8) & 0xF),
                        X1 = (int)((val >> 12) & 0xF) + 1, Y1 = (int)((val >> 16) & 0xF) + 1, Z1 = (int)((val >> 20) & 0xF) + 1,
                        BlockId = mat < ids.Length ? ids[mat] : -1,
                    });
                }
                return result;
            }
            catch (Exception ex)
            {
                if (!_reportedMicro)
                {
                    _reportedMicro = true;
                    _capi.Logger.Warning("[VintageVisuals] mirrors: could not read a chiselled block (" + ex.Message + "); material mode uses the face instead.");
                }
                return null;
            }
        }

        private void Highlight(List<BlockPos> blocks, bool add)
        {
            try
            {
                IPlayer player = _capi.World.Player;
                int colour = add ? ColorUtil.ToRgba(90, 120, 200, 255) : ColorUtil.ToRgba(90, 255, 120, 80);
                _capi.World.HighlightBlocks(player, HighlightSlot, blocks.Take(512).ToList(),
                    Enumerable.Repeat(colour, Math.Min(512, blocks.Count)).ToList());
                _capi.Event.RegisterCallback(_ =>
                {
                    try { _capi.World.HighlightBlocks(player, HighlightSlot, new List<BlockPos>()); } catch { }
                }, 1500);
            }
            catch { }
        }

        // --- axis helpers ----------------------------------------------------

        public static int U(int axis) { return axis == 0 ? 1 : 0; }
        public static int V(int axis) { return axis == 2 ? 1 : 2; }

        private static int Get(BlockPos p, int axis) { return axis == 0 ? p.X : axis == 1 ? p.Y : p.Z; }
        private static double Get(Vec3d p, int axis) { return axis == 0 ? p.X : axis == 1 ? p.Y : p.Z; }
        private static void Add(BlockPos p, int axis, int d) { if (axis == 0) p.X += d; else if (axis == 1) p.Y += d; else p.Z += d; }
        private static long Key(BlockPos p) { return ((long)(p.X & 0x3FFFFF) << 42) | ((long)(p.Y & 0xFFFFF) << 22) | (long)(p.Z & 0x3FFFFF); }
        private static float BoxMin(Cuboidf b, int axis) { return axis == 0 ? b.X1 : axis == 1 ? b.Y1 : b.Z1; }
        private static float BoxMax(Cuboidf b, int axis) { return axis == 0 ? b.X2 : axis == 1 ? b.Y2 : b.Z2; }
        private static long Round16(float f) { return (long)Math.Round(Math.Max(0f, Math.Min(1f, f)) * 16f); }
        private static long Clamp16(long cell, int blockCoord) { return Math.Max(blockCoord * 16L, Math.Min(blockCoord * 16L + 15, cell)); }
        private static int Lo(Cub c, int axis) { return axis == 0 ? c.X0 : axis == 1 ? c.Y0 : c.Z0; }
        private static int Hi(Cub c, int axis) { return axis == 0 ? c.X1 : axis == 1 ? c.Y1 : c.Z1; }
    }
}
