using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;


namespace VintageVisuals.Common.Diagnostics.Profiling
{
    /// <summary>
    /// D99. Bench results on disk, in VintagestoryData/vv-bench/:
    ///
    ///   &lt;date&gt;-&lt;build&gt;-runs.csv     one row per recorded run, with every
    ///                                   game setting that decides comparability,
    ///                                   GPU clock/temperature/power, CPU spans and
    ///                                   the GPU time of every profiler segment
    ///   &lt;date&gt;-&lt;build&gt;-summary.csv  one row per variant of each plan: the
    ///                                   median paired cost and its spread
    ///
    /// Append-only, so several benches on one day and build land in one file
    /// pair. Compare two builds with tools/benchcompare/benchcompare.py. The
    /// header is fixed; segment and span figures go into two key=value columns
    /// so a new segment never shifts a column.
    /// </summary>
    public sealed class BenchResultsFile
    {
        public string Directory { get; private set; }
        public string RunsPath { get; private set; }
        public string SummaryPath { get; private set; }
        private readonly ILogger _logger;
        private bool _failed;

        public const string RunsHeader =
            "time,build,bench,kind,subject,system,variant_key,variant,round,seconds,frames," +
            "frame_avg,frame_p50,frame_p95,frame_p99,gpu_frame,bound," +
            "mod_cpu_ms,mod_alloc_kb,gc0,gc1,gc2," +
            "gpu_core_mhz,gpu_core_min,gpu_core_max,gpu_mem_mhz,gpu_temp_max,gpu_power_w,gpu_util,gpu_throttle," +
            "quality,scene,position,hour,width,height,render_scale,view_distance,shadows,ssao,godrays,vsync,max_fps,gpu," +
            "segments_gpu,segments_cpu,cpu_spans";

        public const string SummaryHeader =
            "time,build,bench,kind,subject,system,variant_key,variant,runs,bound," +
            "frame_median,gpu_frame_median,p99_median,opaque_median,mod_cpu_median," +
            "cost_ms,cost_min,cost_max,gpu_cost_ms,gpu_cost_min,gpu_cost_max,mod_cpu_cost_ms,noise_ms,significant," +
            "quality,scene,note";

        private BenchResultsFile(ILogger logger) { _logger = logger; }

        public static BenchResultsFile Open(ICoreClientAPI capi, ILogger logger, BenchContext ctx)
        {
            var f = new BenchResultsFile(logger);
            try
            {
                f.Directory = capi.GetOrCreateDataPath("vv-bench");
                string stem = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "-" + Safe(ctx.Build);
                f.RunsPath = Path.Combine(f.Directory, stem + "-runs.csv");
                f.SummaryPath = Path.Combine(f.Directory, stem + "-summary.csv");
                if (!File.Exists(f.RunsPath)) File.WriteAllText(f.RunsPath, RunsHeader + "\n");
                if (!File.Exists(f.SummaryPath)) File.WriteAllText(f.SummaryPath, SummaryHeader + "\n");
            }
            catch (Exception ex)
            {
                logger.Warning("[VintageVisuals] bench: cannot write results files (" + ex.Message + "); results stay in the log.");
                f._failed = true;
            }
            return f;
        }

        public string BenchLabel { get; set; }

