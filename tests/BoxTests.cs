// BoxTests — box construction from the three sources, the validity band, the
// break engine's arming/expiry, and the retrace engine's extension gate.
//
// Every test drives the engine the way the strategy does: one CLOSED 1-minute
// bar at a time, with an ET seconds-of-day and a session date. Bars are
// synthetic and hand-built so the expected box is arithmetic, not a fixture.
using System;
using System.Collections.Generic;
using BreakBoxCore;

public static class BoxTests
{
    public static void Run()
    {
        PriorPeriodBox();
        InitialBalanceBox();
        ValidityBand();
        BreakArmAndExpire();
        BreakRequiresClose();
        RetraceNeedsExtension();
        Budgets();
    }

    // 18:00 ET on an arbitrary day. Every test counts minutes from here.
    private static readonly DateTime Open = new DateTime(2026, 8, 3, 18, 0, 0);

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
        c.MinBoxRangeAtr = 0.0;         // the band has its own test
        c.MaxBoxRangeAtr = 1e9;
        c.EntryWindowStartHhmm = 1800;
        c.EntryWindowEndHhmm = 1700;
        return c;
    }

    // Feeds `count` flat bars inside a range so a box seals with a known
    // high/low, then returns the engine positioned at the next bar.
    private static void Feed(BbEngine eng, DateTime start, int count, double hi, double lo, double atr)
    {
        for (int i = 0; i < count; i++)
        {
            DateTime t = start.AddMinutes(i);
            // Alternate so both extremes are printed early and the rest sits inside.
            double h = i == 0 ? hi : hi - 1.0;
            double l = i == 1 ? lo : lo + 1.0;
            eng.OnBar(Bar(t, (hi + lo) / 2, h, l, (hi + lo) / 2), Secs(t), t.Date, atr, true, false);
        }
    }

    private static void PriorPeriodBox()
    {
        T.Section("Box — PriorPeriod (4H slots anchored to the session open)");

        var cfg = Cfg();
        cfg.BoxSource = BbBoxSource.PriorPeriod;
        cfg.HtfMinutes = 240;
        cfg.EnableBreak = false;
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);

        // First slot: 18:00 -> 22:00. Range 100..110.
        Feed(eng, Open, 240, 110.0, 100.0, 4.0);
        T.Check(eng.Box == null, "no box until the first slot closes");

        // One bar into the second slot seals the first.
        DateTime t = Open.AddMinutes(240);
        eng.OnBar(Bar(t, 105, 106, 104, 105), Secs(t), t.Date, 4.0, true, false);
        T.Check(eng.Box != null, "the slot boundary sealed a box");
        T.CheckClose(eng.Box.High, 110.0, "box high");
        T.CheckClose(eng.Box.Low, 100.0, "box low");
        T.Check(eng.Box.Valid, "box is valid inside the band");
        int firstId = eng.Box.Id;

        // The second slot seals its own box, with a new id.
        Feed(eng, Open.AddMinutes(241), 239, 120.0, 112.0, 4.0);
        DateTime t2 = Open.AddMinutes(480);
        eng.OnBar(Bar(t2, 115, 116, 114, 115), Secs(t2), t2.Date, 4.0, true, false);
        T.Check(eng.Box.Id != firstId, "a new slot replaces the box");
        T.CheckClose(eng.Box.High, 120.0, "second box high");
    }

    private static void InitialBalanceBox()
    {
        T.Section("Box — InitialBalance");

        var cfg = Cfg();
        cfg.BoxSource = BbBoxSource.InitialBalance;
        cfg.IbStartHhmm = 930;
        cfg.IbMinutes = 60;
        cfg.EnableBreak = false;
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);

        // Bars before the cash open build nothing.
        DateTime pre = new DateTime(2026, 8, 4, 9, 0, 0);
        for (int i = 0; i < 30; i++)
        {
            DateTime t = pre.AddMinutes(i);
            eng.OnBar(Bar(t, 50, 60, 40, 50), Secs(t), t.Date, 4.0, true, false);
        }
        T.Check(eng.Box == null, "pre-open bars do not build an IB");

        // 09:30 -> 10:30 is the IB. Range 200..210.
        DateTime ib = new DateTime(2026, 8, 4, 9, 30, 0);
        Feed(eng, ib, 60, 210.0, 200.0, 4.0);
        T.Check(eng.Box == null, "still open at the last IB bar");

        DateTime after = new DateTime(2026, 8, 4, 10, 30, 0);
        eng.OnBar(Bar(after, 205, 206, 204, 205), Secs(after), after.Date, 4.0, true, false);
        T.Check(eng.Box != null, "leaving the IB window seals it");
        T.CheckClose(eng.Box.High, 210.0, "IB high");
        T.CheckClose(eng.Box.Low, 200.0, "IB low");
    }

    private static void ValidityBand()
    {
        T.Section("Box — ATR validity band");

        // A 2-point box against a 20-point ATR is noise; a 200-point box is a
        // trend leg with two arbitrary ends. Both are refused.
        var cfg = Cfg();
        cfg.BoxSource = BbBoxSource.PriorPeriod;
        cfg.HtfMinutes = 60;
        cfg.MinBoxRangeAtr = 0.5;
        cfg.MaxBoxRangeAtr = 6.0;
        cfg.EnableBreak = false;

        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);
        Feed(eng, Open, 60, 101.0, 100.0, 20.0);        // range 1.0, ATR 20 -> 0.05 ATR
        DateTime t = Open.AddMinutes(60);
        eng.OnBar(Bar(t, 100.5, 100.6, 100.4, 100.5), Secs(t), t.Date, 20.0, true, false);
        T.Check(eng.Box != null && !eng.Box.Valid, "a sub-0.5-ATR box is refused");

        var st2 = new BbEngineState();
        var eng2 = new BbEngine(cfg, st2);
        Feed(eng2, Open, 60, 300.0, 100.0, 4.0);        // range 200, ATR 4 -> 50 ATR
        eng2.OnBar(Bar(t, 200, 201, 199, 200), Secs(t), t.Date, 4.0, true, false);
        T.Check(eng2.Box != null && !eng2.Box.Valid, "a 50-ATR box is refused");
    }

    private static void BreakArmAndExpire()
    {
        T.Section("Break — arm, then expire or re-enter");

        var cfg = Cfg();
        cfg.BoxSource = BbBoxSource.PriorPeriod;
        cfg.HtfMinutes = 60;
        cfg.EnableBreak = true;
        cfg.BreakBufferTicks = 4;               // 1.00 point
        cfg.RequireCloseOutside = true;
        cfg.TriggerLifeBars = 3;

        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);
        Feed(eng, Open, 60, 110.0, 100.0, 4.0);

        DateTime t = Open.AddMinutes(60);
        // A bar closing above 110 with a high of 112 arms a trigger at 113.00.
        var a = eng.OnBar(Bar(t, 109, 112, 108.5, 111.0), Secs(t), t.Date, 4.0, true, false);
        T.Check(a.Fire, "close above the box fires");
        T.CheckInt(a.Dir, +1, "long");
        T.Check(!a.IsLimit, "the break entry is a stop, not a limit");
        T.CheckClose(a.TriggerPx, 113.00, "trigger sits beyond the break bar's high, not the edge");
        T.Check(eng.BreakArmed, "armed");

        // It does not fire again while armed.
        t = t.AddMinutes(1);
        a = eng.OnBar(Bar(t, 111, 111.5, 110.5, 111.0), Secs(t), t.Date, 4.0, true, false);
        T.Check(!a.Fire, "no re-fire while armed");

        // Closing back inside kills the thesis.
        t = t.AddMinutes(1);
        eng.OnBar(Bar(t, 111, 111.2, 108.0, 109.0), Secs(t), t.Date, 4.0, true, false);
        T.Check(!eng.BreakArmed, "a close back inside disarms");

        // Re-arm, then let it expire on the bar budget.
        var st2 = new BbEngineState();
        var eng2 = new BbEngine(cfg, st2);
        Feed(eng2, Open, 60, 110.0, 100.0, 4.0);
        DateTime u = Open.AddMinutes(60);
        eng2.OnBar(Bar(u, 109, 112, 108.5, 111.0), Secs(u), u.Date, 4.0, true, false);
        T.Check(eng2.BreakArmed, "armed again");
        for (int i = 1; i <= 4; i++)
        {
            DateTime v = u.AddMinutes(i);
            // Stays outside the box, so only the bar budget can kill it.
            eng2.OnBar(Bar(v, 111, 112.5, 110.5, 111.5), Secs(v), v.Date, 4.0, true, false);
        }
        T.Check(!eng2.BreakArmed, "the trigger expires after TriggerLifeBars");
    }

    private static void BreakRequiresClose()
    {
        T.Section("Break — wick vs close");

        var cfg = Cfg();
        cfg.BoxSource = BbBoxSource.PriorPeriod;
        cfg.HtfMinutes = 60;
        cfg.EnableBreak = true;
        cfg.RequireCloseOutside = true;

        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);
        Feed(eng, Open, 60, 110.0, 100.0, 4.0);

        DateTime t = Open.AddMinutes(60);
        // A wick to 115 that closes back at 108: this is the case that makes
        // naive box-breakout backtests look profitable.
        var a = eng.OnBar(Bar(t, 109, 115, 107, 108), Secs(t), t.Date, 4.0, true, false);
        T.Check(!a.Fire, "a wick through the edge is not a break");

        cfg.RequireCloseOutside = false;
        var st2 = new BbEngineState();
        var eng2 = new BbEngine(cfg, st2);
        Feed(eng2, Open, 60, 110.0, 100.0, 4.0);
        a = eng2.OnBar(Bar(t, 109, 115, 107, 108), Secs(t), t.Date, 4.0, true, false);
        T.Check(a.Fire, "with the gate off, the touch fires");
    }

    private static void RetraceNeedsExtension()
    {
        T.Section("Retrace — needs a real extension first");

        var cfg = Cfg();
        cfg.BoxSource = BbBoxSource.PriorPeriod;
        cfg.HtfMinutes = 60;
        cfg.EnableBreak = false;
        cfg.EnableRetrace = true;
        cfg.ExtensionAtr = 1.0;                 // ATR 4.0 -> needs 4 points beyond the edge
        cfg.RetraceMaxBars = 30;
        cfg.RetraceOffsetTicks = 0;

        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);
        Feed(eng, Open, 60, 110.0, 100.0, 4.0);

        // Pokes 2 points out (half the required extension) and comes straight
        // back: no trade, because chasing that is the behaviour the IB bot
        // explicitly refuses.
        DateTime t = Open.AddMinutes(60);
        var a = eng.OnBar(Bar(t, 110, 112, 109.5, 111.0), Secs(t), t.Date, 4.0, true, false);
        T.Check(!a.Fire, "a 0.5-ATR poke does not qualify");
        t = t.AddMinutes(1);
        a = eng.OnBar(Bar(t, 111, 111.2, 110.0, 110.2), Secs(t), t.Date, 4.0, true, false);
        T.Check(!a.Fire, "and the return to the edge still does not fire");

        // Now a real one: 6 points beyond 110, then back to the edge.
        var st2 = new BbEngineState();
        var eng2 = new BbEngine(cfg, st2);
        Feed(eng2, Open, 60, 110.0, 100.0, 4.0);
        DateTime u = Open.AddMinutes(60);
        a = eng2.OnBar(Bar(u, 110, 116, 109.5, 115.5), Secs(u), u.Date, 4.0, true, false);
        T.Check(!a.Fire, "the extension bar itself does not fire (that would be chasing)");
        T.Check(eng2.RetraceQualified, "the excursion qualified");

        u = u.AddMinutes(1);
        a = eng2.OnBar(Bar(u, 115, 115.5, 110.0, 110.5), Secs(u), u.Date, 4.0, true, false);
        T.Check(a.Fire, "the return to the edge fires");
        T.CheckInt(a.Dir, +1, "long, in the direction of the extension");
        T.Check(a.IsLimit, "the retrace entry is a limit at the edge");
        T.CheckClose(a.TriggerPx, 110.00, "limit sits on the box edge");
    }

    private static void Budgets()
    {
        T.Section("Budgets — per box and per day");

        var cfg = Cfg();
        cfg.BoxSource = BbBoxSource.PriorPeriod;
        cfg.HtfMinutes = 60;
        cfg.EnableBreak = true;
        cfg.RequireCloseOutside = true;
        cfg.MaxTradesPerBox = 1;

        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);
        Feed(eng, Open, 60, 110.0, 100.0, 4.0);

        DateTime t = Open.AddMinutes(60);
        var a = eng.OnBar(Bar(t, 109, 112, 108.5, 111.0), Secs(t), t.Date, 4.0, true, false);
        T.Check(a.Fire, "first break fires");

        // A submitted trigger that never fills consumes no budget — only a FILL
        // does. Otherwise MaxTradesPerBox = 1 turns into zero trades on a day of
        // cancelled entries.
        T.CheckInt(st.TradesThisBox, 0, "an unfilled trigger costs no budget");
        eng.OnEntryFilled();
        T.CheckInt(st.TradesThisBox, 1, "the fill costs budget");
        T.Check(!eng.BreakArmed, "the fill disarms the trigger");

        // Second break on the same box is refused.
        t = t.AddMinutes(1);
        eng.OnBar(Bar(t, 111, 111.5, 108, 109), Secs(t), t.Date, 4.0, true, false);   // back inside
        t = t.AddMinutes(1);
        a = eng.OnBar(Bar(t, 109, 113, 108.5, 112.0), Secs(t), t.Date, 4.0, true, false);
        T.Check(!a.Fire, "the per-box budget is spent");

        // A new box resets it.
        Feed(eng, Open.AddMinutes(63), 57, 130.0, 120.0, 4.0);
        DateTime v = Open.AddMinutes(120);
        eng.OnBar(Bar(v, 125, 126, 124, 125), Secs(v), v.Date, 4.0, true, false);
        T.CheckInt(st.TradesThisBox, 0, "a new box resets the per-box counter");
    }
}
