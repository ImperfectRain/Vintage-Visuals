using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;

namespace VintageVisuals.Reflections.Mirrors
{
    /// <summary>
    /// A rectangle on a mirror plane, in sixteenths of a block (one voxel),
    /// max exclusive. U and V are the plane's two in-plane world axes in
    /// increasing order: X planes use (Y, Z), Y planes (X, Z), Z planes (X, Y).
    /// </summary>
    public sealed class MirrorRect
    {
        public long U0 { get; set; }
        public long V0 { get; set; }
        public long U1 { get; set; }
        public long V1 { get; set; }

        public MirrorRect() { }

        public MirrorRect(long u0, long v0, long u1, long v1)
        {
            U0 = Math.Min(u0, u1); U1 = Math.Max(u0, u1);
            V0 = Math.Min(v0, v1); V1 = Math.Max(v0, v1);
        }

        public bool Empty { get { return U1 <= U0 || V1 <= V0; } }
        public long Area { get { return Math.Max(0, U1 - U0) * Math.Max(0, V1 - V0); } }
    }

    /// <summary>
    /// One mirror: every selected surface that lies in the same world plane and
    /// faces the same way. All of it is drawn by ONE virtual camera, whether it
    /// is a single chiselled inlay or a whole marble floor spread over many
    /// blocks - the reflection across a plane does not depend on which part of
    /// the plane you look at, so a second camera would only draw the same image
    /// twice.
    /// </summary>
    public sealed class MirrorPlane
    {
        /// <summary>0 X, 1 Y, 2 Z: the axis the plane's normal lies along.</summary>
        public int Axis { get; set; }

        /// <summary>+1 or -1: which way the reflecting side faces along that axis.</summary>
        public int Sign { get; set; }

        /// <summary>The plane's position along its axis, in sixteenths of a block.</summary>
        public long C16 { get; set; }

        /// <summary>0..1: how far toward a perfect polish the surface's own roughness is taken.</summary>
        public float Polish { get; set; } = 1f;

        /// <summary>0..1: raises the metalness floor so a dielectric (marble, glass) mirrors like a metal.
        /// 0 keeps the material's own physics: gold and silver mirror, marble only at a glancing view.</summary>
        public float Strength { get; set; } = 0f;

        public List<MirrorRect> Rects { get; set; } = new List<MirrorRect>();

        public string Key { get { return KeyOf(Axis, Sign, C16); } }

        public static string KeyOf(int axis, int sign, long c16)
        {
            return axis + (sign > 0 ? "+" : "-") + c16;
        }

        public string Describe()
        {
            string axis = Axis == 0 ? "X" : Axis == 1 ? "Y" : "Z";
            return axis + "=" + (C16 / 16.0).ToString("0.####") + " facing " + (Sign > 0 ? "+" : "-") + axis
                 + ", " + Rects.Count + " rect(s), " + (Rects.Sum(r => r.Area) / 256.0).ToString("0.##") + " block faces";
        }
    }

    /// <summary>The per-world file.</summary>
    public sealed class MirrorWorldFile
    {
        public int Version { get; set; } = 1;
        public List<MirrorPlane> Planes { get; set; } = new List<MirrorPlane>();
    }

    /// <summary>Player-tunable mirror settings, one file for every world.</summary>
    public sealed class MirrorSettings
    {
        /// <summary>Master switch. Off removes the mirror shader group, so off is the material path without it.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Beyond this many blocks from the eye a mirror's camera does not run at all.</summary>
        public float CullDistance { get; set; } = 48f;

        /// <summary>Within this many blocks a mirror shows one colour per texture texel; each doubling of
        /// the distance after it doubles the cell (2x2, 4x4, 8x8 texels ...).</summary>
        public float LodStart { get; set; } = 2f;

        /// <summary>Capture pixels per reflected cell at the mirror's nearest point. 1 is exactly enough
        /// head-on; above 1 covers foreshortening on floors seen at a low angle.</summary>
        public float Resolution { get; set; } = 2f;

        /// <summary>0: mirrors do not show other mirrors. 1: a mirror seen in a mirror reflects too.</summary>
        public int MaxDepth { get; set; } = 1;

        /// <summary>Hard cap on mirror camera passes per frame, across all depths.</summary>
        public int MaxPasses { get; set; } = 4;

        /// <summary>How far past the mirror the reflected world is drawn, in blocks.</summary>
        public float ReflectionRange { get; set; } = 64f;

        /// <summary>Within this many blocks a reflected cell is HALF a texture texel (each texel split
        /// into 2x2), for a mirror seen from right next to it. Only the reflected picture is finer; every
        /// other texel effect keeps the texture's own grid. 0 turns it off.</summary>
        public float FineDistance { get; set; } = 1.5f;

