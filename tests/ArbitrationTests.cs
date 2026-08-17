// ArbitrationTests — the property the shell's §4.1 owner-routing rests on:
// disarming the engine that owns a working entry must never touch the OTHER
// engine's memory. Task 40 rewrote the box engine to v2; before that, the box
// side of this pairing was v1 and there was no way to stand up two v2 engines
// side by side, so nothing ever exercised this end to end.
//
// What this file CANNOT do: run the shell's own routing (CancelWorkingEntry /
// OnEntryRejected in BreakBoxStrategy.cs, gated on `_owningEngine`). That code
// lives in a file with `using NinjaTrader.*` and this runner has no NT8
// assemblies on its reference path — scripts/check.sh's "compiles clean" is
// the only executable check on that half, and the three call sites were
// verified by reading (see task-48-49-report.md).
//
// What this file CAN do, and does: BbEngine and BbCloud each read and write
// only their OWN state object (BbEngineState / BbCloudState respectively) —
// neither takes a reference to the other. Driving both to a genuinely armed
// state side by side, the way the shell does, and disarming only ONE proves
// the two can never leak into each other's memory. That is the necessary
// condition the shell's if/else dispatch depends on to be safe.
using System;
using BreakBoxCore;

public static class ArbitrationTests
{
    public static void Run()
    {
        DisarmingOneEngineLeavesTheOtherUntouched();
    }

    private static readonly DateTime Open = new DateTime(2026, 8, 3, 9, 30, 0);
    private static DateTime Tm(int i) { return Open.AddSeconds(30 * i); }
    private static int Secs(DateTime t) { return t.Hour * 3600 + t.Minute * 60 + t.Second; }

    private static BbBar Bar(int i, double o, double h, double l, double c)
    {
        return new BbBar { Time = Tm(i), Open = o, High = h, Low = l, Close = c, Volume = 100 };
    }

    // ---- Box side. Recipe lifted verbatim from BoxTests.ArmingDoesNotSpendTheEdge
    // and .CooldownAndArmCapAcrossExpiries: seed one sealed box so the first real
    // seal rates a valid 1.0x ratio, seal it on bar 6, arm on the break at bar 7.
    private static BbConfig BoxCfg()
    {
        var c = new BbConfig();
        c.TickSize = 0.25;
        c.BoxLookback = 4;
        c.BoxMinBars = 2;
        c.BoxRangePctile = 50.0;
        c.BoxSampleN = 200;
        c.BoxMeanSamples = 1;
        c.BoxValidLo = 0.4;
        c.BoxValidHi = 2.5;
        c.BoxDeadAtr = 0.5;
        c.BoxMaxAge = 500;
        c.BoxArmsPerEdge = 2;
        c.BoxArmCooldown = 3;
        c.TriggerOffsetTicks = 1;
        c.TriggerLife = 5;         // wide: this test disarms by hand, never by AgeTrigger
        c.EntryWindowStartHhmm = 930;
        c.EntryWindowEndHhmm = 1545;
        return c;
    }

    private static BbAction BoxStep(BbEngine eng, int i, double o, double h, double l, double c, double atr)
    {
        return eng.OnBar(Bar(i, o, h, l, c), Secs(Tm(i)), Tm(i).Date, atr, true, true, false);
    }

    // ---- Cloud side. Recipe lifted verbatim from CloudTests.TokenMintAndElseIf:
    // ten clean uptrend bars latch the regime, then one touch of the far ribbon
    // edge (eS) mints the token.
    private static BbCloudConfig CloudCfg()
    {
        var c = new BbCloudConfig();
        c.TickSize = 0.25;
        c.TrendSlopeLookback = 5;
        c.TrendSlopeAtr = 0.15;
        c.RegimeMemory = 50;
        c.PullbackMax = 50;
        return c;
    }

    private static void CloudUp(BbCloud eng, int i, double eT, double atr)
    {
        double eS = eT + 2.0, eF = eS + 2.0, c = eF + 2.0;
        eng.OnBar(Bar(i, c - 1.0, c + 0.5, eS + 1.0, c), Secs(Tm(i)), eF, eS, eT, atr, true, true, false);
    }

    private static void CloudPull(BbCloud eng, int i, double eT, double eS, double eF,
                                   double low, double close, double atr)
    {
        eng.OnBar(Bar(i, close + 0.5, close + 0.7, low, close), Secs(Tm(i)), eF, eS, eT, atr, true, true, false);
    }

