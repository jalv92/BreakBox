// BracketTests — the stop, the R-multiple tiers, the split, breakeven and the
// trail.
//
// The first two sections are FIDELITY tests: they replay the exact numbers
// measured off the target's own screenshots (docs/research/01-target-analysis.md
// §4.1 and §4.2) and require this bracket to produce them. They are the only
// tests in the repo whose expected values come from outside the repo, and they
// are the reason the bracket is written the way it is. If one of them fails,
// something changed the geometry the whole project was reverse-engineered from.
using System;
using BreakBoxCore;

public static class BracketTests
{
    public static void Run()
    {
        FidelityImage1();
        FidelityImage2Splits();
        StopSources();
        AtrClamp();
        SplitEdges();
        Breakeven();
        Trail();
    }

    private static BbExitConfig Cfg()
    {
        var c = new BbExitConfig();
        c.TickSize = 0.25;
        c.StopSource = BbStopSource.Manual;
        c.StopMinAtr = 0.0;         // fidelity replays measure the geometry, not the sanity band
        c.StopMaxAtr = 1e9;
        return c;
    }

    private static BbStopInputs NoStructure()
    {
        var i = new BbStopInputs();
        i.SignalBarHigh = double.NaN;
        i.SignalBarLow = double.NaN;
        i.LastSwingHigh = double.NaN;
        i.LastSwingLow = double.NaN;
        i.MaValue = double.NaN;
        i.EmaValue = double.NaN;
        return i;
    }

    // Image (1), NQ short, entry 23713.75 / stop 23729.25 -> R = 15.50 pts,
    // multiples 0.40 / 0.80 / 1.50. The chart drew 23707.55 / 23701.35 /
    // 23690.50 and the TP2 order FILLED at 23701.25 — i.e. the levels are
    // computed off-grid and rounded to the tick when submitted. Reproducing that
    // fill is the strongest single check we have that the model is right.
    private static void FidelityImage1()
    {
        T.Section("Fidelity — image (1), NQ short, B_ config");

        var cfg = Cfg();
        cfg.ManualStopTicks = 62;               // 62 * 0.25 = 15.50
        cfg.TierCount = 3;
        cfg.Tp1R = 0.40; cfg.Tp2R = 0.80; cfg.Tp3R = 1.50;
        cfg.Tp1Pct = 50; cfg.Tp2Pct = 30;

        var br = new BbBracket();
        BbExits.OnEntryFill(cfg, br, -1, 23713.75, 7, 0.0, false, NoStructure());

        T.CheckClose(br.StopPx, 23729.25, "stop");
        T.CheckClose(br.R, 15.50, "R");
        T.CheckClose(br.TargetPx[0], 23707.50, "TP1 rounded (chart line 23707.55)");
        T.CheckClose(br.TargetPx[1], 23701.25, "TP2 == the observed FILL 23701.25");
        T.CheckClose(br.TargetPx[2], 23690.50, "TP3 on-grid, matches the chart exactly");

        // The 7-lot split observed on the chart: B_TP1 4+2... no — 4, then 2 at
        // TP2's price and 1 at TP3. 50/30/20 of 7 rounds to exactly 4/2/1.
        T.CheckInt(br.Tiers, 3, "three live tiers");
        T.CheckInt(br.TargetQty[0], 4, "TP1 qty (observed 4)");
        T.CheckInt(br.TargetQty[1], 2, "TP2 qty (observed 2)");
        T.CheckInt(br.TargetQty[2], 1, "TP3 qty (observed 1)");
        T.CheckInt(br.TargetQty[0] + br.TargetQty[1] + br.TargetQty[2], 7, "split covers the position");
    }

