// VisionTests — the pure maths BreakBoxVision paints with.
//
// Vision is an NT8 file and cannot be compiled here, so what this pins is the
// one non-trivial arithmetic it shares with the cloud engine: the signal
// candle's close-in-range ratio. If Vision computed its own version, the chart
// would paint gold bars the engine never saw, which is precisely the "picture
// that lies" §12 exists to prevent.
using System;
using BreakBoxCore;

public static class VisionTests
{
    public static void Run()
    {
        CloseInRangeRatio();
    }

    private static BbBar Bar(double o, double h, double l, double c)
    {
        return new BbBar { Time = new DateTime(2026, 8, 16, 12, 0, 0), Open = o, High = h, Low = l, Close = c, Volume = 100 };
    }

    private static void CloseInRangeRatio()
    {
        T.Section("Vision — signal candle close-in-range");

        // Both reference signal candles were WICKLESS: the close sat exactly on
        // the extreme, ratio 1.00 (spec §2).
        T.CheckClose(BbMath.CloseInRange(Bar(100.0, 105.0, 100.0, 105.0), +1), 1.00, "wickless up bar reads 1.00 long");
        T.CheckClose(BbMath.CloseInRange(Bar(100.0, 105.0, 100.0, 105.0), -1), 0.00, "the same bar reads 0.00 short");
        T.CheckClose(BbMath.CloseInRange(Bar(105.0, 105.0, 100.0, 100.0), -1), 1.00, "wickless down bar reads 1.00 short");

        // The panel mock's blocked case: body 0.42 against a 0.60 gate.
        T.CheckClose(BbMath.CloseInRange(Bar(91.0, 100.0, 90.0, 94.2), +1), 0.42, "mid-range close reads 0.42");

        // A zero-range bar has no ratio. NaN, not 1.0: every comparison against
        // NaN is false, so a halted-tape doji FAILS the gate instead of becoming
        // the best-looking signal candle of the session.
        double flat = BbMath.CloseInRange(Bar(100.0, 100.0, 100.0, 100.0), +1);
        T.Check(double.IsNaN(flat), "a zero-range bar has no ratio");
        T.Check(!(flat >= 0.60), "and it fails the gate closed");
    }
}
