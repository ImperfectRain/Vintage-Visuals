using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

using Vintagestory.API.MathTools;
using Vintagestory.API.Common.Entities;

namespace VintageVisuals.Common.Diagnostics.Profiling
{
    /// <summary>One saved bench scene: where to stand, where to look, what hour and weather.</summary>
    public sealed class BenchScene
    {
        public string Name { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public float Yaw { get; set; }
        public float Pitch { get; set; }

        /// <summary>Hour of day, 0..24; negative = leave the time alone.</summary>
        public double Hour { get; set; } = -1;

        /// <summary>Rain/snow intensity to hold, 0..1; negative = leave the weather to the game.</summary>
        public float Precipitation { get; set; } = -1;

        /// <summary>Wind pattern code for '/weather setw' ("still"), or empty to leave the wind alone.</summary>
        public string Wind { get; set; } = "";

        public string Note { get; set; } = "";
        public string SavedAt { get; set; } = "";
    }

    /// <summary>
    /// D99. Saved bench scenes, one file per world in ModConfig
    /// (vintagevisuals-scenes-&lt;world&gt;.json), so the bench scenes of
    /// docs/VISUAL-TESTS.md ("F" forest at 17:00, "P" pond at noon, ...) are
    /// one command each and a suite can run them unattended.
    ///
    /// Going to a scene sends the game's own server commands, which needs the
    /// tp, time and controlserver privileges (a singleplayer world has them):
    ///   /tp s[] =x =y =z           absolute position (the '=' prefix)
    ///   /time set HH:mm, /time stop
    ///   /weather acp false, /weather setprecip p, /weather setw wind
    /// and sets the camera yaw and pitch on the client. <see cref="Release"/>
    /// undoes the holds (/time resume, /weather setprecipa, /weather acp true)
    /// when the bench ends.
    /// </summary>
    public sealed class BenchScenes
    {
        private readonly ICoreClientAPI _capi;
        private readonly ILogger _logger;
        private List<BenchScene> _scenes;
        private bool _holding;

        public BenchScenes(ICoreClientAPI capi, ILogger logger)
        {
            _capi = capi;
            _logger = logger;
        }

        private string FileName
        {
            get
            {
                string world = "world";
                try { world = _capi.World.SavegameIdentifier ?? world; } catch { }
                var safe = new string(world.Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '_').ToArray());
                return "vintagevisuals-scenes-" + safe + ".json";
            }
        }

        public IReadOnlyList<BenchScene> All { get { Load(); return _scenes; } }