        /// <summary>What a mirror shows: 1 terrain (blocks, topsoil, plants that are blocks) and the
        /// analytic sky; 2 adds entities, other players and the player themselves; 3 adds particles.</summary>
        public int Content { get; set; } = 1;

        public void Clamp()
        {
            CullDistance = Math.Max(4f, Math.Min(256f, CullDistance));
            LodStart = Math.Max(0.25f, Math.Min(32f, LodStart));
            Resolution = Math.Max(0.25f, Math.Min(8f, Resolution));
            MaxDepth = Math.Max(0, Math.Min(2, MaxDepth));
            MaxPasses = Math.Max(1, Math.Min(8, MaxPasses));
            ReflectionRange = Math.Max(8f, Math.Min(256f, ReflectionRange));
            FineDistance = Math.Max(0f, Math.Min(Math.Min(LodStart, 8f), FineDistance));
            Content = Math.Max(1, Math.Min(3, Content));
        }
    }

    /// <summary>
    /// The mirrors of the current world and the settings, with persistence.
    /// Client side only: mirrors are a visual choice of this player, stored in
    /// their ModConfig folder per savegame, never sent to a server.
    /// </summary>
    public sealed class MirrorStore
    {
        public const string SettingsFile = "vintagevisuals-mirrors.json";

        private readonly ICoreClientAPI _capi;
        private string _worldFile;

        public MirrorSettings Settings { get; private set; } = new MirrorSettings();
        public MirrorWorldFile World { get; private set; } = new MirrorWorldFile();
        public bool WorldLoaded { get { return _worldFile != null; } }

        /// <summary>Bumped on every change, so the renderer can rebuild what it derived.</summary>
        public int Revision { get; private set; }

        public event Action Changed;

        public MirrorStore(ICoreClientAPI capi)
        {
            _capi = capi;
        }

        public bool HasMirrors { get { return World.Planes.Any(p => p.Rects.Count > 0); } }

        public void LoadSettings()
        {
            try
            {
                MirrorSettings loaded = _capi.LoadModConfig<MirrorSettings>(SettingsFile);
                Settings = loaded ?? new MirrorSettings();
            }
            catch (Exception ex)
            {
                _capi.Logger.Warning("[VintageVisuals] mirrors: settings file unreadable, using defaults: " + ex.Message);
                Settings = new MirrorSettings();
            }
            Settings.Clamp();
            SaveSettings();
        }

        public void SaveSettings()
        {
            try { _capi.StoreModConfig(Settings, SettingsFile); }
            catch (Exception ex) { _capi.Logger.Warning("[VintageVisuals] mirrors: could not save settings: " + ex.Message); }
        }