    private static void DisarmingOneEngineLeavesTheOtherUntouched()
    {
        T.Section("Arbitration — disarming one engine's trigger cannot touch the other's memory (§4.1)");

        // Arm BOTH engines, independently, the way the shell drives them.
        var boxSt = new BbEngineState();
        var boxEng = new BbEngine(BoxCfg(), boxSt);
        boxEng.SeedSealedRange(1.0);
        for (int i = 0; i < 6; i++)                                       // seals a 100.5/99.5 box on bar 6
            BoxStep(boxEng, i, 100.0, 100.5, 99.5, 100.0, 2.0);
        var boxFire = BoxStep(boxEng, 6, 100.5, 101.25, 100.0, 101.0, 2.0); // bar 7: breaks up, arms
        T.Check(boxFire.Fire && boxSt.Armed, "the box armed");

        var cloudSt = new BbCloudState();
        var cloudEng = new BbCloud(CloudCfg(), cloudSt);
        for (int i = 0; i < 10; i++)
            CloudUp(cloudEng, i, 100.0 + 0.5 * i, 4.0);
        CloudPull(cloudEng, 10, 105.0, 107.0, 106.5, 106.6, 106.9, 4.0);   // mints the token
        T.Check(cloudSt.Armed, "the cloud minted a token");

        // Snapshot the cloud BEFORE the box's expiry — the check below is
        // "unchanged", so it has to know what "unchanged" means.
        bool cArmed = cloudSt.Armed;
        double cExt = cloudSt.Ext;
        int cAge = cloudSt.AgeBars, cSinceArm = cloudSt.BarsSinceLastArm, cTrigBars = cloudSt.TriggerArmedBars;
        int cRegime = cloudSt.RegimeLatched;

        // §4.1's Box branch — CancelWorkingEntry(why, engineDisarmed:true) with
        // _owningEngine == Break calls exactly this. The cloud is never touched.
        boxEng.OnTriggerExpired();
        T.Check(!boxSt.Armed, "the box's own trigger disarmed");
        T.CheckInt(boxSt.ArmsUp, 1, "expiry SPENDS the box's arm");
        T.Check(cloudSt.Armed == cArmed, "the cloud's Armed flag is untouched by the box's expiry");
        T.CheckClose(cloudSt.Ext, cExt, "and its Ext");
        T.CheckInt(cloudSt.AgeBars, cAge, "and its AgeBars");
        T.CheckInt(cloudSt.BarsSinceLastArm, cSinceArm, "and its BarsSinceLastArm");
        T.CheckInt(cloudSt.TriggerArmedBars, cTrigBars, "and its TriggerArmedBars");
        T.CheckInt(cloudSt.RegimeLatched, cRegime, "and its RegimeLatched");

        // The mirror: re-arm the box (cooldown is 3 bars from the first arm, at
        // BarCount 7), snapshot it, then disarm only the CLOUD.
        BoxStep(boxEng, 7, 101.0, 101.25, 100.6, 101.0, 2.0);   // BarCount 8, since=1: cooldown blocks
        BoxStep(boxEng, 8, 101.0, 101.25, 100.6, 101.0, 2.0);   // BarCount 9, since=2: still blocked
        var rearm = BoxStep(boxEng, 9, 101.0, 101.25, 100.6, 101.0, 2.0); // BarCount 10, since=3: arms
        T.Check(rearm.Fire && boxSt.Armed, "the box re-armed for the mirror half");

        bool bArmed = boxSt.Armed;
        int bArmsUp = boxSt.ArmsUp, bArmDir = boxSt.ArmDir;
        double bTrigPx = boxSt.ArmTriggerPx;

        // §4.1's Cloud branch — the same dispatch with _owningEngine == Cloud
        // calls exactly this. The box is never touched.
        cloudEng.OnTriggerExpired();
        T.Check(cloudSt.Armed, "the cloud's token is RESTORED by its own expiry (unlike the box, which spends)");
        T.Check(boxSt.Armed == bArmed, "the box's Armed flag is untouched by the cloud's expiry");
        T.CheckInt(boxSt.ArmsUp, bArmsUp, "and its ArmsUp");
        T.CheckInt(boxSt.ArmDir, bArmDir, "and its ArmDir");
        T.CheckClose(boxSt.ArmTriggerPx, bTrigPx, "and its ArmTriggerPx");
    }
}