        public BenchScene Find(string name)
        {
            Load();
            return _scenes.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        private void Load()
        {
            if (_scenes != null) return;
            try { _scenes = _capi.LoadModConfig<List<BenchScene>>(FileName); }
            catch (Exception ex) { _logger.Warning("[VintageVisuals] could not read " + FileName + ": " + ex.Message); }
            if (_scenes == null) _scenes = new List<BenchScene>();
        }

        private void Store()
        {
            try { _capi.StoreModConfig(_scenes, FileName); }
            catch (Exception ex) { _logger.Warning("[VintageVisuals] could not write " + FileName + ": " + ex.Message); }
        }

        /// <summary>Saves where the player stands now, with the current hour and rain held.</summary>
        public BenchScene Save(string name, float precipitation, string wind, string note)
        {
            Load();
            EntityPos pos = _capi.World.Player.Entity.Pos;
            var s = new BenchScene
            {
                Name = name,
                X = Math.Round(pos.X, 2),
                Y = Math.Round(pos.Y, 2),
                Z = Math.Round(pos.Z, 2),
                Yaw = _capi.World.Player.CameraYaw,
                Pitch = _capi.World.Player.CameraPitch,
                Hour = Math.Round(_capi.World.Calendar.HourOfDay, 2),
                Precipitation = precipitation,
                Wind = wind ?? "",
                Note = note ?? "",
                SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            };
            _scenes.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            _scenes.Add(s);
            Store();
            return s;
        }

        public bool Delete(string name)
        {
            Load();
            int n = _scenes.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (n > 0) Store();
            return n > 0;
        }

        /// <summary>Teleports, sets the hour and weather, and freezes time. Returns the commands sent.</summary>
        public List<string> Go(BenchScene s)
        {
            var sent = new List<string>();
            void Cmd(string c) { sent.Add(c); _capi.SendChatMessage(c); }

            Cmd("/tp s[] =" + F(s.X) + " =" + F(s.Y) + " =" + F(s.Z));
            if (s.Hour >= 0)
            {
                int h = (int)Math.Floor(s.Hour) % 24;
                int m = (int)Math.Round((s.Hour - Math.Floor(s.Hour)) * 60) % 60;
                Cmd("/time set " + h.ToString("00", CultureInfo.InvariantCulture) + ":" + m.ToString("00", CultureInfo.InvariantCulture));
                Cmd("/time stop");
            }
            if (s.Precipitation >= 0)
            {
                Cmd("/weather acp false");
                Cmd("/weather setprecip " + F(s.Precipitation));
            }
            if (!string.IsNullOrEmpty(s.Wind)) Cmd("/weather setw " + s.Wind);
            _holding = true;
            Look(s);
            return sent;
        }

        /// <summary>Points the camera; called again after the teleport lands, since the server move can reset it.</summary>
        public void Look(BenchScene s)
        {
            try
            {
                _capi.World.Player.CameraYaw = s.Yaw;
                _capi.World.Player.CameraPitch = s.Pitch;
            }
            catch { }
        }

        /// <summary>True once the player stands within a block of the scene.</summary>
        public bool Arrived(BenchScene s)
        {
            try
            {
                EntityPos p = _capi.World.Player.Entity.Pos;
                return Math.Abs(p.X - s.X) < 1.5 && Math.Abs(p.Y - s.Y) < 2.5 && Math.Abs(p.Z - s.Z) < 1.5;
            }
            catch { return false; }
        }

        /// <summary>The chunk column the player stands in is loaded.</summary>
        public bool ChunkLoaded()
        {
            try
            {
                BlockPos bp = _capi.World.Player.Entity.Pos.AsBlockPos;
                return _capi.World.BlockAccessor.GetChunkAtBlockPos(bp) != null;
            }
            catch { return false; }
        }

        /// <summary>Lets time and weather run again after a scene held them.</summary>
        public void Release()
        {
            if (!_holding) return;
            _holding = false;
            _capi.SendChatMessage("/time resume");
            _capi.SendChatMessage("/weather setprecipa");
            _capi.SendChatMessage("/weather acp true");
        }

        public static string Describe(BenchScene s)
        {
            return s.Name + ": " + F(s.X) + ", " + F(s.Y) + ", " + F(s.Z) +
                   (s.Hour >= 0 ? " at " + s.Hour.ToString("0.0", CultureInfo.InvariantCulture) + " h" : "") +
                   (s.Precipitation >= 0 ? ", rain " + F(s.Precipitation) : "") +
                   (string.IsNullOrEmpty(s.Wind) ? "" : ", wind " + s.Wind) +
                   (string.IsNullOrEmpty(s.Note) ? "" : " - " + s.Note);
        }

        private static string F(double v) { return v.ToString("0.##", CultureInfo.InvariantCulture); }
    }

    /// <summary>
    /// Frame-time stability, for "wait until chunks finished loading": the
    /// median of the last second within 4 % of the second before it, with no
    /// frame over three times the median (a chunk upload spike).
    /// </summary>
    public sealed class FrameSettleProbe
    {
        private readonly Queue<double> _frames = new Queue<double>();

        public void Push(double ms)
        {
            _frames.Enqueue(ms);
            while (_frames.Count > 240) _frames.Dequeue();
        }

        public bool Stable(double fps)
        {
            int perSecond = (int)Math.Max(20, Math.Min(120, fps));
            if (_frames.Count < perSecond * 2) return false;
            double[] all = _frames.ToArray();
            double[] a = all.Skip(all.Length - perSecond * 2).Take(perSecond).OrderBy(x => x).ToArray();
            double[] b = all.Skip(all.Length - perSecond).OrderBy(x => x).ToArray();
            double ma = a[a.Length / 2], mb = b[b.Length / 2];
            if (ma <= 0 || mb <= 0) return false;
            if (Math.Abs(ma - mb) / ma > 0.04) return false;
            return b.Last() < mb * 3;
        }
    }
}