        /// <summary>Loads this savegame's mirrors once the world is known. Returns true if it loaded now.</summary>
        public bool TryLoadWorld()
        {
            if (_worldFile != null) return false;
            string id = null;
            try { id = _capi.World?.SavegameIdentifier; } catch { }
            if (string.IsNullOrEmpty(id)) return false;

            string safe = new string(id.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
            _worldFile = "vintagevisuals-mirrors-" + safe + ".json";
            try
            {
                World = _capi.LoadModConfig<MirrorWorldFile>(_worldFile) ?? new MirrorWorldFile();
            }
            catch (Exception ex)
            {
                _capi.Logger.Warning("[VintageVisuals] mirrors: " + _worldFile + " unreadable, starting empty: " + ex.Message);
                World = new MirrorWorldFile();
            }
            World.Planes.RemoveAll(p => p.Rects == null || p.Rects.Count == 0);
            foreach (MirrorPlane plane in World.Planes) plane.Rects = Normalize(plane.Rects, null);
            Revision++;
            _capi.Logger.Notification("[VintageVisuals] mirrors: " + World.Planes.Count + " mirror plane(s) in this world (" + _worldFile + ").");
            return true;
        }

        private void SaveWorld()
        {
            if (_worldFile == null) return;
            try { _capi.StoreModConfig(World, _worldFile); }
            catch (Exception ex) { _capi.Logger.Warning("[VintageVisuals] mirrors: could not save " + _worldFile + ": " + ex.Message); }
        }

        public MirrorPlane Find(int axis, int sign, long c16)
        {
            string key = MirrorPlane.KeyOf(axis, sign, c16);
            return World.Planes.FirstOrDefault(p => p.Key == key);
        }

        /// <summary>Adds rects to the plane (creating it), merged into as few rects as the union allows.</summary>
        public int Add(int axis, int sign, long c16, IEnumerable<MirrorRect> rects)
        {
            List<MirrorRect> adds = rects.Where(r => !r.Empty).ToList();
            if (adds.Count == 0) return 0;

            MirrorPlane plane = Find(axis, sign, c16);
            if (plane == null)
            {
                plane = new MirrorPlane { Axis = axis, Sign = sign > 0 ? 1 : -1, C16 = c16 };
                World.Planes.Add(plane);
            }

            long before = plane.Rects.Sum(r => r.Area);
            plane.Rects = Normalize(plane.Rects.Concat(adds), null);
            long gained = plane.Rects.Sum(r => r.Area) - before;
            Commit();
            return (int)Math.Min(int.MaxValue, gained);
        }

        /// <summary>Removes rects from the plane; drops the plane when nothing is left.</summary>
        public int Remove(int axis, int sign, long c16, IEnumerable<MirrorRect> rects)
        {
            MirrorPlane plane = Find(axis, sign, c16);
            if (plane == null) return 0;
            long before = plane.Rects.Sum(r => r.Area);
            plane.Rects = Normalize(plane.Rects, rects.Where(r => !r.Empty).ToList());
            long lost = before - plane.Rects.Sum(r => r.Area);
            if (plane.Rects.Count == 0) World.Planes.Remove(plane);
            Commit();
            return (int)Math.Min(int.MaxValue, lost);
        }

        public bool RemovePlane(MirrorPlane plane)
        {
            bool removed = World.Planes.Remove(plane);
            if (removed) Commit();
            return removed;
        }

        public void ClearAll()
        {
            World.Planes.Clear();
            Commit();
        }

        public void Commit()
        {
            Revision++;
            SaveWorld();
            Changed?.Invoke();
        }

        /// <summary>
        /// The union of <paramref name="adds"/> minus <paramref name="subtracts"/>,
        /// as a small set of non-overlapping rects.
        ///
        /// Coordinate compression on every rect edge, a boolean grid over the
        /// compressed cells, then greedy rectangles: grow along U, then along V
        /// while the whole row matches. Exact (all edges are voxel multiples),
        /// and small: the grid is at most (2N)^2 cells for N rects, and runs
        /// only when the player edits a mirror.
        /// </summary>
        public static List<MirrorRect> Normalize(IEnumerable<MirrorRect> adds, IList<MirrorRect> subtracts)
        {
            List<MirrorRect> a = adds.Where(r => r != null && !r.Empty).ToList();
            if (a.Count == 0) return new List<MirrorRect>();
            List<MirrorRect> s = subtracts == null ? new List<MirrorRect>() : subtracts.Where(r => r != null && !r.Empty).ToList();

            long[] us = a.Concat(s).SelectMany(r => new[] { r.U0, r.U1 }).Distinct().OrderBy(x => x).ToArray();
            long[] vs = a.Concat(s).SelectMany(r => new[] { r.V0, r.V1 }).Distinct().OrderBy(x => x).ToArray();
            int nu = us.Length - 1, nv = vs.Length - 1;
            if (nu <= 0 || nv <= 0) return new List<MirrorRect>();

            var grid = new bool[nu, nv];
            foreach (MirrorRect r in a) Mark(grid, us, vs, r, true);
            foreach (MirrorRect r in s) Mark(grid, us, vs, r, false);

            var result = new List<MirrorRect>();
            for (int j = 0; j < nv; j++)
            {
                for (int i = 0; i < nu; i++)
                {
                    if (!grid[i, j]) continue;
                    int i1 = i;
                    while (i1 + 1 < nu && grid[i1 + 1, j]) i1++;
                    int j1 = j;
                    while (j1 + 1 < nv)
                    {
                        bool full = true;
                        for (int k = i; k <= i1; k++) if (!grid[k, j1 + 1]) { full = false; break; }
                        if (!full) break;
                        j1++;
                    }
                    for (int jj = j; jj <= j1; jj++)
                        for (int ii = i; ii <= i1; ii++) grid[ii, jj] = false;
                    result.Add(new MirrorRect(us[i], vs[j], us[i1 + 1], vs[j1 + 1]));
                }
            }
            return result;
        }

        private static void Mark(bool[,] grid, long[] us, long[] vs, MirrorRect r, bool value)
        {
            int i0 = Array.BinarySearch(us, r.U0), i1 = Array.BinarySearch(us, r.U1);
            int j0 = Array.BinarySearch(vs, r.V0), j1 = Array.BinarySearch(vs, r.V1);
            for (int j = j0; j < j1; j++)
                for (int i = i0; i < i1; i++) grid[i, j] = value;
        }
    }
}