        public void AppendRun(BenchContext c, BenchRun r)
        {
            if (_failed) return;
            var cells = new List<string>
            {
                DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), c.Build, BenchLabel,
                r.Plan.Kind, r.Plan.Subject, r.Plan.System, r.Variant.Key, r.Variant.Label,
                (r.Round + 1).ToString(CultureInfo.InvariantCulture), N(r.Seconds, "0.0"), r.Frame.Frames.ToString(CultureInfo.InvariantCulture),
                N(r.Frame.FrameAvg), N(r.Frame.FrameP50), N(r.Frame.FrameP95), N(r.Frame.FrameP99), N(r.GpuFrame), r.Bound,
                N(r.Cpu?.TotalMsPerFrame ?? double.NaN, "0.000"), N(r.Cpu?.TotalKbPerFrame ?? double.NaN, "0.0"),
                (r.Cpu?.Gc0 ?? 0).ToString(CultureInfo.InvariantCulture), (r.Cpu?.Gc1 ?? 0).ToString(CultureInfo.InvariantCulture),
                (r.Cpu?.Gc2 ?? 0).ToString(CultureInfo.InvariantCulture),
                N(r.Gpu?.CoreAvg ?? double.NaN, "0"), N(r.Gpu?.CoreMin ?? double.NaN, "0"), N(r.Gpu?.CoreMax ?? double.NaN, "0"),
                N(r.Gpu?.MemAvg ?? double.NaN, "0"), N(r.Gpu?.TempMax ?? double.NaN, "0"), N(r.Gpu?.PowerAvg ?? double.NaN, "0.0"),
                N(r.Gpu?.UtilAvg ?? double.NaN, "0"), r.Gpu != null ? r.Gpu.ThrottleText : "n/a",
                c.Quality, r.Scene ?? c.Scene, c.Position, N(c.HourOfDay, "0.00"),
                c.Width.ToString(CultureInfo.InvariantCulture), c.Height.ToString(CultureInfo.InvariantCulture), N(c.RenderScale, "0.##"),
                c.ViewDistance.ToString(CultureInfo.InvariantCulture), c.ShadowQuality.ToString(CultureInfo.InvariantCulture),
                c.SsaoQuality.ToString(CultureInfo.InvariantCulture), c.GodRays.ToString(CultureInfo.InvariantCulture),
                c.VsyncMode.ToString(CultureInfo.InvariantCulture), c.MaxFps.ToString(CultureInfo.InvariantCulture), c.Gpu,
                string.Join(";", r.Frame.Segments.Where(s => !double.IsNaN(s.Gpu)).Select(s => s.Key + "=" + N(s.Gpu, "0.000"))),
                string.Join(";", r.Frame.Segments.Where(s => !double.IsNaN(s.Cpu)).Select(s => s.Key + "=" + N(s.Cpu, "0.000"))),
                r.Cpu == null ? "" : string.Join(";", r.Cpu.Spans.Select(s => s.Name + "=" + N(s.MsPerFrame, "0.000") + "/" + N(s.KbPerFrame, "0.0") + "kb")),
            };
            Append(RunsPath, cells);
        }

        public void AppendSummary(BenchContext c, PlanSummary s)
        {
            if (_failed) return;
            foreach (PlanSummary.Row row in s.Rows)
            {
                var cells = new List<string>
                {
                    DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), c.Build, BenchLabel,
                    s.Plan.Kind, s.Plan.Subject, s.Plan.System, row.Variant.Key, row.Variant.Label,
                    row.Runs.ToString(CultureInfo.InvariantCulture), s.Bound,
                    N(row.FrameMedian), N(row.GpuFrameMedian), N(row.P99Median), N(row.OpaqueMedian), N(row.ModCpuMedian, "0.000"),
                    N(row.Delta), N(row.DeltaMin), N(row.DeltaMax), N(row.GpuDelta), N(row.GpuDeltaMin), N(row.GpuDeltaMax),
                    N(row.CpuDelta, "0.000"), N(row.Noise), row.Variant == s.Plan.Variants[0] ? "baseline" : (row.Significant ? "yes" : "noise"),
                    c.Quality, c.Scene, s.Plan.Note,
                };
                Append(SummaryPath, cells);
            }
        }

        /// <summary>Writes a free-form text file next to the CSVs (the coverage report).</summary>
        public static string WriteText(ICoreClientAPI capi, string name, string text)
        {
            string dir = capi.GetOrCreateDataPath("vv-bench");
            string path = Path.Combine(dir, name);
            File.WriteAllText(path, text);
            return path;
        }

        private void Append(string path, List<string> cells)
        {
            try
            {
                File.AppendAllText(path, string.Join(",", cells.Select(Csv)) + "\n");
            }
            catch (Exception ex)
            {
                _logger.Warning("[VintageVisuals] bench: writing " + path + " failed: " + ex.Message);
                _failed = true;
            }
        }

        public static string Csv(string v)
        {
            if (v == null) return "";
            if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return v;
            return "\"" + v.Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
        }

        private static string N(double v, string format = "0.00")
        {
            return double.IsNaN(v) || double.IsInfinity(v) ? "" : v.ToString(format, CultureInfo.InvariantCulture);
        }

        private static string Safe(string s)
        {
            var sb = new StringBuilder();
            foreach (char ch in s ?? "unknown") sb.Append(char.IsLetterOrDigit(ch) || ch == '-' ? ch : '_');
            return sb.ToString();
        }
    }
}
