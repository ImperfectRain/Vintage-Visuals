using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VintageVisuals.Common.Rendering;

namespace VintageVisuals.SmokeTest
{
    /// <summary>
    /// D95: the depth prepass governor. The prepass is exact, so the governor
    /// can only cost frame time, never pixels - but a decision that is inverted,
    /// has no margin, or a trial that measures one side more often than the
    /// other, would quietly pick the slower side in every view.
    /// </summary>
    public static class PrepassGovernorChecks
    {
        private static List<double> Many(double v, int n) { return Enumerable.Repeat(v, n).ToList(); }

        public static void Run(string repo, Action<string, bool, string> check)
        {
            int n = PrepassGovernor.MinSamples;
            bool cheaperOn = PrepassGovernor.Decide(Many(8.0, n), Many(10.0, n), false);
            bool cheaperOff = PrepassGovernor.Decide(Many(10.0, n), Many(8.0, n), true);
            bool tieKeepsOn = PrepassGovernor.Decide(Many(10.0, n), Many(9.9, n), true);
            bool tieKeepsOff = PrepassGovernor.Decide(Many(9.9, n), Many(10.0, n), false);
            bool fewKeep = PrepassGovernor.Decide(Many(10.0, n - 1), Many(5.0, n - 1), true);
            check("D95: the governor picks the cheaper side, keeps its choice inside the margin and on too few samples",
                cheaperOn && !cheaperOff && tieKeepsOn && !tieKeepsOff && fewKeep,
                "on=" + cheaperOn + " off=" + !cheaperOff + " tie=" + tieKeepsOn + "/" + !tieKeepsOff + " few=" + fewKeep);

            int total = PrepassGovernor.Rounds * 2 * PrepassGovernor.BlockFrames;
            bool fair = true;
            foreach (bool choice in new[] { true, false })
            {
                int on = 0, switches = 0; bool last = !PrepassGovernor.RunsPrepass(0, choice);
                for (int f = 0; f < total; f++)
                {
                    bool r = PrepassGovernor.RunsPrepass(f, choice);
                    if (r) on++;
                    if (r != last) switches++;
                    last = r;
                }
                fair &= on * 2 == total && switches >= 2 * PrepassGovernor.Rounds - 1;
            }
            check("D95: a trial measures both sides equally often, interleaved in blocks", fair, "frames=" + total);

            string dp = File.ReadAllText(Path.Combine(repo, "src/Common/Rendering/DepthPrepass.cs"));
            check("D95: the governor runs only outside benches, and its timing closes in the outer finalizer",
                dp.Contains("if (BenchOverride < 0 && Governor != null && !BenchRecording())") &&
                dp.Contains("if (!Governor.Begin()) return true;") &&
                dp.Contains("if (_governed) { _governed = false; Governor?.End(); }"),
                "");
        }
    }
}