    // Image (2), the two A_ trades: a 10-lot taking 7 at TP1, and a 14-lot
    // splitting 8/6 across two tiers. Both are the SAME splitter with different
    // percentages, which is the finding: the ratio is configurable, not fixed.
    private static void FidelityImage2Splits()
    {
        T.Section("Fidelity — image (2), A_ splits");

        var cfg = Cfg();
        cfg.TierCount = 2;
        cfg.Tp1Pct = 70;
        var q = new int[BbExitConfig.MAX_TIERS];
        int tiers = BbExits.SplitTiers(cfg, 10, q);
        T.CheckInt(tiers, 2, "10-lot, two tiers");
        T.CheckInt(q[0], 7, "TP1 qty (observed 7 of 10)");
        T.CheckInt(q[1], 3, "runner qty 3");

        cfg.Tp1Pct = 57;
        tiers = BbExits.SplitTiers(cfg, 14, q);
        T.CheckInt(q[0], 8, "TP1 qty (observed 8 of 14)");
        T.CheckInt(q[1], 6, "TP2 qty (observed 6 of 14)");

        // R = 5.85 on that trade with TP1 at 0.50 R and TP2 at 1.00 R.
        cfg.TierCount = 2;
        cfg.Tp1R = 0.50; cfg.Tp2R = 1.00;
        cfg.ManualStopTicks = 117;              // 117 * 0.05 MNQ ticks... MNQ tick is 0.25 too
        var br = new BbBracket();
        // MNQ entry 26877.25, stop 5.85 below -> 26871.40, off-grid, rounds to 26871.50.
        cfg.StopSource = BbStopSource.Candle;
        var inp = NoStructure();
        inp.SignalBarLow = 26871.40;
        cfg.StopBufferTicks = 0;
        BbExits.OnEntryFill(cfg, br, +1, 26877.25, 14, 0.0, false, inp);
        T.CheckClose(br.StopPx, 26871.50, "MNQ stop rounds onto the grid");
        T.CheckClose(br.R, 5.75, "R after rounding");
        // 26877.25 + 0.5 * 5.75 = 26880.125, which rounds UP onto the grid.
        // The screenshot's own TP1 read 26879.9x, off an entry of 26877.00 and
        // an R of ~5.85 — i.e. the vendor anchors its levels on the SIGNAL
        // price, this bracket anchors them on the FILL. That is deliberate: R is
        // only real risk when it is measured from the price we actually got.
        T.CheckClose(br.TargetPx[0], 26880.25, "TP1 = entry + 0.50 R, rounded");
        T.CheckClose(br.TargetPx[1], 26883.00, "TP2 = entry + 1.00 R, rounded");
    }

    private static void StopSources()
    {
        T.Section("Stop sources");

        var cfg = Cfg();
        cfg.StopBufferTicks = 2;
        cfg.ManualStopTicks = 40;
        var inp = NoStructure();
        inp.SignalBarLow = 100.00;
        inp.SignalBarHigh = 102.00;
        inp.LastSwingLow = 99.00;
        inp.LastSwingHigh = 103.00;
        inp.MaValue = 100.50;
        inp.EmaValue = 98.00;
        string why;

        cfg.StopSource = BbStopSource.Candle;
        T.CheckClose(BbExits.SeedStop(cfg, +1, 101.00, 0, false, inp, out why), 99.50, "candle long = bar low - 2t");
        T.Check(why == "candle", "candle reports its source");

        cfg.StopSource = BbStopSource.Swing;
        T.CheckClose(BbExits.SeedStop(cfg, -1, 101.00, 0, false, inp, out why), 103.50, "swing short = swing high + 2t");

        cfg.StopSource = BbStopSource.Ema50;
        T.CheckClose(BbExits.SeedStop(cfg, +1, 101.00, 0, false, inp, out why), 97.50, "e50 long");

        // The wrong-side case: a 50-EMA ABOVE price on a long is an ordinary
        // market state, and using it would submit a stop already through the
        // market. It must fall back, and it must SAY it fell back.
        inp.EmaValue = 105.00;
        T.CheckClose(BbExits.SeedStop(cfg, +1, 101.00, 0, false, inp, out why), 91.00, "wrong-side e50 falls back to manual");
        T.Check(why == "manual_fallback", "the fallback is reported, not silent");

        // A missing source is the same case.
        cfg.StopSource = BbStopSource.Swing;
        inp.LastSwingLow = double.NaN;
        T.CheckClose(BbExits.SeedStop(cfg, +1, 101.00, 0, false, inp, out why), 91.00, "missing swing falls back");
    }

    private static void AtrClamp()
    {
        T.Section("ATR sanity band");

        var cfg = Cfg();
        cfg.StopSource = BbStopSource.Candle;
        cfg.StopBufferTicks = 0;
        cfg.StopMinAtr = 0.5;
        cfg.StopMaxAtr = 3.0;
        var inp = NoStructure();
        string why;

        // Structure one tick away, ATR 4.00 -> floored to 2.00.
        inp.SignalBarLow = 100.75;
        T.CheckClose(BbExits.SeedStop(cfg, +1, 101.00, 4.00, true, inp, out why), 99.00, "stop floored at 0.5 ATR");
        T.Check(why.EndsWith("_min", StringComparison.Ordinal), "floor is reported");

        // Structure 20 points away, ATR 4.00 -> capped at 12.00.
        inp.SignalBarLow = 81.00;
        T.CheckClose(BbExits.SeedStop(cfg, +1, 101.00, 4.00, true, inp, out why), 89.00, "stop capped at 3 ATR");
        T.Check(why.EndsWith("_max", StringComparison.Ordinal), "cap is reported");

        // A cold ATR leaves the structural stop alone rather than clamping to a
        // half-warm number.
        T.CheckClose(BbExits.SeedStop(cfg, +1, 101.00, 4.00, false, inp, out why), 81.00, "cold ATR does not clamp");
    }

