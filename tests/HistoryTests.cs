// HistoryTests — the trade journal's wire format, the equity reduction, the
// config digest and the write guard.
//
// The write guard has a test for the same reason it exists: one optimisation
// sweep appending 40,000 rows to the live curve destroys the only data the
// history feature is for, and it destroys it silently. A rule that expensive
// does not live in an impure file where nothing can assert it.
using System;
using System.Collections.Generic;
using BreakBoxCore;

public static class HistoryTests
{
    public static void Run()
    {
        RoundTrip();
        EquityAndHash();
        WriteGuard();
        GateLadder();
        LogRing();
        ChartMath();
        Cap();
    }

    private static BbTradeRecord Rec()
    {
        BbTradeRecord r = default(BbTradeRecord);
        r.Ts = new DateTime(2026, 8, 16, 14, 37, 30);
        r.Dir = -1;
        r.Entry = 29867.25;
        r.Exit = 29873.5;
        r.Qty = 3;
        r.R = -1.0;
        r.Pnl = -50.5;
        r.Engine = "Cloud";
        r.ExitReason = "BB_Stop";
        r.CfgHash = "a1b2c3d4";
        return r;
    }

    private static void RoundTrip()
    {
        T.Section("History — JSONL round trip");

        BbTradeRecord a = Rec();
        string line = BbHistory.Serialise(a);
        T.Check(line.IndexOf('\n') < 0 && line.IndexOf('\r') < 0, "one record is exactly one line");

        BbTradeRecord b;
        T.Check(BbHistory.TryParse(line, out b), "its own output parses");
        T.Check(a.Ts == b.Ts, "ts survives");
        T.CheckInt(b.Dir, -1, "dir survives");
        T.CheckClose(b.Entry, 29867.25, "entry survives");
        T.CheckClose(b.Exit, 29873.5, "exit survives");
        T.CheckInt(b.Qty, 3, "qty survives");
        T.CheckClose(b.R, -1.0, "R survives");
        // The negative case has its own assert because a loss is the row a
        // sign bug hides in: the equity curve still LOOKS like a curve.
        T.CheckClose(b.Pnl, -50.5, "a NEGATIVE pnl survives");
        T.Check(b.Engine == "Cloud", "engine survives");
        T.Check(b.ExitReason == "BB_Stop", "exit reason survives");
        T.Check(b.CfgHash == "a1b2c3d4", "config hash survives");

        // The file is a trust boundary: a human edits it, and a kill -9
        // mid-append leaves half a line. Neither may parse into a WRONG
        // record — a truncated row that silently reads pnl = 0 is worse than
        // a row that is dropped.
        BbTradeRecord junk;
        T.Check(!BbHistory.TryParse("", out junk), "empty line rejected");
        T.Check(!BbHistory.TryParse("   ", out junk), "blank line rejected");
        T.Check(!BbHistory.TryParse("{\"ts\":\"2026-08-16T14:37:30\",\"dir\":-1", out junk),
                "a truncated line is rejected, not half-read");
        T.Check(!BbHistory.TryParse("not json at all", out junk), "garbage rejected");
    }

    private static void EquityAndHash()
    {
        T.Section("History — cumulative equity and the config digest");

        List<BbTradeRecord> rows = new List<BbTradeRecord>();
        T.CheckInt(BbHistory.CumulativeEquity(rows).Length, 0, "an empty file yields an empty curve");
        T.CheckInt(BbHistory.CumulativeEquity(null).Length, 0, "a null list does not throw");

        double[] pnls = { 100.0, -50.5, 25.0 };
        for (int i = 0; i < pnls.Length; i++)
        {
            BbTradeRecord r = Rec();
            r.Pnl = pnls[i];
            rows.Add(r);
        }
        double[] cum = BbHistory.CumulativeEquity(rows);
        T.CheckInt(cum.Length, 3, "one point per trade");
        T.CheckClose(cum[0], 100.0, "cum after trade 1");
        T.CheckClose(cum[1], 49.5, "cum after the loss");
        T.CheckClose(cum[2], 74.5, "cum after trade 3");

        // The digest is what draws the seam between configurations. If it moved
        // when the user clicked Risk 1.5x, every touch of the size dial would
        // dim the whole history and the seam would mean nothing.
        string a = BbHistory.Canonical(new List<string> { "cloud=1", "box=0", "risk=1" });
        string b = BbHistory.Canonical(new List<string> { "cloud=1", "box=0", "risk=1.5" });
        T.Check(a == b, "risk is excluded from the canonical string");
        T.Check(BbHistory.Hash(a) == BbHistory.Hash(b), "and therefore from the hash");

        // Order-independent: BuildConfigs may append in any order it likes.
        string c = BbHistory.Canonical(new List<string> { "box=0", "cloud=1" });
        T.Check(a == c, "the canonical string is order-independent");

        string d = BbHistory.Canonical(new List<string> { "cloud=0", "box=0" });
        T.Check(BbHistory.Hash(a) != BbHistory.Hash(d), "a real config change DOES move the hash");
        T.CheckInt(BbHistory.Hash(a).Length, 8, "8 hex chars, sized to fit a panel row");
        T.Check(BbHistory.Hash(a) == BbHistory.Hash(a), "the hash is stable across calls");
        T.CheckInt(BbHistory.Hash(null).Length, 8, "a null config hashes rather than throwing");
    }

