// ShellTests — the pure types the SHELL leans on, as opposed to the ones an
// engine owns: the seconds->bars scaler that makes the §8 "escala sola"
// contract true, and the gate report the panel renders.
//
// They live in their own file because neither belongs to an engine — every
// engine and the strategy itself go through them — and because a bug in either
// is invisible on a chart. A horizon silently floored to one bar still trades;
// it just trades a different model than the one that was measured.
using System;
using BreakBoxCore;

public static class ShellTests
{
    public static void Run()
    {
        SecondsToBars();
        BarSecondsEstimate();
        GateReport();
        EntryWindowAndBudget();
    }

    // §4.2. One report per engine, never shared. Its whole job is that the panel
    // can never again print READY next to "(out of band)" — v1 displayed both
    // and connected neither, and Javier watched a dead strategy for an hour.
    private static void GateReport()
    {
        T.Section("Gate report — what blocked, and how deep");

        var g = new BbGateReport();
        T.Check(g.Block == "", "a fresh report blocks nothing");
        T.CheckInt(g.GateDepth, -1, "and has no failing gate");

        g.Set("box range", "range 275.00 = 7.2x ATR (max 6.0)", 2);
        T.Check(g.Block == "box range", "the block is the ladder row that failed");
        T.Check(g.BlockDetail == "range 275.00 = 7.2x ATR (max 6.0)", "the detail is what it needs vs what it has");
        T.CheckInt(g.GateDepth, 2, "the depth dims everything after it");

        // -1, not 0. The panel renders rows BEFORE GateDepth as passed, so a
        // Clear() that left the depth at 0 would dim the whole ladder and report
        // a healthy engine as blocked at its first gate.
        g.Clear();
        T.Check(g.Block == "", "Clear empties the block");
        T.CheckInt(g.GateDepth, -1, "and returns the depth to 'no failing gate'");

        // Engines write these from early returns on the hot path. A null there
        // is a NullReferenceException inside the panel's render, one layer away
        // from where it was caused.
        g.Set(null, null, 0);
        T.Check(g.Block == "" && g.BlockDetail == "", "null is stored as empty, never as null");
    }

    // §8: every horizon on the parameter surface is SECONDS, and this is the
    // only conversion in the codebase. The reference model was measured on 30s
    // bars, so those rows have to come out at exactly the periods §5.4 names.
    private static void SecondsToBars()
    {
        T.Section("Scale — seconds to bars (spec 8)");

        T.CheckInt(BbScale.Bars(300, 30, 2), 10, "RibbonFastSec 300 @30s = EMA(10)");
        T.CheckInt(BbScale.Bars(690, 30, 2), 23, "RibbonSlowSec 690 @30s = EMA(23)");
        T.CheckInt(BbScale.Bars(1560, 30, 2), 52, "TrendLineSec 1560 @30s = EMA(52)");

        // The same dial on three other bar sizes. This is the entire point of
        // the contract: the model does not change, the bar counts do.
        T.CheckInt(BbScale.Bars(1560, 15, 2), 104, "TrendLineSec @15s");
        T.CheckInt(BbScale.Bars(1560, 60, 2), 26, "TrendLineSec @1m");
        T.CheckInt(BbScale.Bars(1560, 120, 2), 13, "TrendLineSec @2m");

        // Truncation, not rounding. 120s of trigger life on a 90s bar is ONE
        // bar; rounding it to 2 hands the trade a longer leash than was asked
        // for, and the direction of that error is always "riskier".
        T.CheckInt(BbScale.Bars(120, 90, 1), 1, "the division truncates");

        // The floor is the defect guard, not politeness. A horizon shorter than
        // one bar converts to zero, and a zero-bar gate is not strict — it is
        // OFF: `ageBars >= 0` is true on the touch bar itself, which is the one
        // bar §5.2 step 3 exists to exclude.
        T.CheckInt(BbScale.Bars(30, 300, 1), 1, "a sub-bar horizon floors at the minimum");
        T.CheckInt(BbScale.Bars(30, 300, 2), 2, "and honours a minimum of 2");
        T.CheckInt(BbScale.Bars(600, 0, 2), 2, "a nonsense bar size floors rather than dividing by zero");
    }

