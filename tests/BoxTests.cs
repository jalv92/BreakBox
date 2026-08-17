// BoxTests — the v2 box: formation over a BAR window, seal, invalidate, the
// validity gate against previously sealed boxes, the cold start, and §6.2
// arming.
//
// v1's tests drove a 240-minute slot machine (PriorPeriod / InitialBalance /
// PriorSession) that no longer exists. They were deleted with it rather than
// ported: they pinned the exact dimensional error that made the strategy take
// zero trades.
//
// Every test drives the engine the way the shell does: one CLOSED 30-second bar
// at a time, with an ET seconds-of-day and a session date. Bars are synthetic
// and hand-built so every expected number is arithmetic, not a fixture.
using System;
using BreakBoxCore;

public static class BoxTests
{
    public static void Run()
    {
        ColdStartIsHardDisabled();
        SampleRingIsNotSelfSelected();
        FormationExcludesTheCurrentBar();
        SealFreezesTheEdges();
        InvalidateOnBreakAndOnAge();
    }

    // 09:30 ET, inside the default entry window.
    private static readonly DateTime Open = new DateTime(2026, 8, 3, 9, 30, 0);

    private static BbBar Bar(DateTime t, double o, double h, double l, double c)
    {
        return new BbBar { Time = t, Open = o, High = h, Low = l, Close = c, Volume = 100 };
    }

    private static int Secs(DateTime t)
    {
        return t.Hour * 3600 + t.Minute * 60 + t.Second;
    }

    private static BbConfig Cfg()
    {
        var c = new BbConfig();
        c.TickSize = 0.25;
        c.BoxLookback = 4;
        c.BoxMinBars = 2;
        c.BoxRangePctile = 50.0;
        c.BoxSampleN = 200;
        c.BoxMeanSamples = 3;
        c.BoxValidLo = 0.4;
        c.BoxValidHi = 2.5;
        c.BoxDeadAtr = 0.5;
        c.BoxMaxAge = 500;
        c.BoxArmsPerEdge = 2;
        c.BoxArmCooldown = 3;
        c.TriggerOffsetTicks = 1;
        c.TriggerLife = 1;
        c.EntryWindowStartHhmm = 930;
        c.EntryWindowEndHhmm = 1545;
        return c;
    }

    // ATR warm, auto-trade on, flat. The three booleans are the ones the shell
    // passes; `canTrade` is the B2 fix and is now an argument, not a value the
    // shell computes and throws away.
    private static BbAction Step(BbEngine eng, DateTime t, double o, double h, double l, double c, double atr)
    {
        return eng.OnBar(Bar(t, o, h, l, c), Secs(t), t.Date, atr, true, true, false);
    }

    private static void ColdStartIsHardDisabled()
    {
        T.Section("Box — cold start is hard-disabled and says so");

        var cfg = Cfg();
        // SEAL (T42) and INVALIDATE (T43) landed after this test was written:
        // this exact tape now genuinely seals ~3 boxes by bar 60 (quiet run ->
        // seal, break invalidates it, next quiet run seals the next one), which
        // would graduate SealedCount past the shared Cfg()'s BoxMeanSamples=3
        // mid-run and flip the gate before the loop even finishes — not a cold
        // start anymore. Raised here, locally, so THIS test still tests what it
        // says it tests: an undefined cold start with no sealed-box history at
        // all, for the whole tape.
        cfg.BoxMeanSamples = 1000;
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);

        // A tape that breaks a tight range every 20 bars. With no sealed-box
        // history there is no denominator for the validity gate, so nothing may
        // fire — an undefined cold start is the second way to reproduce v1's
        // silence, and this is the assert that stops it coming back.
        int fires = 0;
        DateTime t = Open;
        for (int i = 0; i < 60; i++)
        {
            bool brk = (i % 20) == 19;
            if (Step(eng, t, 100.0, brk ? 108.5 : 100.5, 99.5, brk ? 108.0 : 100.0, 2.0).Fire)
                fires++;
            t = t.AddSeconds(30);
        }

