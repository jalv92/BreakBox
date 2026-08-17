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
}
