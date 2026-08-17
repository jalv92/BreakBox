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
        RegimeLatchSurvivesTheDeepPullback();
        RegimeClearsThreeWays();
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

    // Feeds `bars` clean uptrend bars ending at eT = eT0 + 0.5*(bars-1).
    private static int Uptrend(BbCloud eng, int i0, int bars, double eT0, double atr)
    {
        for (int k = 0; k < bars; k++)
            Up(eng, i0 + k, eT0 + 0.5 * k, atr);
        return i0 + bars;
    }

    // A pullback bar: the ribbon has CROSSED (eF below eS) so the instantaneous
    // regime is 0, while the close still sits above eT.
    private static BbAction Pull(BbCloud eng, int i, double eT, double eS, double eF,
                                 double low, double close, double atr)
    {
        return eng.OnBar(Bar(i, close + 0.5, close + 0.7, low, close), Secs(Tm(i)),
                         eF, eS, eT, atr, true, true, false);
    }

    private static void RegimeLatchSurvivesTheDeepPullback()
    {
        T.Section("Cloud — the regime LATCH (§5.2 step 2)");

        // THE test. The token is minted by a pullback that TOUCHES eS, and a
        // pullback deep enough to touch eS drags eF to or below eS within a bar
        // or two. Under an instantaneous regime that zeroes the regime and kills
        // the token BEFORE the reclaim bar it is waiting for: the engine mints
        // and destroys on the same move, every time — v1's self-cancelling latch
        // in a new costume, and v1's zero-trade failure reproduced exactly.
        // Steps 3-5 therefore read RegimeLatched, never the instantaneous value.
        var cfg = Cfg();
        var st = new BbCloudState();
        var eng = new BbCloud(cfg, st);

        int i = Uptrend(eng, 0, 10, 100.0, 4.0);        // eT 100.0 -> 104.5
        T.CheckInt(st.RegimeLatched, +1, "a clean uptrend latches long");
        T.CheckInt(st.RegimeLatchedAgeBars, 0, "and the latch is fresh");

        // Deep pullback: eF (106.5) BELOW eS (107.0) -> instantaneous regime 0.
        // Close 106.0 is still above eT (105.0), so nothing legitimate has died.
        Pull(eng, i, 105.0, 107.0, 106.5, 106.8, 106.0, 4.0);
        T.CheckInt(st.RegimeLatched, +1, "the latch HOLDS through a zeroed instantaneous regime");
        T.CheckInt(st.RegimeLatchedAgeBars, 1, "and ages instead of clearing");

        Pull(eng, i + 1, 105.5, 107.5, 106.0, 106.2, 106.4, 4.0);
        T.CheckInt(st.RegimeLatched, +1, "two bars deep, still latched");
        T.Check(st.Gate.Block != "regime", "so the regime gate is not the blocker");
    }

    private static void RegimeClearsThreeWays()
    {
        T.Section("Cloud — the three regime clears");

        // (1) the OPPOSITE regime forming.
        var st1 = new BbCloudState();
        var eng1 = new BbCloud(Cfg(), st1);
        int i = Uptrend(eng1, 0, 10, 100.0, 4.0);
        for (int k = 0; k < 8; k++)
        {
            double eT = 104.5 - 0.5 * k, eS = eT - 2.0, eF = eS - 2.0, c = eF - 2.0;
            eng1.OnBar(Bar(i + k, c + 1.0, eS - 1.0, c - 0.5, c), Secs(Tm(i + k)),
                       eF, eS, eT, 4.0, true, true, false);
        }
        T.CheckInt(st1.RegimeLatched, -1, "the opposite regime replaces the latch");

        // (2) a close THROUGH eT against the latch. The reference's deepest
        // pullback (26864.83) still sat 4.3 pts above eT and never closed
        // through it — that is the line between "pullback" and "over".
        var st2 = new BbCloudState();
        var eng2 = new BbCloud(Cfg(), st2);
        i = Uptrend(eng2, 0, 10, 100.0, 4.0);
        Pull(eng2, i, 105.0, 107.0, 106.5, 104.0, 104.5, 4.0);   // close 104.5 < eT 105.0
        T.CheckInt(st2.RegimeLatched, 0, "a close through eT against the latch clears it");

        // (3) age > RegimeMemory.
        var cfg3 = Cfg();
        cfg3.RegimeMemory = 3;
        var st3 = new BbCloudState();
        var eng3 = new BbCloud(cfg3, st3);
        i = Uptrend(eng3, 0, 10, 100.0, 4.0);
        for (int k = 0; k < 3; k++)
            Pull(eng3, i + k, 105.0, 107.0, 106.5, 106.8, 106.0, 4.0);
        T.CheckInt(st3.RegimeLatched, +1, "still latched at exactly RegimeMemory bars");
        Pull(eng3, i + 3, 105.0, 107.0, 106.5, 106.8, 106.0, 4.0);
        T.CheckInt(st3.RegimeLatched, 0, "age > RegimeMemory clears it");
        T.Check(st3.Gate.Block == "regime" && st3.Gate.GateDepth == 1, "and the ladder says regime at depth 1");
    }
}