        T.CheckInt(fires, 0, "nothing fires before BoxMeanSamples boxes have sealed");
        T.Check(st.Gate.Block == "box warming", "the gate names the cold start (got '" + st.Gate.Block + "')");
    }

    private static void SampleRingIsNotSelfSelected()
    {
        T.Section("Box — the sample ring is fed on EVERY bar, pass or fail");

        var cfg = Cfg();                    // BoxLookback = 4
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);

        DateTime t = Open;
        for (int i = 0; i < 30; i++)        // tight: range 1.0
        {
            Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
            t = t.AddSeconds(30);
        }
        for (int i = 0; i < 30; i++)        // wide: range 20.0 — fails formation every bar
        {
            Step(eng, t, 100.0, 110.0, 90.0, 100.0, 2.0);
            t = t.AddSeconds(30);
        }

        // 60 bars, the first 4 with no full window yet. If sampling were gated
        // on the formation test the 30 wide bars would contribute nothing and
        // this would read 26 — the feedback loop that tightens the percentile
        // forever until no box can ever form.
        T.CheckInt(st.SampleFilled, 56, "every bar with a full window contributes one sample");
    }

    private static void FormationExcludesTheCurrentBar()
    {
        T.Section("Box — the formation window is MAX(High,N)[1], not [0]");

        var cfg = Cfg();
        cfg.BoxMinBars = 999;               // nothing seals: this test is about FORMATION alone
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);

        DateTime t = Open;
        for (int i = 0; i < 30; i++)
        {
            Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
            t = t.AddSeconds(30);
        }
        T.Check(st.CandOpen, "a quiet stretch is a candidate");
        T.CheckClose(st.CandHigh, 100.5, "candidate high");
        T.CheckClose(st.CandLow, 99.5, "candidate low");

        // A 60-point bar. Its own range may NOT enter the window it is being
        // tested against — that is the one-bar lookahead that lets a backtest
        // measure the break it is about to trade.
        Step(eng, t, 100.0, 150.0, 90.0, 100.0, 2.0);
        t = t.AddSeconds(30);
        T.Check(st.CandOpen, "the current bar does not widen its own window");
        T.CheckClose(st.CandHigh, 100.5, "candidate high is still the quiet window's");

        // On the NEXT bar it does enter the window, and the candidate dies.
        Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
        T.Check(!st.CandOpen, "one bar later the wide bar is in the window and formation fails");
    }

    private static void SealFreezesTheEdges()
    {
        T.Section("Box — SEAL after BoxMinBars, edges frozen, Id monotone");

        var cfg = Cfg();                    // BoxLookback 4, BoxMinBars 2
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);

        // Bars 1-4 fill the window. Bar 5 is the first candidate, bar 6 the
        // second consecutive one — that is the seal.
        DateTime t = Open;
        for (int i = 0; i < 5; i++)
        {
            Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
            t = t.AddSeconds(30);
        }
        T.Check(st.Box == null, "one passing bar is not a box");

        Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
        t = t.AddSeconds(30);
        T.Check(st.Box != null, "BoxMinBars consecutive passing bars seal it");
        T.CheckInt(st.Box.Id, 1, "ids are monotone from 1");
        T.CheckClose(st.Box.High, 100.5, "sealed high");
        T.CheckClose(st.Box.Low, 99.5, "sealed low");
        T.CheckInt(st.SealedCount, 1, "the seal counts toward the cold start");

        // Nothing moves a sealed box's edges, and a live box is not replaced:
        // an accumulation that gets a new identity every quiet bar has no
        // identity, and §6.2 counts arms PER BOX ID.
        for (int i = 0; i < 10; i++)
        {
            Step(eng, t, 100.0, 100.9, 99.6, 100.0, 2.0);
            t = t.AddSeconds(30);
        }
        T.CheckInt(st.Box.Id, 1, "a live box is not replaced");
        T.CheckClose(st.Box.High, 100.5, "and its edges did not move");
    }

    private static void InvalidateOnBreakAndOnAge()
    {
        T.Section("Box — INVALIDATE on a close beyond an edge, and on age");

        // --- Killed by distance. ATR 2.0, BoxDeadAtr 0.5 -> 1.0 point of slack
        // past 100.5, so a close at 102.0 is 6 ticks beyond the tolerance.
        var cfg = Cfg();
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);

        DateTime t = Open;
        for (int i = 0; i < 6; i++)
        {
            Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
            t = t.AddSeconds(30);
        }
        T.Check(st.Box != null, "sealed");

        Step(eng, t, 100.0, 101.2, 100.0, 101.0, 2.0);
        t = t.AddSeconds(30);
        T.Check(st.Box != null, "a close inside the BoxDeadAtr tolerance does not kill it");

        Step(eng, t, 101.0, 102.5, 100.9, 102.0, 2.0);
        T.Check(st.Box == null, "a close beyond the edge by more than BoxDeadAtr kills it");

        // --- Killed by age, and replaced by the next candidate.
        var cfg2 = Cfg();
        cfg2.BoxMaxAge = 5;
        var st2 = new BbEngineState();
        var eng2 = new BbEngine(cfg2, st2);

        DateTime u = Open;
        for (int i = 0; i < 12; i++)        // seals at bar 6, ages out on bar 12
        {
            Step(eng2, u, 100.0, 100.5, 99.5, 100.0, 2.0);
            u = u.AddSeconds(30);
        }
        T.CheckInt(st2.Box.Id, 2, "an aged-out box is replaced by the next candidate to seal");
    }
}