    // §8's non-time-series branch. Javier runs 150-tick charts elsewhere in this
    // workspace (PatternZone), so a hard throw would turn "escala sola" into "no
    // carga". The estimate makes a tick chart usable and VISIBLY approximate
    // instead of silently wrong.
    private static void BarSecondsEstimate()
    {
        T.Section("Scale — non-time bar size estimate (spec 8)");

        // 150-tick NQ during RTH: a bar every ~12 seconds.
        double[] g = new double[400];
        for (int i = 0; i < g.Length; i++) g[i] = 12.0;
        T.CheckInt(BbScale.EstimateBarSeconds(g, g.Length), 12, "a clean 150-tick sample estimates 12s");

        // Median, not mean, and this is why. Overnight the same chart prints one
        // bar an hour; twenty of those gaps move a MEAN of 400 samples by ~180s,
        // which would have a 12-second chart claim it is on 3-minute bars and
        // stretch every horizon 15x. The median does not move at all.
        for (int i = 0; i < 20; i++) g[i] = 3600.0;
        T.CheckInt(BbScale.EstimateBarSeconds(g, g.Length), 12, "session gaps do not move the median");

        // Too little history to answer. 0 means "no estimate" so the caller can
        // fall back AND warn; guessing off 50 bars is how a chart ends up
        // silently running a different model.
        T.CheckInt(BbScale.EstimateBarSeconds(g, 199), 0, "under 200 samples yields no estimate");
        T.CheckInt(BbScale.EstimateBarSeconds(null, 400), 0, "no history yields no estimate");

        // Sub-second bars are a real configuration (a fast range chart). They
        // must not collapse to 0 and take every horizon's divisor with them.
        double[] fast = new double[250];
        for (int i = 0; i < fast.Length; i++) fast[i] = 0.2;
        T.CheckInt(BbScale.EstimateBarSeconds(fast, fast.Length), 1, "a sub-second series floors at 1s");
    }

    private static void EntryWindowAndBudget()
    {
        T.Section("Session — the entry window and the daily budget (B8, B9)");

        var cfg = new BbConfig();
        T.CheckInt(cfg.EntryWindowStartHhmm, 930, "the window opens at the cash open");
        T.CheckInt(cfg.EntryWindowEndHhmm, 1545, "and shuts 15 minutes before the cash close");

        int lo = BbMath.HhmmToSecs(cfg.EntryWindowStartHhmm);
        int hi = BbMath.HhmmToSecs(cfg.EntryWindowEndHhmm);
        T.Check(!BbMath.InWindow(BbMath.HhmmToSecs(929) + 59, lo, hi), "09:29:59 is out");
        T.Check(BbMath.InWindow(BbMath.HhmmToSecs(930), lo, hi), "09:30:00 is in");
        T.Check(BbMath.InWindow(BbMath.HhmmToSecs(1544) + 59, lo, hi), "15:44:59 is in");
        T.Check(!BbMath.InWindow(BbMath.HhmmToSecs(1545), lo, hi), "15:45:00 is out");

        // The old default was 18:00 -> 16:00: a 22-hour window that gates
        // nothing. A no-op gate is worse than no gate at all, because it reads
        // like a decision somebody made.
        T.Check(!BbMath.InWindow(BbMath.HhmmToSecs(300), lo, hi), "03:00 overnight is out");

        // B9. The budget is a governor of LAST resort. 5 a day is a swing
        // number, and the design frequency is 8-12 fills per session (§13 step
        // 4) — the old cap would have silenced the strategy before lunch and
        // called it risk management. DailyLossLimit governs HOW MUCH; this only
        // stops a runaway loop.
        T.CheckInt(cfg.MaxTradesPerDay, 30, "the daily cap sits above the design frequency");
    }
}
