using System;
using System.Collections.Generic;
using System.Linq;
using VintageVisuals.Common.Diagnostics;

namespace VintageVisuals.Common.Rendering
{
    /// <summary>
    /// Keeps the depth prepass on only where it pays (D95).
    ///
    /// WHY. The prepass is exact, so whether it runs never changes a pixel; it
    /// only trades one depth-only draw of the opaque and leaf pools against the
    /// material work that draw saves. In game (2026-11-26/27) that trade went
    /// both ways: a dense forest saved about 4 ms, a pond with open ground lost
    /// 0.7 ms. Which one a view is cannot be known in advance, but it can be
    /// measured, so the choice is made from the GPU time of the chunk draw itself.
    ///
    /// HOW. Every <see cref="IntervalFrames"/> frames a short trial interleaves
    /// blocks of frames with and without the prepass (<see cref="Rounds"/> rounds
    /// of <see cref="BlockFrames"/> each way, under a second at 60 fps), timing the
    /// whole of <c>ChunkRenderer.RenderOpaque</c> - prepass plus real pass - with
    /// two GPU timestamp queries. Interleaving keeps a slow change in the view
    /// from favouring either side. The cheaper median wins by more than
    /// <see cref="Margin"/>; otherwise the current choice stands, so it does not
    /// flicker on noise. Results are read without waiting, several frames late.
    ///
    /// COST. Nothing between trials (no queries). During a trial two timestamps
    /// a frame; the half of trial frames run on the losing side cost what that
    /// side costs, at most about one frame's difference spread over the trial.
    ///
    /// SAFETY. No GPU timer means no trials and the prepass stays on, the D88
    /// behaviour. Any exception switches the governor off for the session, also
    /// leaving the prepass on. It never runs while a bench forces the prepass.
    /// </summary>
    public sealed class PrepassGovernor
    {
        public const int IntervalFrames = 900;
        public const int BlockFrames = 6;
        public const int Rounds = 4;
        public const double Margin = 0.03;
        public const int MinSamples = 10;
        private const int FirstTrialFrame = 300;
        private const int Slots = 8;

        private readonly Action<string> _log;
        private GlTimestampApi _gl;
        private readonly int[] _startQuery = new int[Slots];
        private readonly int[] _endQuery = new int[Slots];
        private readonly bool[] _pending = new bool[Slots];
        private readonly bool[] _slotRan = new bool[Slots];
        private readonly List<double> _with = new List<double>();
        private readonly List<double> _without = new List<double>();
        private int _slot;
        private long _frame;
        private long _nextTrial = FirstTrialFrame;
        private int _trialFrame = -1;
        private bool _timing;
        private bool _off;

        /// <summary>The current choice: true runs the prepass outside trials.</summary>
        public bool Choice { get; private set; } = true;

        /// <summary>The last trial's medians in ms (with, without), for the readout; NaN before the first.</summary>
        public double LastWithMs { get; private set; } = double.NaN;
        public double LastWithoutMs { get; private set; } = double.NaN;

        public PrepassGovernor(Action<string> log)
        {
            _log = log;
        }

        /// <summary>
        /// Trial schedule: which side frame <paramref name="trialFrame"/> of a
        /// trial runs. Blocks alternate, starting with the side not currently chosen.
        /// </summary>
        public static bool RunsPrepass(int trialFrame, bool choice)
        {
            bool first = !choice;
            return ((trialFrame / BlockFrames) % 2 == 0) ? first : !first;
        }

        /// <summary>
        /// The decision after a trial: the cheaper median wins by more than the
        /// margin, otherwise <paramref name="current"/> stands. Too few samples
        /// on either side keeps the current choice too.
        /// </summary>
        public static bool Decide(IList<double> withPrepass, IList<double> withoutPrepass, bool current)
        {
            if (withPrepass == null || withoutPrepass == null ||
                withPrepass.Count < MinSamples || withoutPrepass.Count < MinSamples) return current;
            double on = Median(withPrepass), off = Median(withoutPrepass);
            if (on < off * (1.0 - Margin)) return true;
            if (off < on * (1.0 - Margin)) return false;
            return current;
        }