    private static void WriteGuard()
    {
        T.Section("History — the write guard");

        // The fill path also runs in the Strategy Analyzer, in optimisation
        // sweeps, and over historical bars at startup. One 2,000-iteration
        // sweep would append tens of thousands of junk rows to the live curve —
        // the feature destroying its own data set. This is the assert that
        // stands between those two things.
        T.Check(BbHistory.ShouldWrite(true, false, "Sim101"), "a live realtime account writes");
        T.Check(!BbHistory.ShouldWrite(false, false, "Sim101"), "historical bars do NOT write");
        T.Check(!BbHistory.ShouldWrite(true, true, "Sim101"), "TickReplay re-runs the fill path: no write");
        T.Check(!BbHistory.ShouldWrite(true, false, "Backtest"), "the Strategy Analyzer account is skipped");
        T.Check(!BbHistory.ShouldWrite(true, false, "backtest_4"), "and its numbered variants, case-insensitively");
        T.Check(!BbHistory.ShouldWrite(true, false, ""), "an unresolved account does not write");
        T.Check(!BbHistory.ShouldWrite(true, false, null), "and neither does a null one");

        // Replay fills are real fills against recorded tape and worth keeping —
        // but mixed into the live file, a replayed October reads as this week's
        // P&L. Separate file, same format.
        T.Check(BbHistory.ShouldWrite(true, false, "Playback101"), "Replay writes");
        T.Check(BbHistory.FileName("MNQ 09-26", "Playback101").EndsWith("-replay.jsonl",
                StringComparison.Ordinal), "...to a -replay file");
        T.Check(!BbHistory.FileName("MNQ 09-26", "Sim101").Contains("-replay"),
                "the live file has no suffix");
        T.Check(BbHistory.FileName("MNQ 09-26", "Sim101") == "history-MNQ_09-26-Sim101.jsonl",
                "the file name is one instrument, one account");
        T.Check(BbHistory.FileName(null, null).Length > 0, "nulls yield a name, not an exception");
    }

    private static void GateLadder()
    {
        T.Section("Panel — gate ladder row states");

        // The defect this exists to make impossible: v1's panel printed READY
        // while printing "(out of band)" two rows below and never related the
        // two. A gate AFTER the blocker was never evaluated, so rendering it as
        // OK is a lie and rendering it as FAILED is a different lie.
        T.CheckInt(BbGateReport.RowState(0, 3), 0, "a gate before the blocker passed");
        T.CheckInt(BbGateReport.RowState(2, 3), 0, "and the one right before it");
        T.CheckInt(BbGateReport.RowState(3, 3), 1, "the blocker itself");
        T.CheckInt(BbGateReport.RowState(4, 3), 2, "everything after it was NOT evaluated");

        // depth = -1 is "nothing blocks": every row passed, none is dimmed.
        T.CheckInt(BbGateReport.RowState(0, -1), 0, "nothing blocks: row 0 passed");
        T.CheckInt(BbGateReport.RowState(5, -1), 0, "nothing blocks: the last row passed too");

        // Warmup blocks at the first gate, which must not read as "all dimmed".
        T.CheckInt(BbGateReport.RowState(0, 0), 1, "a warmup block is the blocker, not a dimmed row");
        T.CheckInt(BbGateReport.RowState(1, 0), 2, "and everything below it is dimmed");
    }

