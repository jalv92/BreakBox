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
        SealedBarsOwnExtremesAreExcludedFromItsOwnBox();
        InvalidateOnBreakAndOnAge();
        ValidityIsRelativeToEarlierBoxes();
        ArmingDoesNotSpendTheEdge();
        CooldownAndArmCapAcrossExpiries();
        ExpirySpendsAnArmRejectionRefundsIt();
        SecondsScaleToBars();
        AutoTradeIsAFinalVeto();
        DirectionOffIsANamedBlocker();
    }

    // No production seam seeds the sealed-range ring — `SeedSealedRange` was
    // dead (nothing in the shell ever called it, and `BbTradeRecord` carries no
    // box-range field to seed from) and was deleted. Tests reach into the
    // state's own public ring instead, four lines lifted verbatim from `Seal()`.
    private static void Seed(BbEngineState st, double range)
    {
        st.SealedRanges[st.SealedIdx] = range;
        st.SealedIdx = (st.SealedIdx + 1) % st.SealedRanges.Length;
        if (st.SealedFilled < st.SealedRanges.Length)
            st.SealedFilled++;
        st.SealedCount++;
    }

    private static void SecondsScaleToBars()
    {
        T.Section("Box — the §6.3 surface is SECONDS, converted once");

        // BoxLookbackSec 210 = the measured ~7-bar white rectangle at 30s.
        T.CheckInt(BbScale.Bars(210, 15, 2), 14, "15s bars");
        T.CheckInt(BbScale.Bars(210, 30, 2), 7, "30s bars — the reference chart");
        T.CheckInt(BbScale.Bars(210, 60, 2), 3, "1m bars");

        // 210/120 = 1, and a one-bar window has no range to speak of. The floor
        // is what keeps "escala sola" from meaning "degenerates silently".
        T.CheckInt(BbScale.Bars(210, 120, 2), 2, "2m bars clamp to the floor");

        // A tick-bar estimate can come back as 0 seconds if the estimator is
        // starved. Dividing by it would throw inside OnStateChange, where the
        // exception reads as "the strategy will not load". Phase 1's rule is
        // that a nonsense bar size returns the FLOOR, not a horizon-sized bar
        // count: an unknown bar size must degrade to the smallest honest window,
        // never to a 180-bar one that looks like a real setting.
        T.CheckInt(BbScale.Bars(180, 0, 2), 2, "a zero bar size floors");
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

        var cfg = Cfg();                    // BoxMeanSamples = 3
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);

        // A tape that breaks a tight range every 20 bars. Boxes seal all the way
        // through — the lifecycle is never suppressed — but nothing may fire
        // until the denominator exists.
        int fires = 0;
        DateTime t = Open;
        for (int i = 0; i < 20; i++)
        {
            bool brk = (i % 10) == 9;
            if (Step(eng, t, 100.0, brk ? 108.5 : 100.5, 99.5, brk ? 108.0 : 100.0, 2.0).Fire)
                fires++;
            t = t.AddSeconds(30);
        }
        T.CheckInt(fires, 0, "nothing fires before BoxMeanSamples boxes have sealed");
        T.Check(st.Gate.Block == "box warming", "the gate names the cold start (got '" + st.Gate.Block + "')");
        T.Check(st.Gate.BlockDetail == st.SealedCount + "/3 boxes sealed",
                "and counts it (got '" + st.Gate.BlockDetail + "')");
        T.CheckInt(st.Gate.GateDepth, 1, "cold start sits at ladder depth 1");
        T.Check(st.SealedCount > 0, "boxes still sealed while the engine was disabled");
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

    // Pins the fact PaintBox() got wrong: the box's High/Low are the WINDOW's
    // range (Time[1]..Time[BoxLookback], read by WindowRange() BEFORE Push
    // adds the current bar — BreakBoxCore.cs:565), not the sealing bar's own.
    // Form() only gates the sealing bar's CLOSE against that range
    // (BreakBoxCore.cs:657) — never its wick — so a sealing bar with a close
    // inside the window but a high/low deliberately outside it must still
    // seal a box whose edges come from the window, not from that bar. This is
    // the arithmetic a reader needs to get the drawn rectangle's right edge
    // right (Time[1], not Time[0]): the Draw.Rectangle call itself is
    // NT8-only and untestable here, but the range it draws is not.
    private static void SealedBarsOwnExtremesAreExcludedFromItsOwnBox()
    {
        T.Section("Box — the sealing bar's own high/low are excluded from the box it seals");

        var cfg = Cfg();                    // BoxLookback 4, BoxMinBars 2
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);

        // Bars 1-5 are quiet (100.5/99.5) and fill the window plus open the
        // first candidate — identical setup to SealFreezesTheEdges.
        DateTime t = Open;
        for (int i = 0; i < 5; i++)
        {
            Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
            t = t.AddSeconds(30);
        }
        T.Check(st.Box == null, "not yet sealed");

        // Bar 6 seals it. Close 100.0 sits inside the window's [99.5, 100.5],
        // but its own high (108.0) and low (92.0) reach well outside — a wick
        // the window never measured, because WindowRange() read the ring
        // before this bar was pushed into it.
        Step(eng, t, 100.0, 108.0, 92.0, 100.0, 2.0);
        T.Check(st.Box != null, "a wide-wick bar still seals — only its close is gated");
        T.CheckClose(st.Box.High, 100.5, "the box's high is the WINDOW's, not the sealing bar's 108.0");
        T.CheckClose(st.Box.Low, 99.5, "the box's low is the WINDOW's, not the sealing bar's 92.0");
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

    private static void ValidityIsRelativeToEarlierBoxes()
    {
        T.Section("Box — the validity gate is a ratio against boxes sealed BEFORE it");

        // Seed one 3.0-point box. A 1.0-point box then rates 0.333, below
        // BoxValidLo = 0.4, so it is refused. If the box pushed its own range
        // into the denominator first the mean would be 2.0 and the ratio 0.5 —
        // valid. That is the discrimination this test exists for: a box that
        // helps set its own mean always looks normal.
        var cfg = Cfg();
        cfg.BoxMeanSamples = 1;
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);
        Seed(st, 3.0);

        DateTime t = Open;
        for (int i = 0; i < 6; i++)
        {
            Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
            t = t.AddSeconds(30);
        }
        T.Check(st.Box != null, "sealed");
        T.Check(!st.Box.Valid, "0.33x the mean of earlier boxes is out of band");

        // Same 1.0-point box against a 1.0-point history: ratio 1.0, valid.
        var st2 = new BbEngineState();
        var eng2 = new BbEngine(cfg, st2);
        Seed(st2, 1.0);

        DateTime u = Open;
        for (int i = 0; i < 6; i++)
        {
            Step(eng2, u, 100.0, 100.5, 99.5, 100.0, 2.0);
            u = u.AddSeconds(30);
        }
        T.Check(st2.Box.Valid, "1.0x the mean is in band");
    }

    private static void ArmingDoesNotSpendTheEdge()
    {
        T.Section("Box — BoxArmsPerEdge arms per edge per box, cooldown, inside-close reset");

        var cfg = Cfg();                    // ArmsPerEdge 2, ArmCooldown 3, TriggerLife 1
        cfg.BoxMeanSamples = 1;
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);
        Seed(st, 1.0);

        DateTime t = Open;
        for (int i = 0; i < 6; i++)         // seals a valid 100.5 / 99.5 box on bar 6
        {
            Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
            t = t.AddSeconds(30);
        }
        T.Check(st.Box != null && st.Box.Valid, "a valid box exists");

        // Bar 7 — the first break. Close 101.00 is outside the edge but inside
        // the 1.0-point BoxDeadAtr tolerance, so the box survives to be armed
        // again.
        var a = Step(eng, t, 100.5, 101.25, 100.0, 101.0, 2.0);
        t = t.AddSeconds(30);
        T.Check(a.Fire, "a close beyond the edge arms");
        T.CheckInt(a.Dir, +1, "long");
        T.Check(!a.IsLimit, "the box entry is a stop, not a limit");
        T.Check(a.Engine == BbEntryEngine.Break, "engine stamped");
        T.CheckClose(a.TriggerPx, 101.50, "trigger = break bar high + TriggerOffsetTicks");
        T.CheckClose(a.BoxHigh, 100.5, "the action carries the box");
        T.CheckInt(a.BoxId, st.Box.Id, "and its id");
        T.CheckInt(st.ArmsUp, 1, "one arm spent on the up edge");

        // Bar 8 — still armed, nothing fires. One live trigger at a time is the
        // §4.1 half this engine owns.
        a = Step(eng, t, 101.0, 101.25, 100.6, 101.0, 2.0);
        T.Check(!a.Fire, "no second trigger while one is working");
        T.Check(st.Gate.Block == "armed", "and the gate says so (got '" + st.Gate.Block + "')");

        // `canTrade` false suppresses ARMING and nothing else (B2). The box is
        // still there, still valid, still aging — the engine simply may not act.
        var st2 = new BbEngineState();
        var eng2 = new BbEngine(cfg, st2);
        Seed(st2, 1.0);
        DateTime u = Open;
        for (int i = 0; i < 6; i++)
        {
            eng2.OnBar(Bar(u, 100.0, 100.5, 99.5, 100.0), Secs(u), u.Date, 2.0, true, false, false);
            u = u.AddSeconds(30);
        }
        T.Check(st2.Box != null && st2.Box.Valid, "the lifecycle ran with auto-trade off");

        var blocked = eng2.OnBar(Bar(u, 100.5, 101.25, 100.0, 101.0), Secs(u), u.Date,
                                 2.0, true, false, false);
        T.Check(!blocked.Fire, "a break does not arm while canTrade is false");
        T.Check(st2.Gate.Block == "auto-trade", "and the gate names it (got '" + st2.Gate.Block + "')");
        T.CheckInt(st2.ArmsUp, 0, "no arm was spent");

        // Every depth any Gate.Set passes must index a real rung. Cheap, and it
        // catches the one-sided edit that would otherwise only show up as a
        // mislabelled row on a chart.
        T.CheckInt(BbEngine.GateLadder.Length, 12, "the box ladder has 12 rungs");
        T.Check(BbEngine.GateLadder[10] == "cooldown", "cooldown is its own rung, not sharing 9 with arms");
        T.Check(BbEngine.GateLadder[11] == "direction off", "direction off is appended, not inserted");
    }

    // Task veto: `canTrade` moved from an early gate at rung 4 to a FINAL veto
    // checked after the whole ladder runs (window/budget/armed/break/arms/
    // cooldown). Pins the property `ArmingDoesNotSpendTheEdge` already covers
    // at rung 4 (a firing setup reports auto-trade) PLUS the one that fails
    // against the pre-veto code: a setup blocked at a DEEPER rung must report
    // THAT rung with orders off, not freeze at rung 4 before ever reaching it.
    private static void AutoTradeIsAFinalVeto()
    {
        T.Section("Box — canTrade is a final veto, not an early gate");

        var cfg = Cfg();
        cfg.BoxMeanSamples = 1;
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);
        Seed(st, 1.0);

        DateTime t = Open;
        for (int i = 0; i < 6; i++)         // seals a valid 100.5 / 99.5 box on bar 6
        {
            Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
            t = t.AddSeconds(30);
        }
        T.Check(st.Box != null && st.Box.Valid, "a valid box exists");

        // Bar 7, orders off: close 100.2 stays INSIDE the box, so this bar
        // blocks at "break" (rung 8) — a rung the old early-gate canTrade
        // check never let a bar reach. Before this task every one of these
        // bars would have reported "auto-trade" regardless of the close.
        var inside = eng.OnBar(Bar(t, 100.2, 100.3, 100.1, 100.2), Secs(t), t.Date, 2.0, true, false, false);
        t = t.AddSeconds(30);
        T.Check(!inside.Fire, "an inside close does not fire");
        T.Check(st.Gate.Block == "break", "the break gate is named, not auto-trade (" + st.Gate.Block + ")");
        T.CheckInt(st.Gate.GateDepth, 8, "break keeps its own depth even though canTrade is false");
        T.CheckInt(st.ArmsUp, 0, "and nothing armed");

        // Bar 8, orders still off: a real close beyond the edge. Every other
        // rung passes, so THIS is the one case that reports auto-trade — the
        // setup that would otherwise have fired — and Arm() must not run: no
        // edge spent, no counter moved, exactly as ArmingDoesNotSpendTheEdge
        // already pins for the single-bar case.
        var broke = eng.OnBar(Bar(t, 100.5, 101.25, 100.0, 101.0), Secs(t), t.Date, 2.0, true, false, false);
        T.Check(!broke.Fire, "a break does not arm while canTrade is false");
        T.Check(st.Gate.Block == "auto-trade", "and the gate names it (got '" + st.Gate.Block + "')");
        T.CheckInt(st.Gate.GateDepth, 4, "auto-trade sits at depth 4");
        T.CheckInt(st.ArmsUp, 0, "no arm was spent — the deeper bar before it didn't open a hole either");
        T.CheckInt(st.TradesThisBox, 0, "no trade counted");
        T.Check(!st.Armed, "the engine state never armed");
    }

    // The direction gate has its own rung. Before this, AllowLong/AllowShort
    // were folded into the break test at rung 8, so an operator who disabled
    // Short read "close X inside Y/Z" — the break gate's message — for a bar
    // that DID break; the real reason was the toggle he flipped.
    private static void DirectionOffIsANamedBlocker()
    {
        T.Section("Box — AllowLong/AllowShort are their own rung, not folded into break");

        var cfg = Cfg();
        cfg.BoxMeanSamples = 1;
        cfg.AllowShort = false;
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);
        Seed(st, 1.0);

        DateTime t = Open;
        for (int i = 0; i < 6; i++)         // seals a valid 100.5 / 99.5 box on bar 6
        {
            Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
            t = t.AddSeconds(30);
        }
        T.Check(st.Box != null && st.Box.Valid, "a valid box exists");

        // Bar 7 — a real short break (close 99.00 < box low 99.50), inside the
        // 1.0-point BoxDeadAtr tolerance so the box itself survives to be
        // arm-tested, with Short disabled.
        var broke = Step(eng, t, 100.0, 100.5, 98.75, 99.0, 2.0);
        T.Check(!broke.Fire, "a disabled direction never arms");
        T.Check(st.Gate.Block == "direction off", "the gate names it, not 'break' (got '" + st.Gate.Block + "')");
        T.CheckInt(st.Gate.GateDepth, 11, "direction off sits at the appended rung");
        T.Check(st.Gate.BlockDetail == "short disabled", "and says which side (got '" + st.Gate.BlockDetail + "')");
        T.CheckInt(st.ArmsDn, 0, "nothing armed — a refused direction spends no edge");
        T.Check(!st.Armed, "the engine state never armed");
    }

    // The §6.2 half that Task 46 could not assert: every one of these steps
    // needs a trigger to EXPIRE, and until AgeTrigger exists the engine stays
    // armed forever and the box is never re-armable.
    private static void CooldownAndArmCapAcrossExpiries()
    {
        T.Section("Box — cooldown between arms, a hard cap per edge, and the inside-close refill");

        var cfg = Cfg();                    // ArmsPerEdge 2, ArmCooldown 3, TriggerLife 1
        cfg.BoxMeanSamples = 1;
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);
        Seed(st, 1.0);

        DateTime t = Open;
        for (int i = 0; i < 6; i++)         // seals a valid 100.5 / 99.5 box on bar 6
        {
            Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
            t = t.AddSeconds(30);
        }

        // Bar 7 — the first break arms. Close 101.00 is outside the edge but
        // inside the 1.0-point BoxDeadAtr tolerance, so the box survives.
        var a = Step(eng, t, 100.5, 101.25, 100.0, 101.0, 2.0);
        t = t.AddSeconds(30);
        T.Check(a.Fire && st.ArmsUp == 1, "armed once");

        // Bar 8 — still working. Bar 9 — the trigger expires (TriggerLife 1),
        // but the cooldown is 3 bars from the ARM, so the edge is not re-armed
        // on the spot: that is what stops a sustained break re-arming every bar.
        Step(eng, t, 101.0, 101.25, 100.6, 101.0, 2.0);
        t = t.AddSeconds(30);
        a = Step(eng, t, 101.0, 101.25, 100.6, 101.0, 2.0);
        t = t.AddSeconds(30);
        T.Check(!a.Fire, "expiry does not re-arm inside the cooldown");
        T.Check(st.Gate.Block == "cooldown", "the gate names the cooldown (got '" + st.Gate.Block + "')");

        // Bar 10 — cooldown served. The EDGE was not spent by the first arm:
        // v1's boolean latch would have burned the box here without a trade.
        a = Step(eng, t, 101.0, 101.25, 100.6, 101.0, 2.0);
        t = t.AddSeconds(30);
        T.Check(a.Fire, "the second arm of the edge fires");
        T.CheckInt(st.ArmsUp, 2, "two arms spent");

        // Bars 11-13 — expire, serve the cooldown, and find the budget gone.
        for (int i = 0; i < 3; i++)
        {
            a = Step(eng, t, 101.0, 101.25, 100.6, 101.0, 2.0);
            t = t.AddSeconds(30);
        }
        T.Check(!a.Fire, "BoxArmsPerEdge is a hard cap per edge per box");
        T.Check(st.Gate.Block == "arms", "the gate names it (got '" + st.Gate.Block + "')");

        // A close back INSIDE the sealed box refills both counters.
        Step(eng, t, 101.0, 101.0, 99.8, 100.0, 2.0);
        t = t.AddSeconds(30);
        T.CheckInt(st.ArmsUp, 0, "an inside close of the sealed box resets the arm counters");

        a = Step(eng, t, 100.0, 101.25, 99.9, 101.0, 2.0);
        T.Check(a.Fire, "and the edge is armable again");
    }

    private static void ExpirySpendsAnArmRejectionRefundsIt()
    {
        T.Section("Box — expiry spends an arm, a refusal refunds it");

        var cfg = Cfg();
        cfg.BoxMeanSamples = 1;
        cfg.TriggerLife = 2;
        // Widened from Cfg()'s default of 3. AgeTrigger fires on
        // TriggerArmedBars > TriggerLife, so a TriggerLife of 2 expires on the
        // THIRD bar after the arm (bars 8, 9, 10 count 1, 2, 3) — the same bar
        // the default 3-bar cooldown would also clear on. Left at 3 the engine
        // auto-re-arms on that same bar, in the same OnBar call that just
        // expired the first trigger, before this test ever gets to exercise a
        // deliberate refusal. Widening it keeps the two clocks from colliding
        // so "expired, not yet re-armed" is an actual observable state.
        cfg.BoxArmCooldown = 6;
        var st = new BbEngineState();
        var eng = new BbEngine(cfg, st);
        Seed(st, 1.0);

        DateTime t = Open;
        for (int i = 0; i < 6; i++)
        {
            Step(eng, t, 100.0, 100.5, 99.5, 100.0, 2.0);
            t = t.AddSeconds(30);
        }
        var a = Step(eng, t, 100.5, 101.25, 100.0, 101.0, 2.0);
        t = t.AddSeconds(30);
        T.Check(a.Fire && st.Armed, "armed");

        // The ENGINE owns the clock and the shell mirrors it (B6). Two clocks —
        // one counting from arm, one from submit — is how v1 ended up believing
        // it was disarmed while a stop order still rested at the exchange.
        // Three bars, not two: TriggerArmedBars must exceed TriggerLife (2), so
        // it takes bars 8, 9 AND 10 (1, 2, 3) before AgeTrigger expires it.
        for (int i = 0; i < 3; i++)
        {
            Step(eng, t, 101.0, 101.25, 100.6, 101.0, 2.0);
            t = t.AddSeconds(30);
        }
        T.Check(!st.Armed, "the engine expires its own trigger after TriggerLife");
        T.CheckInt(st.ArmsUp, 1, "expiry SPENDS the arm — the market declined a live trigger");

        // Idempotent: the shell mirrors the same expiry and must not
        // double-count it.
        eng.OnTriggerExpired();
        T.CheckInt(st.ArmsUp, 1, "the shell's mirrored expiry is a no-op");

        // A refusal is OURS, not the market's: nothing was offered, so the arm
        // comes back. The cooldown does not — that is what stops a refusal loop
        // from re-arming on the very next bar.
        for (int i = 0; i < 3; i++)
        {
            a = Step(eng, t, 101.0, 101.25, 100.6, 101.0, 2.0);
            t = t.AddSeconds(30);
        }
        T.Check(a.Fire, "re-armed after the cooldown");
        T.CheckInt(st.ArmsUp, 2, "two arms spent");
        eng.OnEntryRejected("qty<1");
        T.Check(!st.Armed, "a refusal disarms");
        T.CheckInt(st.ArmsUp, 1, "and refunds the arm");

        // A FILL is what costs budget — not a submit.
        eng.OnEntryFilled();
        T.CheckInt(st.TradesThisBox, 1, "the fill costs box budget");
        T.CheckInt(st.TradesToday, 1, "and daily budget");
    }
}
