// AcctGovTests — the account-wide daily ledger (BbAcctGov): two instruments
// summing into ONE day P&L, the breach broadcast that stops all of them, day
// rollover, and the rewind reset.
//
// This is the piece that cannot be checked by reading the strategy: the whole
// point is what happens ACROSS instances, and each of them only ever sees its
// own numbers.
using System;
using BreakBoxCore;

public static class AcctGovTests
{
    public static void Run()
    {
        T.Section("BbAcctGov");
        SumsAcrossInstruments();
        BreachIsBroadcastToSiblings();
        StaleDayCannotDragTheSumBack();
        NewDayStartsClean();
        ResetClearsEverything();
    }

    private static readonly DateTime D1 = new DateTime(2026, 8, 20);
    private static readonly DateTime D2 = new DateTime(2026, 8, 21);

    private static double Pub(string acct, DateTime day, string key, double pnl, out bool breached)
    {
        double sum;
        bool ok = BbAcctGov.Publish(acct, day, key, pnl, out sum, out breached);
        return ok ? sum : double.NaN;
    }

    // Three charts, one account: each judges the TOTAL, not its own slice.
    private static void SumsAcrossInstruments()
    {
        BbAcctGov.Reset("A");
        bool b;
        T.CheckClose(Pub("A", D1, "NQ", 300, out b), 300, "first instance sees its own");
        T.CheckClose(Pub("A", D1, "ES", 250, out b), 550, "second adds in");
        T.CheckClose(Pub("A", D1, "NQ", 500, out b), 750, "republish REPLACES, never accumulates");
        // A separate account must not see any of it.
        T.CheckClose(Pub("B", D1, "NQ", 10, out b), 10, "accounts are isolated");
    }

    private static void BreachIsBroadcastToSiblings()
    {
        BbAcctGov.Reset("A");
        bool b;
        Pub("A", D1, "NQ", 500, out b);
        Pub("A", D1, "ES", 260, out b);
        T.Check(!b, "no breach before anyone latches one");
        string detail = BbAcctGov.Breach("A", D1);
        T.Check(detail.Contains("NQ 500") && detail.Contains("ES 260"), "breach detail lists every contributor");

        Pub("A", D1, "ES", 260, out b);
        T.Check(b, "sibling reads the broadcast on its next publish");
        // Even an instance whose own limits are 0 gets the flag: it publishes
        // like everyone else, so it hears it like everyone else.
        Pub("A", D1, "CL", 0, out b);
        T.Check(b, "a third, limitless instance hears the broadcast too");
        T.Check(BbAcctGov.Breach("A", D2) == "", "a breach for another day is not this day's");
    }

    // An instance still on yesterday must not publish yesterday's number into
    // today's sum — that is a governor firing on a P&L that no longer exists.
    private static void StaleDayCannotDragTheSumBack()
    {
        BbAcctGov.Reset("A");
        bool b;
        double sum;
        Pub("A", D2, "NQ", 400, out b);
        T.Check(!BbAcctGov.Publish("A", D1, "ES", -900, out sum, out b), "stale day is refused");
        T.CheckClose(Pub("A", D2, "NQ", 400, out b), 400, "and left the sum untouched");
    }

    private static void NewDayStartsClean()
    {
        BbAcctGov.Reset("A");
        bool b;
        Pub("A", D1, "NQ", 700, out b);
        BbAcctGov.Breach("A", D1);
        T.CheckClose(Pub("A", D2, "NQ", 50, out b), 50, "new day drops yesterday's contributions");
        T.Check(!b, "new day drops yesterday's breach latch");
    }

    private static void ResetClearsEverything()
    {
        BbAcctGov.Reset("A");
        bool b;
        Pub("A", D1, "NQ", 700, out b);
        BbAcctGov.Breach("A", D1);
        BbAcctGov.Reset("A");
        T.CheckClose(Pub("A", D1, "NQ", 700, out b), 700, "rewind reset drops the ledger");
        T.Check(!b, "rewind reset drops the breach latch");
    }
}
