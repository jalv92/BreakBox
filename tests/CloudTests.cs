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
        PullbackLimitMode();
        RegimeClearsThreeWays();
        TokenMintAndElseIf();
        TokenKills();
        TokenRestoreOnExpiryAndRejection();
        GoldCandleGatesLong();
        GoldCandleGatesShort();
        TriggerAndTokenOwnership();
        CanTradeBoundaryAndCooldown();
        AutoTradeIsAFinalVeto();
        DirectionGatesHonourAllowFlags();
    }

    private static readonly DateTime Open = new DateTime(2026, 8, 3, 18, 0, 0);

    private static DateTime Tm(int i) { return Open.AddSeconds(30 * i); }
    private static int Secs(DateTime t) { return t.Hour * 3600 + t.Minute * 60 + t.Second; }

    private static BbBar Bar(int i, double o, double h, double l, double c)
    {
        return new BbBar { Time = Tm(i), Open = o, High = h, Low = l, Close = c, Volume = 100 };
    }

    // The alternative entry: rest a LIMIT on the far ribbon edge the moment the
    // token qualifies, instead of a STOP above whatever bar reclaimed it. A
    // measured run put the breakout fill 65 points above the pullback low because
    // the reclaim bar was 2.5 ATR tall.
    private static void PullbackLimitMode()
    {
        T.Section("Cloud — the pullback LIMIT entry (§5.2 step 6, alternative)");

        var cfg = Cfg();
        cfg.PullbackLimitEntry = true;
        cfg.MinPullback = 1;
        cfg.MinBarsBetween = 0;
        var st = new BbCloudState();
        var eng = new BbCloud(cfg, st);
        int i = Uptrend(eng, 0, 10, 100.0, 4.0);

        Pull(eng, i, 105.0, 107.0, 106.5, 106.6, 106.9, 4.0);       // low touches eS
        T.Check(st.Armed, "the touch mints the token");
        T.CheckClose(st.Ext, 106.6, "ext is the touch bar's low");

        // This bar would FAIL the reclaim gate — it closes below eF — and its low
        // is ABOVE the token's extreme, so the two candidate stop levels differ.
        var a = Pull(eng, i + 1, 105.5, 107.4, 107.2, 106.7, 106.8, 4.0);
        T.Check(a.Fire, "the limit fires with no gold candle to qualify");
        T.Check(a.IsLimit, "and it is a LIMIT, not a stop");
        T.CheckClose(a.TriggerPx, 107.5, "it rests on eS, the same edge the touch test uses");
        T.CheckInt(a.Dir, 1, "long, from the latched regime");
        T.CheckClose(a.SignalBarLow, 106.6, "the stop is priced off the PULLBACK extreme, not this bar's low");

        // The mode is the whole difference: the identical sequence in breakout
        // mode is refused by the reclaim gate.
        var cfgB = Cfg();
        cfgB.MinPullback = 1;
        cfgB.MinBarsBetween = 0;
        var stB = new BbCloudState();
        var engB = new BbCloud(cfgB, stB);
        int j = Uptrend(engB, 0, 10, 100.0, 4.0);
        Pull(engB, j, 105.0, 107.0, 106.5, 106.6, 106.9, 4.0);
        var b = Pull(engB, j + 1, 105.5, 107.4, 107.2, 106.7, 106.8, 4.0);
        T.Check(!b.Fire, "breakout mode refuses the same bar");
        T.Check(stB.Gate.Block == "reclaim", "and names the reclaim gate as the blocker");

        // Both vetoes still bind in limit mode.
        var cfgC = Cfg();
        cfgC.PullbackLimitEntry = true;
        cfgC.MinPullback = 1;
        cfgC.MinBarsBetween = 0;
        cfgC.AllowLong = false;
        var stC = new BbCloudState();
        var engC = new BbCloud(cfgC, stC);
        int k = Uptrend(engC, 0, 10, 100.0, 4.0);
        Pull(engC, k, 105.0, 107.0, 106.5, 106.6, 106.9, 4.0);
        var c2 = Pull(engC, k + 1, 105.5, 107.4, 107.2, 106.7, 106.8, 4.0);
        T.Check(!c2.Fire, "a disabled direction still refuses a limit entry");
        T.Check(stC.Gate.Block == "direction off", "and says so");

        var cfgD = Cfg();
        cfgD.PullbackLimitEntry = true;
        cfgD.MinPullback = 1;
        cfgD.MinBarsBetween = 0;
        var stD = new BbCloudState();
        var engD = new BbCloud(cfgD, stD);
        int m = Uptrend(engD, 0, 10, 100.0, 4.0);
        Pull(engD, m, 105.0, 107.0, 106.5, 106.6, 106.9, 4.0);
        var d = engD.OnBar(Bar(m + 1, 107.3, 107.5, 106.7, 106.8), Secs(Tm(m + 1)),
                           107.2, 107.4, 105.5, 4.0, true, false, false);
        T.Check(!d.Fire, "auto-trade off still refuses a limit entry");
        T.Check(stD.Gate.Block == "auto-trade", "and reports it at its own rung");
    }

    private static BbCloudConfig Cfg()
    {
        var c = new BbCloudConfig();
        c.TickSize = 0.25;
        c.TrendSlopeLookback = 5;       // short so the ring fills in 6 bars, not 11
        c.TrendSlopeAtr = 0.15;
        c.RegimeMemory = 50;
        c.PullbackMax = 50;
        // Every section below this one pins the BREAKOUT entry: a stop beyond the
        // reclaim bar. The engine's default is now the pullback limit, so the mode
        // is stated here rather than inherited — a default flip must not silently
        // retarget the tests that measure the other path.
        c.PullbackLimitEntry = false;
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

        // Sixth push fills a 6-slot ring, so the warmup gate clears. This same
        // bar also latches the regime (its slope trivially clears TrendSlopeAtr).
        // With no pullback token armed yet, the ladder now falls through to the
        // token gate (Task 22) rather than reporting empty — an empty Block here
        // would be exactly the "READY next to (out of band)" defect §9.1 warns
        // about, now that a real downstream blocker exists to name.
        Up(eng, 6, 103.0, 4.0);
        T.Check(st.Gate.Block != "warmup", "a full ring clears the warmup block");
        T.CheckInt(st.RegimeLatched, +1, "and the same bar latches the regime");
        T.Check(st.Gate.Block == "token", "so the ladder now reports the real next blocker, not empty");

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

    private static void TokenMintAndElseIf()
    {
        T.Section("Cloud — token mint, and the three things the else-if buys");

        var cfg = Cfg();
        var st = new BbCloudState();
        var eng = new BbCloud(cfg, st);
        int i = Uptrend(eng, 0, 10, 100.0, 4.0);

        // (a) the touch bar itself. eS = 107.0, low 106.6 touches it.
        Pull(eng, i, 105.0, 107.0, 106.5, 106.6, 106.9, 4.0);
        T.Check(st.Armed, "a touch of the FAR edge mints the token");
        T.CheckInt(st.AgeBars, 0, "the touch bar has AgeBars == 0 — §2: never on the touch");
        T.CheckClose(st.Ext, 106.6, "ext is the touch bar's low");

        // (c) a re-touch DEEPENS ext but does NOT reset the clock. Without the
        // else-if, price riding the ribbon resets AgeBars forever and defeats
        // PullbackMax — the token never ages out and fires days later.
        Pull(eng, i + 1, 105.5, 107.5, 106.5, 107.2, 107.4, 4.0);
        T.CheckInt(st.AgeBars, 1, "a non-touch bar ages the token");
        Pull(eng, i + 2, 106.0, 108.0, 107.0, 106.1, 107.6, 4.0);
        T.CheckInt(st.AgeBars, 2, "a RE-touch ages it too — it does not reset the clock");
        T.CheckClose(st.Ext, 106.1, "and the re-touch deepens ext");

        // (b) ext is ASSIGNED on mint, never min()-ed into a stale value. Kill
        // this token, then mint a HIGHER one: a fold would leave 106.1 behind and
        // gate (e)'s leg would be measured from a price this pullback never saw.
        Pull(eng, i + 3, 106.0, 108.0, 107.0, 105.0, 105.5, 4.0);   // close < eT -> kill
        T.Check(!st.Armed, "closing through eT killed it");
        int j = Uptrend(eng, i + 4, 10, 110.0, 4.0);                 // re-latch long
        Pull(eng, j, 114.5, 116.5, 116.0, 116.2, 116.4, 4.0);
        T.Check(st.Armed, "a new touch mints a new token");
        T.CheckClose(st.Ext, 116.2, "ext is ASSIGNED, not min()-ed into the dead token's 106.1");
    }

    private static void TokenKills()
    {
        T.Section("Cloud — the token kills (§5.2 step 4)");

        // (1) close through eT against the latch.
        var st1 = new BbCloudState();
        var eng1 = new BbCloud(Cfg(), st1);
        int i = Uptrend(eng1, 0, 10, 100.0, 4.0);
        Pull(eng1, i, 105.0, 107.0, 106.5, 106.6, 106.9, 4.0);
        T.Check(st1.Armed, "armed");
        Pull(eng1, i + 1, 105.0, 107.0, 106.5, 104.0, 104.5, 4.0);
        T.Check(!st1.Armed, "a close through eT kills the token");
        T.Check(double.IsNaN(st1.Ext), "and ext goes NaN — 0.0 would pass gate (e) as a real price");
        // These exact bar values are RegimeClearsThreeWays' clear-#2 fixture:
        // "closed through eT against the latch" is the SAME predicate UpdateRegime
        // tests, and it runs before step 3/4 — so this bar clears the regime one
        // step earlier in the same OnBar call and the ladder reports the
        // shallower "regime" gate, not "token". The token is still provably dead
        // (both asserts above), just filed under the more fundamental reason.
        T.Check(st1.Gate.Block == "regime" && st1.Gate.GateDepth == 1, "the ladder says regime at depth 1, not an unreachable token/2");

        // (2) AgeBars > PullbackMax.
        var cfg2 = Cfg();
        cfg2.PullbackMax = 3;
        var st2 = new BbCloudState();
        var eng2 = new BbCloud(cfg2, st2);
        i = Uptrend(eng2, 0, 10, 100.0, 4.0);
        Pull(eng2, i, 105.0, 107.0, 106.5, 106.6, 106.9, 4.0);
        for (int k = 1; k <= 3; k++)
            Pull(eng2, i + k, 105.0, 107.0, 106.5, 107.4, 107.6, 4.0);
        T.Check(st2.Armed, "still armed at exactly PullbackMax");
        Pull(eng2, i + 4, 105.0, 107.0, 106.5, 107.4, 107.6, 4.0);
        T.Check(!st2.Armed && double.IsNaN(st2.Ext), "AgeBars > PullbackMax kills it");

        // (3) the latch FLIPPING sign. A long token in a short regime would fire
        // the wrong way with an ext that is a low.
        var st3 = new BbCloudState();
        var eng3 = new BbCloud(Cfg(), st3);
        i = Uptrend(eng3, 0, 10, 100.0, 4.0);
        Pull(eng3, i, 105.0, 107.0, 106.5, 106.6, 106.9, 4.0);
        T.Check(st3.Armed, "armed long");
        for (int k = 0; k < 8; k++)
        {
            double eT = 104.5 - 0.5 * k, eS = eT - 2.0, eF = eS - 2.0, c = eF - 2.0;
            eng3.OnBar(Bar(i + 1 + k, c + 1.0, eS - 1.0, c - 0.5, c), Secs(Tm(i + 1 + k)),
                       eF, eS, eT, 4.0, true, true, false);
        }
        T.CheckInt(st3.RegimeLatched, -1, "the latch flipped");
        T.Check(!st3.Armed && double.IsNaN(st3.Ext), "and the flip killed the long token");
    }

    private static void TokenRestoreOnExpiryAndRejection()
    {
        T.Section("Cloud — expiry and rejection RESTORE the token (§5.2 steps 7, 9)");

        // v1 burned the edge the moment a trigger armed: an expired, cancelled or
        // refused entry spent the box without ever trading it. That is defect B3,
        // and the same rule now applies to the cloud.
        var cfg = Cfg();
        var st = new BbCloudState();
        var eng = new BbCloud(cfg, st);
        int i = Uptrend(eng, 0, 10, 100.0, 4.0);

        Pull(eng, i, 105.0, 107.0, 106.5, 106.6, 106.9, 4.0);   // mint
        Pull(eng, i + 1, 105.5, 107.5, 106.5, 107.2, 107.4, 4.0); // age to 1
        T.Check(st.Armed && st.AgeBars == 1, "token armed, one bar old");

        // What step 6 does on a consumed trigger. Poked directly here because the
        // trigger itself lands in Task 24; this pins the contract it must honour.
        st.Armed = false;
        st.BarsSinceLastArm = 0;
        st.TriggerArmedBars = 3;

        eng.OnTriggerExpired();
        T.Check(st.Armed, "expiry RESTORES the token — it does not burn the edge (B3)");
        T.CheckClose(st.Ext, 106.6, "ext is preserved across the restore");
        T.CheckInt(st.AgeBars, 1, "and so is AgeBars — the pullback did not get younger");
        T.CheckInt(st.TriggerArmedBars, 0, "the trigger clock resets");

        st.Armed = false;                                        // consumed again
        eng.OnEntryRejected("qty<1");
        T.Check(st.Armed, "a refusal restores it too (every path in §11 B4)");

        // A flip must NOT restore: the flip already killed the token, so ext is
        // NaN and the restore has nothing to bring back. One guard, both cases.
        for (int k = 0; k < 8; k++)
        {
            double eT = 104.5 - 0.5 * k, eS = eT - 2.0, eF = eS - 2.0, c = eF - 2.0;
            eng.OnBar(Bar(i + 2 + k, c + 1.0, eS - 1.0, c - 0.5, c), Secs(Tm(i + 2 + k)),
                      eF, eS, eT, 4.0, true, true, false);
        }
        T.CheckInt(st.RegimeLatched, -1, "regime flipped short");
        eng.OnTriggerExpired();
        T.Check(!st.Armed, "a flipped regime does NOT restore a token pointing the other way");

        // And a FILLED token never comes back — a late reject after a fill would
        // resurrect a trade that already happened.
        var st2 = new BbCloudState();
        var eng2 = new BbCloud(Cfg(), st2);
        int j = Uptrend(eng2, 0, 10, 100.0, 4.0);
        Pull(eng2, j, 105.0, 107.0, 106.5, 106.6, 106.9, 4.0);
        T.Check(st2.Armed, "armed before the fill");
        eng2.OnEntryFilled();
        T.Check(!st2.Armed && double.IsNaN(st2.Ext), "a fill spends the token for good");
        eng2.OnEntryRejected("late reject");
        T.Check(!st2.Armed, "and a late refusal cannot resurrect it");
    }

    // Gate tests seed a live token directly instead of driving sixty bars
    // through the ribbon. The mint, the latch and the kill have their own tests
    // in Tasks 21-23; a gate test that depends on all three fails for three
    // reasons and diagnoses none of them.
    private static BbCloudConfig GateCfg()
    {
        var c = new BbCloudConfig();
        c.TickSize = 0.25;
        c.TrendSlopeLookback = 10;
        c.TrendSlopeAtr = 0.15;
        c.RegimeMemory = 30;
        c.PullbackMax = 20;
        c.PullbackLimitEntry = false;       // these sections measure the bar gates
        c.MinPullback = 1;
        c.MinBarsBetween = 6;
        c.CloseInRange = 0.60;
        c.MinBarRangeAtr = 0.20;      // ATR 4.00 -> 0.80 points
        c.MinLegAtr = 0.35;           // ATR 4.00 -> 1.40 points
        c.TriggerLife = 4;
        c.TriggerOffsetTicks = 1;
        return c;
    }

    // The cloud owns no clock — the shell passes `secs` and never reads
    // bar.Time — so these bars carry no timestamp on purpose.
    private static BbBar B(double o, double h, double l, double c)
    {
        return new BbBar { Time = DateTime.MinValue, Open = o, High = h, Low = l, Close = c, Volume = 100 };
    }

    // BUG FIX (brief defect, see task-24-25-report.md): the brief's LiveToken
    // seeded RegimeLatched/Armed/Ext directly but left the slope ring at
    // SlopeFilled == 0. With TrendSlopeLookback == 10 the ring is 11 slots, and
    // every gate test below drives exactly ONE OnBar call — so without this fix
    // step 1's warmup gate blocks every single case and no test ever reaches
    // the gate it names. Filling the ring flat at `eT` reproduces the fixture
    // the tests' own comments describe ("a flat eT means the instantaneous
    // regime reads 0") instead of just papering over the gap with an arbitrary
    // value that would let the instantaneous regime latch out from under
    // RegimeLatched and quietly stop the tests from proving what they claim to.
    private static BbCloud LiveToken(BbCloudConfig cfg, BbCloudState st, int dir, double ext, double eT)
    {
        st.RegimeLatched = dir;
        st.RegimeLatchedAgeBars = 0;
        st.Armed = true;
        st.Ext = ext;
        st.AgeBars = 3;               // past MinPullback, far short of PullbackMax
        st.BarsSinceLastArm = 99;     // no cooldown in the way
        var eng = new BbCloud(cfg, st);    // sizes st.SlopeBuf
        for (int k = 0; k < st.SlopeBuf.Length; k++)
            st.SlopeBuf[k] = eT;
        st.SlopeFilled = st.SlopeBuf.Length;
        return eng;
    }

    private static void GoldCandleGatesLong()
    {
        T.Section("Cloud — the five gold-candle gates, long");

        // Ribbon eF 101.00 / eS 100.50, trend eT 99.00, ATR 4.00. The token was
        // minted on a touch of eS and its extreme sits at 100.00. A flat eT
        // means the instantaneous regime reads 0 every bar — which is exactly
        // the case the latch exists for, so these tests also prove the gates
        // read `RegimeLatched` and never `regimeNow`.
        const double eF = 101.0, eS = 100.5, eT = 99.0, atr = 4.0;

        var cfg = GateCfg();
        var st = new BbCloudState();
        var c = LiveToken(cfg, st, +1, 100.0, eT);
        var a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(a.Fire, "all five gates pass");
        T.CheckInt(a.Dir, +1, "long, in the latched regime's direction");
        T.Check(string.IsNullOrEmpty(st.Gate.Block), "a firing bar leaves no blocker on the ladder");

        // (a) the identical bar under a ribbon it never reclaimed.
        st = new BbCloudState(); c = LiveToken(cfg, st, +1, 100.0, eT);
        a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36000, 103.5, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a close still inside the ribbon does not fire");
        T.Check(st.Gate.Block == "reclaim", "the ladder names the reclaim gate (" + st.Gate.Block + ")");
        T.CheckInt(st.Gate.GateDepth, 7, "reclaim sits at depth 7");

        // (b) reclaims the ribbon, but on a down bar.
        st = new BbCloudState(); c = LiveToken(cfg, st, +1, 100.0, eT);
        a = c.OnBar(B(103.0, 103.0, 101.0, 102.9), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a down close does not fire a long");
        T.Check(st.Gate.Block == "direction", "the ladder names the direction gate (" + st.Gate.Block + ")");
        T.CheckInt(st.Gate.GateDepth, 8, "direction sits at depth 8");

        // (c) same body, 2.1 points of upper wick: 1.90/4.00 = 0.475 < 0.60.
        st = new BbCloudState(); c = LiveToken(cfg, st, +1, 100.0, eT);
        a = c.OnBar(B(101.2, 105.0, 101.0, 102.9), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a bar that gave back half its range does not fire");
        T.Check(st.Gate.Block == "close-in-range", "the ladder names close-in-range (" + st.Gate.Block + ")");
        T.CheckInt(st.Gate.GateDepth, 9, "close-in-range sits at depth 9");

        // (d) a wickless 0.60-point bar against a 0.80-point floor.
        st = new BbCloudState(); c = LiveToken(cfg, st, +1, 100.0, eT);
        a = c.OnBar(B(101.9, 102.5, 101.9, 102.5), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a doji-sized reclaim does not fire");
        T.Check(st.Gate.Block == "bar range", "the ladder names the bar-range gate (" + st.Gate.Block + ")");
        T.CheckInt(st.Gate.GateDepth, 10, "bar range sits at depth 10");

        // (e) a qualifying bar whose leg from the pullback extreme is 1.30.
        st = new BbCloudState(); c = LiveToken(cfg, st, +1, 100.4, eT);
        a = c.OnBar(B(101.0, 101.7, 100.8, 101.65), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a 1.30-point leg misses the 1.40-point floor");
        T.Check(st.Gate.Block == "leg", "the ladder names the leg gate (" + st.Gate.Block + ")");
        T.CheckInt(st.Gate.GateDepth, 11, "leg sits at depth 11");

        // (e) with the extreme lost. Every comparison against NaN is false, so
        // without an explicit guard this bar fires on a token that is gone.
        st = new BbCloudState(); c = LiveToken(cfg, st, +1, double.NaN, eT);
        a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a NaN pullback extreme fails CLOSED, not open");
        T.Check(st.Gate.Block == "leg", "and it is reported as the leg gate (" + st.Gate.Block + ")");
    }

    private static void GoldCandleGatesShort()
    {
        T.Section("Cloud — the five gold-candle gates, short (a real mirror)");

        // Mirrored ribbon: eF 99.00 below eS 99.50 below eT 101.00, regime -1,
        // the token minted on a touch of eS from below with its extreme at
        // 100.00. Same ATR, same two floors.
        const double eF = 99.0, eS = 99.5, eT = 101.0, atr = 4.0;

        var cfg = GateCfg();
        var st = new BbCloudState();
        var c = LiveToken(cfg, st, -1, 100.0, eT);
        var a = c.OnBar(B(98.8, 99.0, 97.0, 97.1), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(a.Fire, "all five gates pass, short");
        T.CheckInt(a.Dir, -1, "short, in the latched regime's direction");

        // (a) a close that is still above the ribbon.
        st = new BbCloudState(); c = LiveToken(cfg, st, -1, 100.0, eT);
        a = c.OnBar(B(98.8, 99.0, 97.0, 97.1), 36000, 96.5, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a close above the ribbon does not fire a short");
        T.Check(st.Gate.Block == "reclaim", "reclaim, short (" + st.Gate.Block + ")");

        // (b) below the ribbon, but on an up bar.
        st = new BbCloudState(); c = LiveToken(cfg, st, -1, 100.0, eT);
        a = c.OnBar(B(97.0, 99.0, 97.0, 97.1), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "an up close does not fire a short");
        T.Check(st.Gate.Block == "direction", "direction, short (" + st.Gate.Block + ")");

        // (c) measured from the HIGH for a short: (99.00-97.10)/4.00 = 0.475.
        st = new BbCloudState(); c = LiveToken(cfg, st, -1, 100.0, eT);
        a = c.OnBar(B(98.8, 99.0, 95.0, 97.1), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a 2.1-point lower wick does not fire a short");
        T.Check(st.Gate.Block == "close-in-range", "close-in-range, short (" + st.Gate.Block + ")");

        // (d) 0.60 points of range against the same 0.80-point floor.
        st = new BbCloudState(); c = LiveToken(cfg, st, -1, 100.0, eT);
        a = c.OnBar(B(98.1, 98.1, 97.5, 97.5), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a doji-sized reclaim does not fire a short");
        T.Check(st.Gate.Block == "bar range", "bar range, short (" + st.Gate.Block + ")");

        // (e) the leg runs DOWN from the extreme: 99.60 - 98.30 = 1.30.
        st = new BbCloudState(); c = LiveToken(cfg, st, -1, 99.6, eT);
        a = c.OnBar(B(98.9, 99.2, 98.3, 98.35), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a 1.30-point leg misses the floor, short");
        T.Check(st.Gate.Block == "leg", "leg, short (" + st.Gate.Block + ")");
    }

    private static void TriggerAndTokenOwnership()
    {
        T.Section("Cloud — the trigger, and who spends the token");

        const double eF = 101.0, eS = 100.5, eT = 99.0, atr = 4.0;
        var cfg = GateCfg();

        var st = new BbCloudState();
        var c = LiveToken(cfg, st, +1, 100.0, eT);
        var a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(a.Fire, "the qualifying bar fires");
        // A STOP one tick beyond the signal bar's high. Both observed fills were
        // WORSE than the signal — that is a stop being taken out, not a limit
        // being hit, and a limit here is a different model that wins the
        // mean-reverting cases and loses every real continuation.
        T.CheckClose(a.TriggerPx, 103.25, "trigger sits one tick above the signal bar's high");
        T.Check(!a.IsLimit, "the cloud entry is a stop, not a limit");
        T.Check(a.Engine == BbEntryEngine.Cloud, "stamped as the cloud engine");
        T.CheckClose(a.SignalBarHigh, 103.0, "signal bar high feeds the Candle stop");
        T.CheckClose(a.SignalBarLow, 101.0, "signal bar low feeds the Candle stop");
        // §4.1: the panel must not read box fields on a cloud action, so they
        // are zeroed rather than left carrying whatever the struct had.
        T.CheckClose(a.BoxHigh, 0.0, "no box high on a cloud action");
        T.CheckClose(a.BoxLow, 0.0, "no box low on a cloud action");
        T.CheckInt(a.BoxId, 0, "no box id on a cloud action");

        // The action was RETURNED, not accepted. The shell may still refuse it
        // (§4.1 suppression, qty < 1, a platform rejection), so OnBar must not
        // have spent anything.
        T.Check(st.Armed, "returning an action does not consume the token");
        T.CheckClose(st.Ext, 100.0, "and it does not forget the pullback extreme");

        c.OnEntryFilled();
        T.Check(!st.Armed, "the FILL consumes the token");
        T.CheckInt(st.BarsSinceLastArm, 0, "and starts the cooldown");

        // Short mirror: one tick BELOW the signal bar's low.
        st = new BbCloudState(); c = LiveToken(cfg, st, -1, 100.0, 101.0);
        a = c.OnBar(B(98.8, 99.0, 97.0, 97.1), 36000, 99.0, 99.5, 101.0, atr, true, true, false);
        T.Check(a.Fire, "the short fires");
        T.CheckClose(a.TriggerPx, 96.75, "short trigger sits one tick below the signal bar's low");
        T.Check(a.Why == "cloud_short", "and says which engine and side it came from");

        // Suppressed by an open position: no action, and the token survives for
        // the next opportunity instead of being burned by a bar we could not act
        // on anyway.
        st = new BbCloudState(); c = LiveToken(cfg, st, +1, 100.0, eT);
        a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36000, eF, eS, eT, atr, true, true, true);
        T.Check(!a.Fire, "positioned suppresses the trigger");
        T.Check(st.Armed, "and leaves the token intact");
        T.Check(st.Gate.Block == "in trade", "ladder: in trade (" + st.Gate.Block + ")");
        T.CheckInt(st.Gate.GateDepth, 3, "in trade sits at depth 3");

        // Suppressed by AUTO-TRADE off / lockout / outside the window.
        st = new BbCloudState(); c = LiveToken(cfg, st, +1, 100.0, eT);
        a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36000, eF, eS, eT, atr, true, false, false);
        T.Check(!a.Fire, "canTrade == false suppresses the trigger");
        T.Check(st.Armed, "and leaves the token intact");
        T.Check(st.Gate.Block == "auto-trade", "ladder: auto-trade (" + st.Gate.Block + ")");
        T.CheckInt(st.Gate.GateDepth, 4, "auto-trade sits at depth 4");
    }

    private static void CanTradeBoundaryAndCooldown()
    {
        T.Section("Cloud — MinBarsBetween, and ten minutes with AUTO-TRADE off");

        const double eF = 101.0, eS = 100.5, eT = 99.0, atr = 4.0;
        var cfg = GateCfg();                    // MinBarsBetween = 6

        // Cooldown: the counter ticks at the top of the bar, so a state seeded
        // at 4 reads 5 on this bar and 6 on the next.
        var st = new BbCloudState();
        var c = LiveToken(cfg, st, +1, 100.0, eT);
        st.BarsSinceLastArm = 4;
        var a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a qualifying bar inside the cooldown does not fire");
        T.Check(st.Gate.Block == "cooldown", "ladder: cooldown (" + st.Gate.Block + ")");
        T.CheckInt(st.Gate.GateDepth, 6, "cooldown sits at depth 6");
        a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36060, eF, eS, eT, atr, true, true, false);
        T.Check(a.Fire, "and fires on the bar the cooldown expires");

        // The pullback-age floor: the touch bar itself can never fire (§5.2
        // step 3), and MinPullback is the floor above it.
        st = new BbCloudState(); c = LiveToken(cfg, st, +1, 100.0, eT);
        st.AgeBars = 0; cfg.MinPullback = 3;
        a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36000, eF, eS, eT, atr, true, true, false);
        T.Check(!a.Fire, "a token younger than MinPullback does not fire");
        T.Check(st.Gate.Block == "pullback age", "ladder: pullback age (" + st.Gate.Block + ")");
        T.CheckInt(st.Gate.GateDepth, 5, "pullback age sits at depth 5");
        cfg.MinPullback = 1;

        // §5.2 step 1b — the boundary that matters. Five bars with AUTO-TRADE
        // off must age the token and the cooldown exactly as if we were
        // trading; anything else means re-enabling resumes from stale state and
        // the first live bar is evaluated against a ten-minute-old picture.
        st = new BbCloudState(); c = LiveToken(cfg, st, +1, 100.0, eT);
        for (int i = 0; i < 5; i++)
        {
            a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36000 + 30 * i, eF, eS, eT, atr, true, false, false);
            T.Check(!a.Fire, "blackout bar " + (i + 1) + " does not fire");
        }
        T.CheckInt(st.RegimeLatched, +1, "the latch survived the blackout");
        T.Check(st.Armed, "the token survived the blackout");
        T.CheckInt(st.AgeBars, 8, "the token kept ageing while we could not trade");
        T.CheckInt(st.BarsSinceLastArm, 104, "and so did the cooldown");

        a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36150, eF, eS, eT, atr, true, true, false);
        T.Check(a.Fire, "re-enabling trades the very next qualifying bar");
        T.CheckInt(st.AgeBars, 9, "with no gap in the token's age");
    }

    // Task veto: `canTrade` moved from an early gate at rung 4 to a FINAL veto
    // checked after the whole ladder runs. These two asserts pin exactly that:
    // a setup blocked deeper than rung 4 must still report ITS rung with
    // orders off (the property that was structurally impossible before — the
    // old code returned at rung 4 before the leg gate ever ran), and a setup
    // that WOULD fire must suppress every arm/fire side-effect, not just Fire.
    private static void AutoTradeIsAFinalVeto()
    {
        T.Section("Cloud — canTrade is a final veto, not an early gate");

        const double eF = 101.0, eS = 100.5, eT = 99.0, atr = 4.0;
        var cfg = GateCfg();

        // Same fixture as GoldCandleGatesLong's leg case (depth 11), replayed
        // with orders off. Before this task, canTrade short-circuited at rung
        // 4 before "leg" ever ran, so this assert fails against the old code.
        var st = new BbCloudState();
        var c = LiveToken(cfg, st, +1, 100.4, eT);
        var a = c.OnBar(B(101.0, 101.7, 100.8, 101.65), 36000, eF, eS, eT, atr, true, false, false);
        T.Check(!a.Fire, "still does not fire with orders off");
        T.Check(st.Gate.Block == "leg", "the leg gate is named, not auto-trade (" + st.Gate.Block + ")");
        T.CheckInt(st.Gate.GateDepth, 11, "leg keeps its own depth even though canTrade is false");

        // A run of bars that would ALL fire, orders off throughout: the token
        // must survive and TriggerArmedBars — an arm/fire side-effect — must
        // stay untouched. Seeding it non-zero makes a silent reset visible.
        st = new BbCloudState();
        c = LiveToken(cfg, st, +1, 100.0, eT);
        st.TriggerArmedBars = 3;
        for (int i = 0; i < 4; i++)
        {
            a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36000 + 30 * i, eF, eS, eT, atr, true, false, false);
            T.Check(!a.Fire, "blocked bar " + (i + 1) + " does not fire with orders off");
            T.Check(st.Gate.Block == "auto-trade", "and reports auto-trade — this setup would have fired");
        }
        T.Check(st.Armed, "no token consumed while orders are off");
        T.CheckInt(st.TriggerArmedBars, 3, "no arm/fire side-effect ran under the veto");
    }

    // The panel's Buy/Sell toggles (BreakBoxPanel.cs _uiLongOn/_uiShortOn ->
    // BbCloudConfig.AllowLong/AllowShort). Before this fix the cloud engine
    // never read either field — Sell off stopped the BOX engine's shorts
    // (BreakBoxCore.cs ANDs them into the break condition) while the cloud,
    // the PRIMARY engine, kept shorting anyway, with nothing on the panel
    // showing it. These fixtures are GoldCandleGatesLong/Short's first case
    // — a bar that would otherwise fire clean — replayed with the opposite
    // side disabled.
    private static void DirectionGatesHonourAllowFlags()
    {
        T.Section("Cloud — AllowLong/AllowShort gate the final veto (panel Buy/Sell toggles)");

        const double eFLong = 101.0, eSLong = 100.5, eTLong = 99.0;
        const double eFShort = 99.0, eSShort = 99.5, eTShort = 101.0;
        const double atr = 4.0;

        // Sell off: a qualifying short does not fire, and the ladder names
        // the reason instead of leaving the operator to guess.
        var cfg = GateCfg();
        cfg.AllowLong = true;
        cfg.AllowShort = false;
        var st = new BbCloudState();
        var c = LiveToken(cfg, st, -1, 100.0, eTShort);
        var a = c.OnBar(B(98.8, 99.0, 97.0, 97.1), 36000, eFShort, eSShort, eTShort, atr, true, true, false);
        T.Check(!a.Fire, "AllowShort = false blocks a qualifying short setup");
        T.Check(st.Gate.Block == "direction off", "the ladder names the direction gate (" + st.Gate.Block + ")");
        T.CheckInt(st.Gate.GateDepth, 12, "direction off sits at the newest rung, 12");
        T.Check(st.Armed, "the token survives — a disabled direction suppresses, it does not kill (§4.1 pattern)");

        // Same config, opposite direction: Buy is still on. A gate that
        // blocked BOTH sides by accident would pass the assert above and
        // hide behind it — this is what catches that.
        st = new BbCloudState();
        c = LiveToken(cfg, st, +1, 100.0, eTLong);
        a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36000, eFLong, eSLong, eTLong, atr, true, true, false);
        T.Check(a.Fire, "AllowShort = false leaves long untouched");

        // Mirror: Buy off blocks a qualifying long, Sell still fires.
        cfg = GateCfg();
        cfg.AllowLong = false;
        cfg.AllowShort = true;
        st = new BbCloudState();
        c = LiveToken(cfg, st, +1, 100.0, eTLong);
        a = c.OnBar(B(101.2, 103.0, 101.0, 102.9), 36000, eFLong, eSLong, eTLong, atr, true, true, false);
        T.Check(!a.Fire, "AllowLong = false blocks a qualifying long setup");
        T.Check(st.Gate.Block == "direction off", "the ladder names the direction gate (" + st.Gate.Block + ")");

        st = new BbCloudState();
        c = LiveToken(cfg, st, -1, 100.0, eTShort);
        a = c.OnBar(B(98.8, 99.0, 97.0, 97.1), 36000, eFShort, eSShort, eTShort, atr, true, true, false);
        T.Check(a.Fire, "AllowLong = false leaves short untouched");
    }
}