    private static void LogRing()
    {
        T.Section("Panel — engine log ring");

        BbLogRing r = new BbLogRing(3);
        T.CheckInt(r.Count, 0, "empty");
        T.Check(r.Newest(0) == "", "reading an empty ring yields a blank, not an exception");

        r.Push("12:41", "armed cloud_long @ 29867.50");
        T.CheckInt(r.Count, 1, "one entry");
        T.Check(r.Newest(0) == "12:41  armed cloud_long @ 29867.50", "HH:mm two spaces text");

        r.Push("12:46", "suppressed: box (cloud armed)");
        r.Push("12:52", "token killed - closed through E50");
        // NEWEST FIRST. The ladder says why now; the log says what happened
        // while you were away, and the thing you were away for is the last one.
        T.Check(r.Newest(0).StartsWith("12:52", StringComparison.Ordinal), "newest first");
        T.Check(r.Newest(2).StartsWith("12:41", StringComparison.Ordinal), "oldest last");

        r.Push("12:55", "filled");
        T.CheckInt(r.Count, 3, "the ring does not grow");
        T.Check(r.Newest(0).StartsWith("12:55", StringComparison.Ordinal), "the new entry is newest");
        T.Check(r.Newest(2).StartsWith("12:46", StringComparison.Ordinal), "the oldest fell off");
        T.Check(r.Newest(3) == "", "reading past the end is blank");

        // A repeated block transition would otherwise push the same line three
        // times and evict the two entries that explained it. If this push were
        // NOT suppressed it would land in the slot Newest(1) currently reads
        // ("12:52"), evicting "12:46" and promoting the old Newest(0) — so
        // Newest(1) staying put is the proof the duplicate never wrote.
        r.Push("12:56", "filled");
        T.Check(r.Newest(1).StartsWith("12:52", StringComparison.Ordinal), "a repeat is not pushed twice");
    }

    private static void ChartMath()
    {
        T.Section("Panel — history chart math");

        List<BbTradeRecord> rows = new List<BbTradeRecord>();
        for (int i = 0; i < 120; i++)
        {
            BbTradeRecord r = Rec();
            r.Ts = new DateTime(2026, 8, 1).AddDays(i / 6).AddHours(9 + i % 6);
            r.Pnl = 10.0;
            rows.Add(r);
        }

        T.CheckInt(BbHistory.View(rows, "100t").Count, 100, "100t takes the last hundred trades");
        // Windowed off the NEWEST RECORD, never DateTime.Now: a Replay file's
        // last trade is months old, and "today" against the wall clock would
        // render an empty chart with no explanation anywhere on the panel.
        T.CheckInt(BbHistory.View(rows, "today").Count, 6, "today = the newest record's own day");
        T.Check(BbHistory.View(rows, "20d").Count > 6, "20d is wider than today");
        T.CheckInt(BbHistory.View(new List<BbTradeRecord>(), "20d").Count, 0, "an empty file yields no view");
        T.CheckInt(BbHistory.View(null, "20d").Count, 0, "a null list does not throw");

        double zeroY;
        T.CheckInt(BbHistory.SparkPoints(new double[0], 100, 50, out zeroY).Length, 0, "no points from no trades");

        // A flat curve is the divide-by-zero: span 0. It draws down the middle.
        double[] flat = BbHistory.SparkPoints(new double[] { 0.0, 0.0, 0.0 }, 100, 50, out zeroY);
        T.CheckClose(zeroY, 25.0, "a flat curve puts the zero line mid-box");
        T.CheckClose(flat[1], 25.0, "and the curve on it");
        T.CheckClose(flat[4], 100.0, "the last point is at the right edge");

        // An all-negative curve: zero is still IN frame, pinned to the top.
        // Off-canvas would leave the reader with no reference at all.
        double[] down = BbHistory.SparkPoints(new double[] { -10.0, -20.0 }, 100, 50, out zeroY);
        T.CheckClose(zeroY, 0.0, "an underwater curve keeps zero at the top edge");
        T.CheckClose(down[3], 50.0, "the worst point sits on the floor");

        // y is inverted: WPF's origin is top-left, so a PROFIT must have a
        // SMALLER y. Getting this wrong renders every winning run as a slide.
        double[] up = BbHistory.SparkPoints(new double[] { 0.0, 100.0 }, 100, 50, out zeroY);
        T.Check(up[3] < up[1], "profit goes UP the screen");
    }

    private static void Cap()
    {
        T.Section("Panel — in-memory history cap (§68 amendment)");

        List<BbTradeRecord> rows = new List<BbTradeRecord>();
        for (int i = 0; i < 5; i++)
        {
            BbTradeRecord r = Rec();
            r.Pnl = i;
            rows.Add(r);
        }
        BbHistory.TrimFront(rows, 3);
        T.CheckInt(rows.Count, 3, "trimmed down to the cap");
        T.CheckClose(rows[0].Pnl, 2.0, "the two OLDEST were dropped, not the newest");
        T.CheckClose(rows[2].Pnl, 4.0, "the newest survives");

        List<BbTradeRecord> under = new List<BbTradeRecord> { Rec() };
        BbHistory.TrimFront(under, 3);
        T.CheckInt(under.Count, 1, "under the cap is a no-op");

        BbHistory.TrimFront(null, 3);
        T.Check(true, "a null list does not throw");
    }
}