        private static double Median(IList<double> values)
        {
            double[] sorted = values.OrderBy(v => v).ToArray();
            int n = sorted.Length;
            return (n % 2 == 1) ? sorted[n / 2] : 0.5 * (sorted[n / 2 - 1] + sorted[n / 2]);
        }

        /// <summary>
        /// Called at the start of the outer RenderOpaque when the prepass is
        /// wanted and ready. Returns whether to run it this frame.
        /// </summary>
        public bool Begin()
        {
            _timing = false;
            if (_off) return true;
            try
            {
                _frame++;
                if (_gl == null && !EnsureGl()) return true;

                Harvest(_slot);

                if (_trialFrame < 0 && _frame >= _nextTrial)
                {
                    _trialFrame = 0;
                    _with.Clear();
                    _without.Clear();
                }

                if (_trialFrame < 0) return Choice;

                int total = Rounds * 2 * BlockFrames;
                if (_trialFrame < total)
                {
                    bool run = RunsPrepass(_trialFrame, Choice);
                    _slotRan[_slot] = run;
                    _gl.Stamp(_startQuery[_slot]);
                    _timing = true;
                    _trialFrame++;
                    return run;
                }

                // Trial frames done; let the last results arrive, then decide.
                _trialFrame++;
                if (_trialFrame >= total + Slots + 2) Finish();
                return Choice;
            }
            catch (Exception ex)
            {
                Fail(ex);
                return true;
            }
        }

        /// <summary>Called at the end of the outer RenderOpaque.</summary>
        public void End()
        {
            if (!_timing || _off) return;
            _timing = false;
            try
            {
                _gl.Stamp(_endQuery[_slot]);
                _pending[_slot] = true;
                _slot = (_slot + 1) % Slots;
            }
            catch (Exception ex)
            {
                Fail(ex);
            }
        }

        private void Harvest(int slot)
        {
            if (!_pending[slot]) return;
            _pending[slot] = false;
            if (_gl.TryRead(_startQuery[slot], out long a) && _gl.TryRead(_endQuery[slot], out long b))
            {
                double ms = (b - a) / 1e6;
                if (ms > 0 && ms < 200) (_slotRan[slot] ? _with : _without).Add(ms);
            }
        }

        private void Finish()
        {
            // Read whatever is still pending (it has had Slots + 2 frames).
            for (int i = 0; i < Slots; i++) Harvest(i);
            bool before = Choice;
            Choice = Decide(_with, _without, Choice);
            if (_with.Count >= MinSamples && _without.Count >= MinSamples)
            {
                LastWithMs = Median(_with);
                LastWithoutMs = Median(_without);
            }
            if (Choice != before)
            {
                _log?.Invoke("[VintageVisuals] depth prepass: " + (Choice ? "on" : "off") + " for this view - chunk draw " +
                             LastWithMs.ToString("0.00") + " ms with it, " + LastWithoutMs.ToString("0.00") +
                             " ms without (rechecked every " + IntervalFrames + " frames; the image is identical either way).");
            }
            _trialFrame = -1;
            _nextTrial = _frame + IntervalFrames;
        }

        private bool EnsureGl()
        {
            _gl = GlTimestampApi.TryCreate();
            if (_gl == null)
            {
                _off = true;
                _log?.Invoke("[VintageVisuals] depth prepass: no GPU timer (" + GlTimestampApi.LastError +
                             "); it stays on everywhere, as before.");
                return false;
            }
            for (int i = 0; i < Slots; i++)
            {
                _startQuery[i] = _gl.Create();
                _endQuery[i] = _gl.Create();
            }
            return true;
        }

        private void Fail(Exception ex)
        {
            if (_off) return;
            _off = true;
            _timing = false;
            Choice = true;
            _log?.Invoke("[VintageVisuals] depth prepass: the auto choice is off for this session and the prepass stays on (" +
                         ex.Message + ").");
        }

        /// <summary>A bench or a config change starts over: prepass on, next trial soon.</summary>
        public void Reset()
        {
            Choice = true;
            _trialFrame = -1;
            _nextTrial = _frame + FirstTrialFrame;
            for (int i = 0; i < Slots; i++) _pending[i] = false;
            _timing = false;
        }
    }
}
