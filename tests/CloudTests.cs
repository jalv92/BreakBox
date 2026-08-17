// CloudTests — the cloud engine (§5), driven the way the shell drives it: one
// CLOSED bar at a time with the ribbon values passed in as plain doubles. The
// EMAs are NOT computed here on purpose — the engine takes eF/eS/eT as numbers,
// so every geometry a test needs is one literal instead of 60 warmup bars.
using System;
using BreakBoxCore;

public static class CloudTests
{
    public static void Run()
    {
        WarmupBlocks();
    }

    private static readonly DateTime Open = new DateTime(2026, 8, 3, 18, 0, 0);

    private static DateTime Tm(int i) { return Open.AddSeconds(30 * i); }
    private static int Secs(DateTime t) { return t.Hour * 3600 + t.Minute * 60 + t.Second; }

    private static BbBar Bar(int i, double o, double h, double l, double c)
    {
        return new BbBar { Time = Tm(i), Open = o, High = h, Low = l, Close = c, Volume = 100 };
    }

    private static BbCloudConfig Cfg()
    {
        var c = new BbCloudConfig();
        c.TickSize = 0.25;
        c.TrendSlopeLookback = 5;       // short so the ring fills in 6 bars, not 11
        c.TrendSlopeAtr = 0.15;
        c.RegimeMemory = 50;
        c.PullbackMax = 50;
        return c;
    }

    // One clean uptrend bar: eT rising 0.5/bar, ribbon stacked above it, close
    // above eF, and the LOW deliberately kept above eS so this helper never
    // mints a pullback token by accident.
    private static BbAction Up(BbCloud eng, int i, double eT, double atr)
    {
        double eS = eT + 2.0, eF = eS + 2.0, c = eF + 2.0;
        return eng.OnBar(Bar(i, c - 1.0, c + 0.5, eS + 1.0, c), Secs(Tm(i)),
                         eF, eS, eT, atr, true, true, false);
    }

    private static void WarmupBlocks()
    {
        T.Section("Cloud — warmup gate (§5.2 step 1)");

        var cfg = Cfg();
        var st = new BbCloudState();
        var eng = new BbCloud(cfg, st);

        // The constructor owns the ring size. A hard-coded 12 slots silently
        // under-reads the moment the converted lookback exceeds it (§5.1).
        T.CheckInt(st.SlopeBuf.Length, cfg.TrendSlopeLookback + 1, "the constructor sized the slope ring");
        T.Check(double.IsNaN(st.Ext), "ext starts NaN — 0.0 is a price, not 'no token'");

        // atrWarm false: the shell has already ANDed in every EMA the active
        // config reads (§11 B13), so one false is the whole warmup story.
        var a = eng.OnBar(Bar(0, 100, 101, 99, 100), Secs(Tm(0)), 104, 102, 100, 4.0, false, true, false);
        T.Check(!a.Fire, "a cold engine never fires");
        T.Check(st.Gate.Block == "warmup", "and it says warmup, not 'ready' (§9.1: READY next to (out of band) is the defect)");
        T.CheckInt(st.Gate.GateDepth, 0, "warmup is ladder depth 0");

        // Warm indicators, ring still filling: the engine's OWN warmup, which the
        // shell cannot see because it does not own the ring. 4 iterations here,
        // not 5 — the cold bar-0 call above already pushed once, so 4 more
        // leaves the 6-slot ring at 5/6 (still short) instead of exactly full.
        for (int i = 1; i <= 4; i++)
            Up(eng, i, 100.0 + 0.5 * i, 4.0);
        T.Check(st.Gate.Block == "warmup", "an unfilled slope ring is still warmup");

        // Sixth push fills a 6-slot ring, so the warmup gate clears.
        Up(eng, 6, 103.0, 4.0);
        T.Check(string.IsNullOrEmpty(st.Gate.Block), "a full ring clears the warmup block");

        // The ring is pushed BEFORE the warmup return. Gate the push behind the
        // gate and it never fills, so warmup never clears — a deadlock that
        // looks exactly like v1's zero-trade silence.
        T.CheckInt(st.SlopeFilled, st.SlopeBuf.Length, "the ring filled while the gate was blocking");
    }
}
