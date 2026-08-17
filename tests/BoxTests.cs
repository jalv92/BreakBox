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
}