    private static void SplitEdges()
    {
        T.Section("Split edges");

        var cfg = Cfg();
        cfg.TierCount = 3;
        cfg.Tp1Pct = 50; cfg.Tp2Pct = 30;
        var q = new int[BbExitConfig.MAX_TIERS];

        T.CheckInt(BbExits.SplitTiers(cfg, 1, q), 1, "1 lot collapses to one tier");
        T.CheckInt(q[0], 1, "1 lot all on tier 1");

        T.CheckInt(BbExits.SplitTiers(cfg, 2, q), 2, "2 lots -> two tiers");
        T.CheckInt(q[0] + q[1], 2, "2 lots covered");
        T.Check(q[0] >= 1 && q[1] >= 1, "no zero-quantity tier");

        BbExits.SplitTiers(cfg, 3, q);
        T.CheckInt(q[0] + q[1] + q[2], 3, "3 lots covered");
        T.Check(q[0] >= 1 && q[1] >= 1 && q[2] >= 1, "every live tier gets a contract");

        BbExits.SplitTiers(cfg, 100, q);
        T.CheckInt(q[0], 50, "100 lots 50/30/20");
        T.CheckInt(q[1], 30, "100 lots tier 2");
        T.CheckInt(q[2], 20, "100 lots tier 3");

        T.CheckInt(BbExits.SplitTiers(cfg, 0, q), 0, "zero qty yields no tiers");
    }

    private static void Breakeven()
    {
        T.Section("Breakeven on TP1 fill");

        var cfg = Cfg();
        cfg.ManualStopTicks = 40;               // 10.00 points
        cfg.TierCount = 3;
        cfg.BreakevenOnTp1 = true;
        cfg.BreakevenOffsetTicks = 1;

        var br = new BbBracket();
        BbExits.OnEntryFill(cfg, br, +1, 100.00, 10, 0.0, false, NoStructure());
        T.CheckClose(br.StopPx, 90.00, "initial stop");

        var d = BbExits.OnTierFill(cfg, br, 0, br.TargetQty[0]);
        T.Check(d.StopMoved, "TP1 fill moves the stop");
        T.CheckClose(br.StopPx, 100.25, "stop parks 1 tick beyond entry");
        T.Check(br.BeApplied, "breakeven latched");

        // One-shot: a second tier-0 fill (a partial re-fill) must not move it again.
        br.StopPx = 101.00;                     // as if the trail had tightened
        d = BbExits.OnTierFill(cfg, br, 0, 0);
        T.Check(!d.StopMoved, "breakeven is one-shot");
        T.CheckClose(br.StopPx, 101.00, "a tightened stop is not walked back");

        // A hand-cancelled stop is never resurrected.
        var br2 = new BbBracket();
        BbExits.OnEntryFill(cfg, br2, +1, 100.00, 10, 0.0, false, NoStructure());
        br2.StopCancelled = true;
        d = BbExits.OnTierFill(cfg, br2, 0, 5);
        T.Check(!d.StopMoved, "a cancelled stop stays cancelled");
    }

    private static void Trail()
    {
        T.Section("Trail after TP2");

        var cfg = Cfg();
        cfg.ManualStopTicks = 40;
        cfg.TierCount = 3;
        cfg.TrailAfterTp2 = true;
        cfg.TrailAtrMult = 1.5;

        var br = new BbBracket();
        BbExits.OnEntryFill(cfg, br, +1, 100.00, 10, 0.0, false, NoStructure());

        var bar = new BbBar { High = 110.00, Low = 99.00, Close = 109.00 };
        var d = BbExits.OnBarClose(cfg, br, bar, 4.00);
        T.Check(!d.StopMoved, "no trail before TP2");
        T.CheckClose(br.Mfe, 110.00, "MFE tracks anyway");

        BbExits.OnTierFill(cfg, br, 0, br.TargetQty[0]);
        BbExits.OnTierFill(cfg, br, 1, br.TargetQty[1]);
        T.Check(br.TrailArmed, "TP2 arms the trail");

        d = BbExits.OnBarClose(cfg, br, bar, 4.00);
        T.Check(d.StopMoved, "trail moves once armed");
        T.CheckClose(br.StopPx, 104.00, "chandelier = MFE - 1.5 ATR");

        // Monotone: a lower candidate never wins.
        var pullback = new BbBar { High = 106.00, Low = 103.00, Close = 104.00 };
        d = BbExits.OnBarClose(cfg, br, pullback, 4.00);
        T.Check(!d.StopMoved, "the trail never goes backwards");
        T.CheckClose(br.StopPx, 104.00, "stop held");

        // A hand-loosened stop is re-tightened at the next close: the ratchet is
        // a rule about the algorithm, not about the operator.
        BbExits.AdoptManualStop(br, 95.00, false);
        d = BbExits.OnBarClose(cfg, br, pullback, 4.00);
        T.Check(d.StopMoved, "a loosened stop is re-tightened");
        T.CheckClose(br.StopPx, 104.00, "back to the chandelier price");

        // Coverage accounting: two tiers filled, the runner is still covered.
        T.CheckInt(BbExits.CoveredQty(br), br.TargetQty[2], "covered qty == the runner");
        T.CheckInt(br.QtyOpen, br.TargetQty[2], "open qty agrees");
    }
}
