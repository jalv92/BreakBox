// TrendStTests.cs — the TrendST setup detector, TrendStCore.cs.
using System;
using BreakBoxCore;

public static class TrendStTests
{
    private static BbBar B(double o, double h, double l, double c, double v)
    {
        BbBar b;
        b.Time = DateTime.MinValue;
        b.Open = o; b.High = h; b.Low = l; b.Close = c; b.Volume = v;
        return b;
    }

    // eF = 100 throughout. Tip bar sits above the ribbon, pullback bars dip
    // into it, signal bar closes above the previous high.
    private static int Play(TrendStSetup s, double[] vols, bool decayed)
    {
        int f = 0;
        f = s.OnBar(B(104, 106, 103, 105, vols[0]), +1, 100);            // tip
        for (int i = 1; i < vols.Length; i++)
            f = s.OnBar(B(103, 104, 99, 101, vols[i]), +1, 100);         // pullback, low <= eF
        return s.OnBar(B(101, 106, 100, 105, 300), +1, 100);              // engulf: close 105 > prev high 104
    }

    public static void Run()
    {
        T.Section("TrendST setup");
        TrendStConfig cfg = new TrendStConfig();

        T.CheckInt(Play(new TrendStSetup(cfg), new[] { 500.0, 300, 200 }, true), +1, "long: 2-bar pullback, volume tip 500 -> 200, engulf fires");
        T.CheckInt(Play(new TrendStSetup(cfg), new[] { 500.0, 300, 900, 200 }, true), +1, "a big bar in the middle does not kill it (tip vs last bar)");
        T.CheckInt(Play(new TrendStSetup(cfg), new[] { 300.0, 300, 300 }, false), 0, "flat volume: no signal");
        T.CheckInt(Play(new TrendStSetup(cfg), new[] { 200.0, 300, 500 }, false), 0, "rising volume: no signal");
        T.CheckInt(Play(new TrendStSetup(cfg), new[] { 500.0, 200 }, true), 0, "one pullback bar < MinPullbackBars: no signal");

        // Regime flip mid-pullback voids it.
        TrendStSetup s = new TrendStSetup(cfg);
        s.OnBar(B(104, 106, 103, 105, 500), +1, 100);
        s.OnBar(B(103, 104, 99, 101, 300), +1, 100);
        s.OnBar(B(103, 104, 99, 101, 200), -1, 100);
        T.CheckInt(s.OnBar(B(101, 106, 100, 105, 300), +1, 100), 0, "regime flip voids the pullback");

        // Short mirror.
        s = new TrendStSetup(cfg);
        s.OnBar(B(96, 97, 94, 95, 500), -1, 100);                         // tip below ribbon
        s.OnBar(B(97, 101, 96, 99, 300), -1, 100);                        // high >= eF
        s.OnBar(B(97, 101, 96, 99, 200), -1, 100);
        T.CheckInt(s.OnBar(B(99, 100, 94, 95, 300), -1, 100), -1, "short: close 95 < prev low 96 fires");

        // No engulf (close inside previous bar) -> nothing, and the bar that left
        // the ribbon reset the pullback.
        s = new TrendStSetup(cfg);
        s.OnBar(B(104, 106, 103, 105, 500), +1, 100);
        s.OnBar(B(103, 104, 99, 101, 300), +1, 100);
        s.OnBar(B(103, 104, 99, 101, 200), +1, 100);
        T.CheckInt(s.OnBar(B(101, 103, 100.5, 102, 300), +1, 100), 0, "green bar that does not clear the prior high: no signal");
        T.CheckInt(s.OnBar(B(102, 106, 101, 105, 300), +1, 100), 0, "…and the pullback was reset by leaving the ribbon");
    }
}
