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
}
