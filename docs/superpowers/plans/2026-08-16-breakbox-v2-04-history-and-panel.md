## Phase 4 — History persistence and the vertical panel

> **Line numbers below were read off disk on 2026-08-16, before Phases 1–3 land.** Every citation was verified against the current file; Phases 1–3 shift them. Re-grep the quoted anchor text before editing — the anchors are chosen to survive the shift, the numbers are not.

---

### Task 60: `BreakBoxHistory.cs` — the record and its wire format

**Files:**
- Create: `ninjascript/BreakBoxHistory.cs`
- Create: `tests/HistoryTests.cs`
- Modify: `tests/BreakBox.Tests.csproj:9-13` (the `ItemGroup` of `Compile Include`s)
- Modify: `tests/Program.cs:45-48` (`Main` — ONE line inserted into the run list, never a rewrite of it)
- Modify: `scripts/check.sh:28` (`FILES=(...)`)
- Test: `tests/HistoryTests.cs`

**Interfaces:**
- Consumes: nothing — this is the root of the phase.
- Produces: `public struct BbTradeRecord { DateTime Ts; int Dir; double Entry, Exit; int Qty; double R, Pnl; string Engine, ExitReason, CfgHash; }` · `public static string BbHistory.Serialise(BbTradeRecord)` · `public static bool BbHistory.TryParse(string, out BbTradeRecord)`

- [ ] **Step 1: Write the failing test**

Create `tests/HistoryTests.cs`:

```csharp
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
```

Register it in `tests/Program.cs` by INSERTING one line after `ShellTests.Run();` — do not
rewrite the block. Every phase adds a suite here and a phase that replaces the list instead of
appending to it silently retires ~40 asserts from the other phases with nothing red to show for it:

```csharp
        BoxTests.Run();
        BracketTests.Run();
        ShellTests.Run();
        HistoryTests.Run();     // <- the only new line
```

Add the file to `tests/BreakBox.Tests.csproj`, after the `BreakBoxExits.cs` line:

```xml
    <Compile Include="../ninjascript/BreakBoxHistory.cs" Condition="Exists('../ninjascript/BreakBoxHistory.cs')" />
```

And to `scripts/check.sh` so the NT8 compilation unit carries it too:

```bash
FILES=(BreakBoxTypes BreakBoxCore BreakBoxExits BreakBoxHistory BreakBoxStrategy BreakBoxPanel)
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "error CS|FAIL|ALL PASS"`
Expected: FAIL with `error CS0103: The name 'BbHistory' does not exist in the current context`

- [ ] **Step 3: Write minimal implementation**

Create `ninjascript/BreakBoxHistory.cs`:

```csharp
// BreakBoxHistory.cs — the trade journal: one record per closed trade, its
// hand-rolled JSONL line, the equity reduction the panel plots, the digest that
// tells two configurations apart, and the guard that decides whether a row may
// be written at all.
//
// ZERO `using NinjaTrader.*`, own namespace `BreakBoxCore`, C# 7.3 only — same
// rules and same reason as BreakBoxTypes.cs.
//
// HAND-ROLLED ON PURPOSE. No System.Text.Json: NT8 is .NET Framework 4.8 and
// the test runner is net8, and no serializer sits on both reference paths. One
// that compiled here and not there would push this file out of the pure set,
// and the pure set is the only thing the assert suite can see.
//
// NO FILE I/O HERE. File.AppendAllText / File.ReadAllLines live in
// BreakBoxStrategy.cs. This file decides WHAT to write and WHETHER to write it;
// the shell does the writing. That split is what makes the write guard — the
// one rule standing between an optimisation sweep and the live equity curve —
// an assert in tests/ rather than a hope.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace BreakBoxCore
{
    public struct BbTradeRecord
    {
        public DateTime Ts;
        public int Dir;                 // +1 long, -1 short
        public double Entry, Exit;
        public int Qty;
        public double R;                // realised, in R multiples of the initial risk
        public double Pnl;              // currency, gross — the same basis the HUD reports
        public string Engine;           // "Cloud" / "Break": which engine owned the trigger
        public string ExitReason;
        public string CfgHash;
    }

    public static class BbHistory
    {
        // Every field is required. A record missing one is a torn write, and a
        // torn write that parses is a fabricated trade in the equity curve.
        private const int RequiredMask = 0x3FF;

        public static string Serialise(BbTradeRecord r)
        {
            StringBuilder sb = new StringBuilder(220);
            sb.Append("{\"ts\":\"").Append(r.Ts.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
            sb.Append("\",\"dir\":").Append(r.Dir.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"entry\":").Append(Num(r.Entry));
            sb.Append(",\"exit\":").Append(Num(r.Exit));
            sb.Append(",\"qty\":").Append(r.Qty.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"r\":").Append(Num(r.R));
            sb.Append(",\"pnl\":").Append(Num(r.Pnl));
            sb.Append(",\"engine\":\"").Append(Clean(r.Engine));
            sb.Append("\",\"exitReason\":\"").Append(Clean(r.ExitReason));
            sb.Append("\",\"cfgHash\":\"").Append(Clean(r.CfgHash)).Append("\"}");
            return sb.ToString();
        }

        public static bool TryParse(string line, out BbTradeRecord r)
        {
            r = default(BbTradeRecord);
            if (string.IsNullOrEmpty(line))
                return false;
            string s = line.Trim();
            if (s.Length < 2 || s[0] != '{' || s[s.Length - 1] != '}')
                return false;
            s = s.Substring(1, s.Length - 2);

            // Splitting on ',' is safe only because Clean() strips commas out of
            // every string field on the way in and the numbers are invariant.
            // That is the whole reason the writer sanitises instead of escaping:
            // there is no escape grammar to get wrong on the way back.
            string[] parts = s.Split(',');
            int seen = 0;
            double d;
            for (int i = 0; i < parts.Length; i++)
            {
                int c = parts[i].IndexOf(':');
                if (c < 0)
                    return false;
                string k = Unquote(parts[i].Substring(0, c).Trim());
                string v = parts[i].Substring(c + 1).Trim();

                if (k == "ts")
                {
                    DateTime t;
                    if (!DateTime.TryParseExact(Unquote(v), "yyyy-MM-ddTHH:mm:ss",
                            CultureInfo.InvariantCulture, DateTimeStyles.None, out t))
                        return false;
                    r.Ts = t; seen |= 1 << 0;
                }
                else if (k == "dir") { if (!Dbl(v, out d)) return false; r.Dir = (int)d; seen |= 1 << 1; }
                else if (k == "entry") { if (!Dbl(v, out d)) return false; r.Entry = d; seen |= 1 << 2; }
                else if (k == "exit") { if (!Dbl(v, out d)) return false; r.Exit = d; seen |= 1 << 3; }
                else if (k == "qty") { if (!Dbl(v, out d)) return false; r.Qty = (int)d; seen |= 1 << 4; }
                else if (k == "r") { if (!Dbl(v, out d)) return false; r.R = d; seen |= 1 << 5; }
                else if (k == "pnl") { if (!Dbl(v, out d)) return false; r.Pnl = d; seen |= 1 << 6; }
                else if (k == "engine") { r.Engine = Unquote(v); seen |= 1 << 7; }
                else if (k == "exitReason") { r.ExitReason = Unquote(v); seen |= 1 << 8; }
                else if (k == "cfgHash") { r.CfgHash = Unquote(v); seen |= 1 << 9; }
            }
            return seen == RequiredMask;
        }

        // "R" is the round-trip format: parse(format(x)) == x exactly. "0.00"
        // would quietly re-quantise every price the panel later subtracts.
        private static string Num(double v)
        {
            return v.ToString("R", CultureInfo.InvariantCulture);
        }

        private static bool Dbl(string v, out double d)
        {
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }

        private static string Unquote(string v)
        {
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
                return v.Substring(1, v.Length - 2);
            return v;
        }

        // Our own strings only ever hold [A-Za-z0-9_:] — but this file is read
        // back from disk, where a human can edit it. One stray quote or comma
        // would otherwise produce a line that parses into a WRONG record
        // instead of failing.
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                bool bad = ch == '"' || ch == '\\' || ch == ',' || ch == '{' || ch == '}' || ch == ':' || ch < ' ';
                sb.Append(bad ? '_' : ch);
            }
            return sb.ToString();
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"`
Expected: PASS — `ALL PASS (n checks)`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxHistory.cs tests/HistoryTests.cs tests/Program.cs tests/BreakBox.Tests.csproj scripts/check.sh && \
git commit -m "feat(history): BbTradeRecord + hand-rolled JSONL round trip

No System.Text.Json: NT8 is net48, the runner is net8, and no serializer
sits on both reference paths. Sanitise-on-write instead of an escape
grammar, and a required-field mask so a torn append is dropped rather
than half-read into a fabricated trade.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 61: Cumulative equity, the canonical config string, and the hash

**Files:**
- Modify: `ninjascript/BreakBoxHistory.cs` (append to `BbHistory`)
- Test: `tests/HistoryTests.cs`

**Interfaces:**
- Consumes: `BbTradeRecord` (Task 60)
- Produces: `public static double[] BbHistory.CumulativeEquity(IReadOnlyList<BbTradeRecord>)` · `public static string BbHistory.Hash(string)` · **plus one name not in the shared contract:** `public static string BbHistory.Canonical(IReadOnlyList<string> pairs)` — the `key=value` list the shell hands in, with the excluded keys dropped **here**, in the pure file. It is added because "`_uiRiskMult` is excluded" (§10) is a rule that only earns its keep if it can be asserted, and the call site is impure.

- [ ] **Step 1: Write the failing test**

Add to `tests/HistoryTests.cs` — one new method, and its call in `Run()`:

```csharp
    public static void Run()
    {
        RoundTrip();
        EquityAndHash();
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "error CS|FAIL|ALL PASS"`
Expected: FAIL with `error CS0117: 'BbHistory' does not contain a definition for 'CumulativeEquity'`

- [ ] **Step 3: Write minimal implementation**

Append inside `BbHistory` in `ninjascript/BreakBoxHistory.cs`, before the private helpers:

```csharp
        // Running sum, one point per trade. Not a rolling window and not
        // resampled by time: the x axis is TRADES, so a quiet day does not
        // stretch the curve and a busy one does not compress it.
        public static double[] CumulativeEquity(IReadOnlyList<BbTradeRecord> rows)
        {
            if (rows == null || rows.Count == 0)
                return new double[0];
            double[] cum = new double[rows.Count];
            double run = 0.0;
            for (int i = 0; i < rows.Count; i++)
            {
                run += rows[i].Pnl;
                cum[i] = run;
            }
            return cum;
        }

        // Keys that scale SIZE rather than change the DECISION. The shell hands
        // in everything it has, including risk, and the drop happens here — in
        // the file the assert suite can see — rather than at the impure call
        // site. §10: including `_uiRiskMult` would fragment the curve into a
        // new colour every time the user touches the Risk buttons, which
        // defeats the entire point of the seam.
        private static readonly string[] Excluded = { "risk" };

        public static string Canonical(IReadOnlyList<string> pairs)
        {
            if (pairs == null || pairs.Count == 0)
                return "";
            List<string> keep = new List<string>(pairs.Count);
            for (int i = 0; i < pairs.Count; i++)
            {
                string p = pairs[i];
                if (string.IsNullOrEmpty(p))
                    continue;
                int eq = p.IndexOf('=');
                string key = eq < 0 ? p : p.Substring(0, eq);
                bool drop = false;
                for (int k = 0; k < Excluded.Length; k++)
                    if (string.Equals(key, Excluded[k], StringComparison.Ordinal))
                        drop = true;
                if (!drop)
                    keep.Add(p);
            }
            // Sorted, so a reordering of BuildConfigs' own statements does not
            // read as a configuration change and dim every historical trade.
            keep.Sort(StringComparer.Ordinal);
            return string.Join(";", keep.ToArray());
        }

        // SHA-1, first 8 hex chars. A fingerprint, not a security primitive:
        // 4 billion buckets against a few dozen configurations a year, and it
        // has to fit in a 300px panel row next to the trade.
        public static string Hash(string canonicalConfig)
        {
            string s = canonicalConfig == null ? "" : canonicalConfig;
            using (System.Security.Cryptography.SHA1 sha = System.Security.Cryptography.SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                StringBuilder sb = new StringBuilder(8);
                for (int i = 0; i < 4; i++)
                    sb.Append(h[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxHistory.cs tests/HistoryTests.cs && \
git commit -m "feat(history): cumulative equity + config digest, risk excluded

The exclusion of _uiRiskMult lives in the pure file so it can be an
assert: it scales size, not the decision, and a hash that tracked it
would dim the whole curve every time the user clicks 1.5x.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 62: The write guard — the rule that protects the curve from a sweep

**Files:**
- Modify: `ninjascript/BreakBoxHistory.cs` (append to `BbHistory`)
- Test: `tests/HistoryTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: `public static bool BbHistory.ShouldWrite(bool realtime, bool tickReplay, string account)` · `public static string BbHistory.FileName(string instrument, string account)`

- [ ] **Step 1: Write the failing test**

Add to `tests/HistoryTests.cs`, plus its call in `Run()`:

```csharp
    public static void Run()
    {
        RoundTrip();
        EquityAndHash();
        WriteGuard();
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "error CS|FAIL|ALL PASS"`
Expected: FAIL with `error CS0117: 'BbHistory' does not contain a definition for 'ShouldWrite'`

- [ ] **Step 3: Write minimal implementation**

Append inside `BbHistory` in `ninjascript/BreakBoxHistory.cs`:

```csharp
        // THE WRITE GUARD (§10). Every argument is an NT8 fact passed IN:
        // `realtime` is State == State.Realtime, `tickReplay` is
        // Bars.IsTickReplay, `account` is Account.Name. They are parameters
        // rather than reads because this decision is the most expensive one in
        // the file and it has to be assertable.
        //
        // The account check is not redundant with the state check: a Strategy
        // Analyzer iteration reaches State.Realtime on some NT8 builds, and
        // that is exactly the path that would empty 40,000 rows into the live
        // curve before anyone noticed.
        public static bool ShouldWrite(bool realtime, bool tickReplay, string account)
        {
            if (!realtime || tickReplay)
                return false;
            if (string.IsNullOrEmpty(account))
                return false;
            return !account.StartsWith("Backtest", StringComparison.OrdinalIgnoreCase);
        }

        // One file per instrument per account, and Replay gets its own. NT8
        // names the Market Replay account "Playback101"; matching on the prefix
        // rather than the exact name survives NT8 numbering a second one.
        public static string FileName(string instrument, string account)
        {
            string ins = Clean(instrument == null ? "unknown" : instrument).Replace(' ', '_');
            string acc = Clean(account == null ? "unknown" : account).Replace(' ', '_');
            if (ins.Length == 0) ins = "unknown";
            if (acc.Length == 0) acc = "unknown";
            bool replay = acc.StartsWith("Playback", StringComparison.OrdinalIgnoreCase);
            return "history-" + ins + "-" + acc + (replay ? "-replay" : "") + ".jsonl";
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "FAIL|ALL PASS"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxHistory.cs tests/HistoryTests.cs && \
git commit -m "feat(history): the write guard, with the assert that justifies it

Realtime only, never TickReplay, never a Backtest* account, and Replay
to its own -replay file. Without it one optimisation sweep appends tens
of thousands of junk rows to the live equity curve.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 63: The file I/O and the journal call site (shell)

**Files:**
- Modify: `ninjascript/BreakBoxStrategy.cs` — the `Fields` region (`:91-144`), `BuildConfigs()` (`:272-322`), `State.DataLoaded` (`:239-258`), `WentFlat` (`:633-651`), `OnExecutionUpdate`'s flat branch (`:700-702`)
- Test: `scripts/check.sh` (the NT8 compilation unit — file I/O has no reach in the assert suite by construction)

**Interfaces:**
- Consumes: `BbHistory.ShouldWrite` / `FileName` / `Serialise` / `TryParse` / `Canonical` / `Hash` (Tasks 60–62) · from Phase 3's shell: `private BbEntryEngine _owningEngine;`, `private bool _uiBreakOn, _uiLongOn, _uiShortOn;` and `private bool _uiCloudOn;`, `private int BarSeconds();` — the box engine's toggle field is `_uiBreakOn` (Phase 3 names it after `EnableBreak`); the panel button on top of it is labelled "Box"
- Produces: `private readonly List<BbTradeRecord> _history` (newest last) · `private string _cfgHash` · `private void OpenHistory()` · `private void AppendHistory(BbTradeRecord)` — the panel reads all three.

- [ ] **Step 1: Write the failing test**

The check here is the compile gate: add the call sites first, so `check.sh` fails on the missing methods. In `ninjascript/BreakBoxStrategy.cs`, `State.DataLoaded` — after `_swings = new SwingDetector(SwingStrength);`:

```csharp
                OpenHistory();
```

And in `WentFlat`, immediately after the `pnl` computation (`:635-636`):

```csharp
            // Journalled BEFORE the bracket is torn down: `_bracket.Dir` is
            // zeroed twelve lines below, and reading it after is how a history
            // file fills up with dir=0 rows that plot but mean nothing.
            BbTradeRecord rec = default(BbTradeRecord);
            rec.Ts = Time[0];
            rec.Dir = _bracket.Dir;
            rec.Entry = _bracket.EntryPx;
            rec.Exit = exitPx;
            rec.Qty = _bracket.QtyTotal;
            rec.R = _bracket.R > 0.0 ? (exitPx - _bracket.EntryPx) * _bracket.Dir / _bracket.R : 0.0;
            rec.Pnl = pnl;
            rec.Engine = _owningEngine.ToString();
            rec.ExitReason = _exitReason.Length > 0 ? _exitReason : "unknown";
            rec.CfgHash = _cfgHash;
            AppendHistory(rec);
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | grep -E "error CS|compiles clean"`
Expected: FAIL with `error CS0103: The name 'OpenHistory' does not exist in the current context` (and the same for `AppendHistory`, `_cfgHash`)

- [ ] **Step 3: Write minimal implementation**

Add to the `Fields` region of `ninjascript/BreakBoxStrategy.cs`, after the governor block (`:129`):

```csharp
        // History (§10). The list is the panel's data source and holds EVERY
        // trade of this run, written or not: in a backtest you still want to
        // see the curve the run produced — you just must not let it touch the
        // live file. The guard is about the FILE, not about the chart.
        private readonly List<BbTradeRecord> _history = new List<BbTradeRecord>();
        private string _histPath = "";
        private string _cfgHash = "";
```

Add a `#region History` before `#region Governor` (`:754`):

```csharp
        #region History (file I/O — the impure half of BreakBoxHistory.cs)

        // Resolved once, at DataLoaded. NinjaTrader.Core.Globals.UserDataDir is
        // a directory probe, and Account.Name is stable for the strategy's life.
        private void OpenHistory()
        {
            string acct = Account != null ? Account.Name : "";
            string ins = Instrument != null ? Instrument.FullName : "";
            string dir = System.IO.Path.Combine(NinjaTrader.Core.Globals.UserDataDir, "BreakBox");
            _histPath = System.IO.Path.Combine(dir, BbHistory.FileName(ins, acct));
            _history.Clear();

            try
            {
                if (!System.IO.Directory.Exists(dir))
                    System.IO.Directory.CreateDirectory(dir);
                if (!System.IO.File.Exists(_histPath))
                    return;
                string[] lines = System.IO.File.ReadAllLines(_histPath);
                // Skip, never throw. A half-written last line — the platform was
                // killed mid-append — must cost that ONE trade, not the chart.
                for (int i = 0; i < lines.Length; i++)
                {
                    BbTradeRecord r;
                    if (BbHistory.TryParse(lines[i], out r))
                        _history.Add(r);
                }
                Print("BreakBox: history loaded, " + _history.Count + " trades from " + _histPath);
            }
            catch (Exception ex)
            {
                // A strategy that refuses to start because a log file is locked
                // is a worse outcome than a strategy with an empty chart.
                Print("BreakBox: history unreadable (" + ex.Message + ")");
            }
        }

        private void AppendHistory(BbTradeRecord r)
        {
            _history.Add(r);

            if (!BbHistory.ShouldWrite(State == State.Realtime,
                                       Bars != null && Bars.IsTickReplay,
                                       Account != null ? Account.Name : ""))
                return;

            try
            {
                System.IO.File.AppendAllText(_histPath, BbHistory.Serialise(r) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Print("BreakBox: history NOT written (" + ex.Message + ")");
            }
        }

        #endregion
```

At the end of `BuildConfigs()`, before the `if (_engine != null)` handover (`:318-321`):

```csharp
            // The digest that draws the seam between configurations. Rebuilt
            // HERE rather than at DataLoaded because BuildConfigs is exactly
            // what a panel toggle calls: a hash that only tracked the startup
            // parameters would stamp post-toggle trades as identical to
            // pre-toggle ones, which is the contamination the seam exists to
            // make visible. `risk` is listed and then dropped by Canonical —
            // listed so the exclusion is legible at the call site too.
            _cfgHash = BbHistory.Hash(BbHistory.Canonical(new List<string>
            {
                "risk=" + _uiRiskMult.ToString("0.##", CultureInfo.InvariantCulture),
                "cloud=" + (_uiCloudOn ? "1" : "0"),
                "box=" + (_uiBreakOn ? "1" : "0"),
                "long=" + (_uiLongOn ? "1" : "0"),
                "short=" + (_uiShortOn ? "1" : "0"),
                "stop=" + _uiStopSource,
                "sbuf=" + StopBufferTicks.ToString(CultureInfo.InvariantCulture),
                "smin=" + StopMinAtr.ToString("0.###", CultureInfo.InvariantCulture),
                "smax=" + StopMaxAtr.ToString("0.###", CultureInfo.InvariantCulture),
                "tiers=" + TierCount.ToString(CultureInfo.InvariantCulture),
                "tp1r=" + Tp1R.ToString("0.###", CultureInfo.InvariantCulture),
                "tp2r=" + Tp2R.ToString("0.###", CultureInfo.InvariantCulture),
                "tp3r=" + Tp3R.ToString("0.###", CultureInfo.InvariantCulture),
                "tp1pct=" + Tp1Pct.ToString(CultureInfo.InvariantCulture),
                "be=" + (BreakevenOnTp1 ? "1" : "0"),
                "bar=" + BarSeconds().ToString(CultureInfo.InvariantCulture)
            }));
```

In `OnExecutionUpdate`, replace the flat branch (`:700-702`):

```csharp
            // Any exit that leaves us flat closes the trade out.
            if (_inTrade && Position.MarketPosition == MarketPosition.Flat)
            {
                // The exit order's own signal name is the only honest reason
                // available here — BB_Stop / BB_TP2 / BB_Flatten. FlattenAll
                // already set a richer one, so it wins.
                if (_exitReason.Length == 0)
                    _exitReason = sig;
                WentFlat(price);
            }
```

And at the end of `WentFlat`, before `CheckDailyLimits();`:

```csharp
            // Cleared here, not at the next entry: a stale reason on the next
            // trade's record is indistinguishable from a real one.
            _exitReason = "";
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -5`
Expected: PASS — `ALL PASS (n checks)` then `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxStrategy.cs && \
git commit -m "feat(history): journal every closed trade from the shell

Record built before the bracket is torn down (Dir is zeroed below it),
exit reason taken from the exit order's signal name and cleared after
use, config hash rebuilt inside BuildConfigs so a panel toggle moves the
seam. Reads skip unparseable lines instead of refusing to start.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 64: Panel chrome — 300 DIP, docked left, header / scroll / action bar

**Files:**
- Modify: `ninjascript/BreakBoxPanel.cs:38-53` (fields), `:57-200` (`BuildPanel`), `:202-212` (`DisposePanel`), `:216-283` (widgets)
- Test: `scripts/check.sh`

**Interfaces:**
- Consumes: from Phase 3's shell — `private int BarSeconds();`
- Produces: `private DockPanel _panelRoot` · `private StackPanel _body` · `private static Grid Row2(UIElement, UIElement)` · `private static Grid Cols(params UIElement[])` · `private static TextBlock Section(string)` · `private static TextBlock Small(string)` · brushes `HeaderBg / DimBrush / OkBrush / WarnBrush / LossBrush / RuleBrush`

- [ ] **Step 1: Write the failing test**

Replace `BuildPanel` (`:57-200`) and `DisposePanel` (`:202-212`) in `ninjascript/BreakBoxPanel.cs`:

```csharp
        private void BuildPanel()
        {
            if (!ShowPanel || ChartControl == null)
                return;

            // Read off the STRATEGY thread and capture: Instrument and
            // BarSeconds() belong to NinjaScript, and reaching for them from
            // inside the dispatcher lambda is a cross-thread read that works
            // right up until it does not.
            string instLabel = (Instrument != null ? Instrument.FullName : "--")
                             + "  ·  " + BarSeconds() + "s";

            ChartControl.Dispatcher.InvokeAsync(new Action(() =>
            {
                if (_panelRoot != null && UserControlCollection.Contains(_panelRoot))
                    return;

                _panelRoot = new DockPanel
                {
                    Width = PanelWidth,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    LastChildFill = true,
                    Background = PanelBg
                };

                // Header and action bar dock FIRST, the ScrollViewer last. In a
                // DockPanel the last child fills what is left, and that is the
                // only arrangement in which a long gate ladder scrolls instead
                // of pushing FLATTEN off the bottom of the chart.
                UIElement header = BuildHeader(instLabel);
                DockPanel.SetDock(header, Dock.Top);
                _panelRoot.Children.Add(header);

                UIElement actions = BuildActionBar();
                DockPanel.SetDock(actions, Dock.Bottom);
                _panelRoot.Children.Add(actions);

                _body = new StackPanel { Margin = new Thickness(10, 6, 10, 6) };

                ScrollViewer scroll = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = _body
                };
                _panelRoot.Children.Add(scroll);

                UserControlCollection.Add(_panelRoot);
            }));
        }

        private void DisposePanel()
        {
            if (ChartControl == null || _panelRoot == null)
                return;
            ChartControl.Dispatcher.InvokeAsync(new Action(() =>
            {
                if (_panelRoot != null && UserControlCollection.Contains(_panelRoot))
                    UserControlCollection.Remove(_panelRoot);
                _panelRoot = null;
            }));
        }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | grep -E "error CS|compiles clean"`
Expected: FAIL with `error CS0103: The name 'PanelWidth' does not exist in the current context` (and the same for `BuildHeader`, `BuildActionBar`)

- [ ] **Step 3: Write minimal implementation**

Replace the `Panel fields` region (`:38-53`) of `ninjascript/BreakBoxPanel.cs`:

```csharp
        #region Panel fields

        // 300 DIP, docked left, full height. v1 was an auto-sized Grid of
        // horizontal StackPanels: width was max(row), height was sum(row), and
        // the result was an 830x480 landscape slab where no two rows lined up.
        // The fixed width is what makes "every row is the same 2-column grid"
        // mean anything.
        private const double PanelWidth = 300;

        private DockPanel _panelRoot;
        private StackPanel _body;
        private TextBlock _statusDot, _statusText, _instText;
        private Button _autoBtn, _lockBtn;

        private static readonly Brush OnBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xC8, 0xFF));
        private static readonly Brush OffBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x40));
        private static readonly Brush TextBrush = Brushes.White;
        private static readonly Brush PanelBg = new SolidColorBrush(Color.FromArgb(0xE8, 0x0F, 0x13, 0x1A));
        private static readonly Brush HeaderBg = new SolidColorBrush(Color.FromArgb(0xFF, 0x08, 0x0B, 0x10));
        private static readonly Brush DimBrush = new SolidColorBrush(Color.FromRgb(0x6A, 0x72, 0x7E));
        private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xC3, 0x8C));
        private static readonly Brush WarnBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        private static readonly Brush LossBrush = new SolidColorBrush(Color.FromRgb(0xD9, 0x53, 0x4F));
        private static readonly Brush RuleBrush = new SolidColorBrush(Color.FromRgb(0x1C, 0x22, 0x2C));

        // Frozen because the static initialiser runs on whichever thread
        // touches the class first — normally NinjaScript's — and these are then
        // assigned to Foreground on the WPF thread. An unfrozen Freezable used
        // across dispatchers throws "The calling thread cannot access this
        // object", and it throws intermittently, which is the worst way to find
        // out about it.
        static BreakBoxStrategy()
        {
            Brush[] all = { OnBrush, OffBrush, PanelBg, HeaderBg, DimBrush, OkBrush, WarnBrush, LossBrush, RuleBrush };
            for (int i = 0; i < all.Length; i++)
                all[i].Freeze();
        }

        #endregion
```

Replace the `Widgets` region (`:216-283`) — `Row()` goes, the grid helpers arrive:

```csharp
        #region Widgets

        // EVERY row in the body is this: label left on a star column, value
        // right on an auto column. v1's rows were horizontal StackPanels, so
        // each one was as wide as its own content and nothing lined up with
        // anything — that is the whole of the "messy rectangle".
        private static Grid Row2(UIElement left, UIElement right)
        {
            Grid g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (left != null) { Grid.SetColumn(left, 0); g.Children.Add(left); }
            if (right != null) { Grid.SetColumn(right, 1); g.Children.Add(right); }
            return g;
        }

        // Equal-width columns, for the button strips. The action bar is the one
        // place where the 2-column rule would look wrong: three equal buttons
        // beat two wide ones and a stub.
        private static Grid Cols(params UIElement[] cells)
        {
            Grid g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            for (int i = 0; i < cells.Length; i++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (cells[i] == null)
                    continue;
                Grid.SetColumn(cells[i], i);
                g.Children.Add(cells[i]);
            }
            return g;
        }

        private static TextBlock Section(string title)
        {
            return new TextBlock
            {
                Text = title,
                Foreground = DimBrush,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 10, 0, 3)
            };
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = TextBrush,
                FontSize = 11,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static TextBlock Small(string text)
        {
            TextBlock t = Label(text);
            t.Foreground = DimBrush;
            t.FontSize = 10;
            return t;
        }

        private static Border Rule()
        {
            return new Border { Height = 1, Background = RuleBrush, Margin = new Thickness(0, 6, 0, 0) };
        }

        private static Button Toggle(string text, bool on, System.Windows.RoutedEventHandler onClick)
        {
            Button b = new Button
            {
                Content = text,
                Margin = new Thickness(1),
                Padding = new Thickness(4, 2, 4, 2),
                FontSize = 10,
                Foreground = TextBrush,
                Background = on ? OnBrush : OffBrush,
                BorderThickness = new Thickness(0)
            };
            b.Click += onClick;
            return b;
        }

        private static Button Action_(string text, System.Windows.RoutedEventHandler onClick)
        {
            Button b = new Button
            {
                Content = text,
                Margin = new Thickness(1),
                Padding = new Thickness(4, 5, 4, 5),
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.Black,
                Background = Brushes.Gainsboro,
                BorderThickness = new Thickness(0)
            };
            b.Click += onClick;
            return b;
        }

        private static void Paint(Button b, bool on)
        {
            if (b != null)
                b.Background = on ? OnBrush : OffBrush;
        }

        #endregion

        #region Chrome

        private UIElement BuildHeader(string instLabel)
        {
            Border b = new Border { Background = HeaderBg, Padding = new Thickness(10, 8, 10, 8) };
            StackPanel s = new StackPanel();

            TextBlock title = new TextBlock
            {
                Text = "BREAKBOX",
                Foreground = TextBrush,
                FontSize = 13,
                FontWeight = FontWeights.Bold
            };
            _instText = Small(instLabel);
            s.Children.Add(Row2(title, _instText));

            StackPanel st = new StackPanel { Orientation = Orientation.Horizontal };
            // A text bullet, not an Ellipse: NinjaTrader.NinjaScript.DrawingTools
            // also declares Ellipse, and check.sh hoists every file's usings into
            // ONE compilation unit — so the WPF shape and the drawing tool become
            // an ambiguous reference (CS0104) in the combined build only.
            _statusDot = new TextBlock
            {
                Text = "\u25CF",
                Foreground = DimBrush,
                FontSize = 11,
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            _statusText = Label("WARMING");
            st.Children.Add(_statusDot);
            st.Children.Add(_statusText);

            _autoBtn = Toggle("AUTO-TRADE", _uiAutoTrade, delegate
            {
                _uiAutoTrade = !_uiAutoTrade;
                Paint(_autoBtn, _uiAutoTrade);
            });
            s.Children.Add(Row2(st, _autoBtn));

            b.Child = s;
            return b;
        }

        private UIElement BuildActionBar()
        {
            Border b = new Border { Background = HeaderBg, Padding = new Thickness(9, 6, 9, 9) };
            StackPanel s = new StackPanel();

            _lockBtn = Toggle("LOCK OUT", _lockout, delegate { Dispatch(o => PanelToggleLockout()); });
            _lockBtn.Padding = new Thickness(4, 5, 4, 5);
            _lockBtn.FontWeight = FontWeights.Bold;

            s.Children.Add(Cols(
                Action_("FLATTEN", delegate { Dispatch(o => FlattenAll("panel")); }),
                Action_("BE", delegate { Dispatch(o => PanelBreakeven()); }),
                _lockBtn));
            s.Children.Add(Cols(
                Action_("MANUAL BUY", delegate { Dispatch(o => PanelManualEntry(+1)); }),
                Action_("MANUAL SELL", delegate { Dispatch(o => PanelManualEntry(-1)); })));

            b.Child = s;
            return b;
        }

        #endregion
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -5`
Expected: PASS — `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxPanel.cs && \
git commit -m "refactor(panel): 300 DIP DockPanel docked left, fixed header + action bar

Header and action bar dock first so the ScrollViewer fills the rest and
a long gate ladder scrolls instead of pushing FLATTEN off the chart.
Row2/Cols replace the horizontal StackPanels that made width = max(row).
Brushes frozen: the static init runs on NinjaScript's thread and the
assignment happens on WPF's.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 65: The gate ladder — WHY NO TRADE

**Files:**
- Modify: `ninjascript/BreakBoxTypes.cs` — append one static to `BbGateReport` (the class is Phase 1's; no verified line number)
- Modify: `ninjascript/BreakBoxPanel.cs` — `BuildPanel`'s body list, `#region Chrome`
- Test: `tests/HistoryTests.cs`

**Interfaces:**
- Consumes: `BbGateReport { string Block; string BlockDetail; int GateDepth; }` (Phase 1) · `BbCloudState.Gate`, `BbEngineState.Gate` (Phases 2–3)
- Produces: `public static int BbGateReport.RowState(int row, int depth)` — 0 passed, 1 blocker, 2 not evaluated · `private const int GateRows = 10` (the depth of the DEEPER of the two ladders — the box's) · `private static readonly string[] CloudGates / BoxGates` · `private readonly TextBlock[] _gateMark/_gateName/_gateVal`

- [ ] **Step 1: Write the failing test**

Add to `tests/HistoryTests.cs`, plus its call in `Run()`:

```csharp
    public static void Run()
    {
        RoundTrip();
        EquityAndHash();
        WriteGuard();
        GateLadder();
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "error CS|FAIL|ALL PASS"`
Expected: FAIL with `error CS0117: 'BbGateReport' does not contain a definition for 'RowState'`

- [ ] **Step 3: Write minimal implementation**

Append inside `BbGateReport` in `ninjascript/BreakBoxTypes.cs`:

```csharp
        // How the panel should render ladder row `row` given `depth`, the index
        // of the first failing gate (-1 = nothing blocks). 0 = passed,
        // 1 = the blocker, 2 = never evaluated.
        //
        // Pure and here rather than in the panel because "everything after the
        // blocker is DIMMED, not FAILED" is the entire point of the ladder: the
        // engine short-circuits at the first failure, so the rows below it were
        // never computed and any verdict on them is invented.
        public static int RowState(int row, int depth)
        {
            if (depth < 0)
                return 0;
            return row < depth ? 0 : (row == depth ? 1 : 2);
        }
```

In `ninjascript/BreakBoxPanel.cs`, add to the fields region:

```csharp
        // TEN rendered rows, because the DEEPER of the two ladders — the box
        // engine's — reports depths 0..9. The engine reports only a DEPTH and
        // the names live here, so a panel sized to six rows answers `depth == 7`
        // ("armed") by painting six green OKs under a strategy that is blocked:
        // the exact failure this whole section exists to end. Size to the
        // deepest ladder, never to the shortest.
        private const int GateRows = 10;

        // The BOX ladder — the exact strings the box engine passes as the first
        // argument of `Gate.Set`, in its own evaluation order (§6.2). Index i
        // here IS depth i there; if the engine inserts a gate, it inserts one
        // here on the same line or the panel starts naming the wrong blocker.
        private static readonly string[] BoxGates =
        {
            "atr warm", "box warming", "box", "box valid", "auto-trade",
            "window", "budget", "armed", "break", "arms"
        };

        // The CLOUD ladder, DERIVED from the cloud engine's own `Gate.Set`
        // depths (§5) rather than asserted here — that mapping is the cloud
        // engine's to publish, and this array follows it. It is SHORTER than the
        // box's, which is why FillGates renders `names.Length` rows and blanks
        // the rest: a cloud blocker labelled with a box gate name is worse than
        // no label at all. The GateVal[1]/[2] fills in FillGates are indices
        // into THIS array — move them if it moves.
        private static readonly string[] CloudGates = { "atr warm", "regime", "token", "gold candle", "window", "budget" };

        private TextBlock _headline;
        private readonly TextBlock[] _gateMark = new TextBlock[GateRows];
        private readonly TextBlock[] _gateName = new TextBlock[GateRows];
        private readonly TextBlock[] _gateVal = new TextBlock[GateRows];
```

Add the builder to `#region Chrome`:

```csharp
        private UIElement BuildGateSection()
        {
            StackPanel s = new StackPanel();
            s.Children.Add(Section("WHY NO TRADE"));

            // The headline answers the question in words. Three numbers that did
            // not exist in v1 live here: how far the nearest actionable price
            // is, how many bars are left on the armed trigger, and what the
            // token is doing.
            _headline = Label("--");
            _headline.TextWrapping = TextWrapping.Wrap;
            _headline.Margin = new Thickness(0, 0, 0, 4);
            s.Children.Add(_headline);

            for (int i = 0; i < GateRows; i++)
            {
                _gateMark[i] = new TextBlock
                {
                    Text = "\u00B7",
                    Foreground = DimBrush,
                    FontSize = 11,
                    Width = 14,
                    VerticalAlignment = VerticalAlignment.Center
                };
                _gateName[i] = Small("");
                _gateVal[i] = Small("");

                StackPanel left = new StackPanel { Orientation = Orientation.Horizontal };
                left.Children.Add(_gateMark[i]);
                left.Children.Add(_gateName[i]);
                s.Children.Add(Row2(left, _gateVal[i]));
            }

            s.Children.Add(Rule());
            return s;
        }
```

And add it to the body in `BuildPanel`, right after `_body` is created:

```csharp
                _body.Children.Add(BuildGateSection());
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -5`
Expected: PASS — `ALL PASS (n checks)` then `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxTypes.cs ninjascript/BreakBoxPanel.cs tests/HistoryTests.cs && \
git commit -m "feat(panel): the WHY NO TRADE gate ladder

RowState is pure and asserted: passed / blocker / never evaluated. The
rows below the blocker were short-circuited, so rendering them as either
OK or FAILED is invented. This is the section that replaces a panel
reading READY next to (out of band) for an hour.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 66: The engine log ring buffer

**Files:**
- Modify: `ninjascript/BreakBoxHistory.cs` (add `BbLogRing`)
- Modify: `ninjascript/BreakBoxPanel.cs` (fields, `#region Chrome`, `BuildPanel` body)
- Test: `tests/HistoryTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces: **one name not in the shared contract:** `public sealed class BbLogRing` in `BreakBoxHistory.cs` — `Push(string ts, string text)`, `Newest(int i)`, `Count`. It lives in the pure file so newest-first ordering is an assert rather than a thing you check by squinting at a chart. · `private readonly BbLogRing _log` on the strategy · `private void EngineLog(string)`

- [ ] **Step 1: Write the failing test**

Add to `tests/HistoryTests.cs`, plus its call in `Run()`:

```csharp
    public static void Run()
    {
        RoundTrip();
        EquityAndHash();
        WriteGuard();
        GateLadder();
        LogRing();
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
        // times and evict the two entries that explained it.
        r.Push("12:56", "filled");
        T.Check(r.Newest(1).StartsWith("12:55", StringComparison.Ordinal), "a repeat is not pushed twice");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "error CS|FAIL|ALL PASS"`
Expected: FAIL with `error CS0246: The type or namespace name 'BbLogRing' could not be found`

- [ ] **Step 3: Write minimal implementation**

Append to `ninjascript/BreakBoxHistory.cs`, inside `namespace BreakBoxCore`:

```csharp
    // The engine log (§9.4). Lives in the pure file for one reason: newest-first
    // ordering across a wrap is the kind of off-by-one you cannot see on a chart
    // — three plausible lines in the wrong order look exactly like three lines
    // in the right order.
    public sealed class BbLogRing
    {
        private readonly string[] _buf;
        private int _next;
        private int _count;
        private string _lastText = "";

        public BbLogRing(int size)
        {
            _buf = new string[size < 1 ? 1 : size];
        }

        public int Count { get { return _count; } }

        public void Push(string ts, string text)
        {
            if (text == null)
                text = "";
            // Every transition into a new Block pushes. Without this, a block
            // that persists for forty bars evicts the two entries that
            // explained how it got there.
            if (text == _lastText)
                return;
            _lastText = text;
            _buf[_next] = (ts == null ? "" : ts) + "  " + text;
            _next = (_next + 1) % _buf.Length;
            if (_count < _buf.Length)
                _count++;
        }

        // 0 = the most recent entry.
        public string Newest(int i)
        {
            if (i < 0 || i >= _count)
                return "";
            int idx = _next - 1 - i;
            while (idx < 0)
                idx += _buf.Length;
            return _buf[idx] == null ? "" : _buf[idx];
        }
    }
```

In `ninjascript/BreakBoxPanel.cs`, add to the fields region:

```csharp
        private static readonly int LogRows = 3;
        private readonly BbLogRing _log = new BbLogRing(3);
        private readonly TextBlock[] _logText = new TextBlock[3];
```

Add to `#region Chrome`:

```csharp
        private UIElement BuildLogSection()
        {
            StackPanel s = new StackPanel();
            s.Children.Add(Section("ENGINE LOG"));
            for (int i = 0; i < LogRows; i++)
            {
                _logText[i] = Small("");
                _logText[i].TextTrimming = TextTrimming.CharacterEllipsis;
                s.Children.Add(_logText[i]);
            }
            s.Children.Add(Rule());
            return s;
        }
```

Add to `#region Panel actions (strategy thread)` — the single push point, called from the strategy thread only:

```csharp
        // Called from OnBarUpdate and the order handlers, never from WPF. The
        // ring is plain fields with no lock because there is exactly one writer
        // thread and the reader only ever runs inside the batched dispatcher
        // callback, which reads a COPY taken on this thread.
        private void EngineLog(string text)
        {
            _log.Push(Time[0].ToString("HH:mm", CultureInfo.InvariantCulture), text);
        }
```

And add the section to `BuildPanel`'s body, after the gate section:

```csharp
                _body.Children.Add(BuildLogSection());
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -5`
Expected: PASS — `ALL PASS (n checks)` then `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxHistory.cs ninjascript/BreakBoxPanel.cs tests/HistoryTests.cs && \
git commit -m "feat(panel): engine log ring, newest first and de-duplicated

Pure and asserted: three plausible lines in the wrong order look exactly
like three lines in the right order. Repeats are dropped so a block that
persists for forty bars does not evict the entries that explained it.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 67: CONTROLS and SESSION — toggles off the WPF thread (B5)

**Files:**
- Modify: `ninjascript/BreakBoxPanel.cs` (fields, `#region Chrome`, `BuildPanel` body, `#region Panel actions`)
- Test: `scripts/check.sh`

**Interfaces:**
- Consumes: from Phase 3's shell — `private bool _uiCloudOn;` and `private bool _uiBreakOn, _uiLongOn, _uiShortOn;` (the box engine's toggle is `_uiBreakOn`, named after `EnableBreak`; only the button caption says "Box"), `private double _uiRiskMult;`, `private BbStopSource _uiStopSource;`, `private void BuildConfigs();`, `private int BarSeconds();`
- Produces: `private void Rebuild()` (the B5 bridge) · `private TextBlock _sessionA, _sessionB` · `private Button _cloudBtn, _boxBtn, _buyBtn, _sellBtn` · `private readonly Button[] _riskBtns, _slBtns`

- [ ] **Step 1: Write the failing test**

Add the two sections to `BuildPanel`'s body list, after the log section:

```csharp
                _body.Children.Add(BuildControlsSection());
                _body.Children.Add(BuildSessionSection());
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | grep -E "error CS|compiles clean"`
Expected: FAIL with `error CS0103: The name 'BuildControlsSection' does not exist in the current context`

- [ ] **Step 3: Write minimal implementation**

Add to the fields region of `ninjascript/BreakBoxPanel.cs`:

```csharp
        private Button _cloudBtn, _boxBtn, _buyBtn, _sellBtn;
        private readonly Button[] _riskBtns = new Button[3];
        private readonly Button[] _slBtns = new Button[5];
        private static readonly double[] RiskLevels = { 0.5, 1.0, 1.5 };
        // Five, because BbStopSource has five (§9.5). `MA` changes MEANING with
        // MaPeriod — at RibbonSlow it is "the far ribbon edge" — which is why
        // the SESSION block below names the active one instead of leaving the
        // lit button to imply it.
        private static readonly string[] SlNames = { "Cndl", "Swng", "MA", "E50", "Man" };
        private TextBlock _sessionA, _sessionB;
```

Add to `#region Chrome`:

```csharp
        private UIElement BuildControlsSection()
        {
            StackPanel s = new StackPanel();
            s.Children.Add(Section("CONTROLS"));

            _cloudBtn = Toggle("Cloud", _uiCloudOn, delegate
            {
                _uiCloudOn = !_uiCloudOn;
                Paint(_cloudBtn, _uiCloudOn);
                Rebuild();
            });
            // Caption "Box", field `_uiBreakOn`. The engine is the break engine
            // and Phase 3 names the field after its `EnableBreak` property; the
            // box is what the user sees it draw, so that is what the button says.
            _boxBtn = Toggle("Box", _uiBreakOn, delegate
            {
                _uiBreakOn = !_uiBreakOn;
                Paint(_boxBtn, _uiBreakOn);
                Rebuild();
            });
            s.Children.Add(Row2(Small("Engine"), Cols(_cloudBtn, _boxBtn)));

            _buyBtn = Toggle("Buy", _uiLongOn, delegate
            {
                _uiLongOn = !_uiLongOn;
                Paint(_buyBtn, _uiLongOn);
                Rebuild();
            });
            _sellBtn = Toggle("Sell", _uiShortOn, delegate
            {
                _uiShortOn = !_uiShortOn;
                Paint(_sellBtn, _uiShortOn);
                Rebuild();
            });
            s.Children.Add(Row2(Small("Side"), Cols(_buyBtn, _sellBtn)));

            UIElement[] risk = new UIElement[RiskLevels.Length];
            for (int i = 0; i < RiskLevels.Length; i++)
            {
                int idx = i;
                _riskBtns[i] = Toggle(RiskLevels[i].ToString("0.#", CultureInfo.InvariantCulture) + "x",
                    Math.Abs(_uiRiskMult - RiskLevels[i]) < 1e-9, delegate
                    {
                        _uiRiskMult = RiskLevels[idx];
                        for (int k = 0; k < _riskBtns.Length; k++)
                            Paint(_riskBtns[k], k == idx);
                        // NO Rebuild(): risk scales size, not the decision, and
                        // it is excluded from the config hash for the same
                        // reason (§10). Rebuilding here would be harmless and
                        // misleading.
                    });
                risk[i] = _riskBtns[i];
            }
            s.Children.Add(Row2(Small("Risk"), Cols(risk)));

            UIElement[] sl = new UIElement[SlNames.Length];
            for (int i = 0; i < SlNames.Length; i++)
            {
                int idx = i;
                _slBtns[i] = Toggle(SlNames[i], (int)_uiStopSource == i, delegate
                {
                    _uiStopSource = (BbStopSource)idx;
                    for (int k = 0; k < _slBtns.Length; k++)
                        Paint(_slBtns[k], k == idx);
                    Rebuild();
                });
                sl[i] = _slBtns[i];
            }
            s.Children.Add(Row2(Small("Stop"), Cols(sl)));

            s.Children.Add(Rule());
            return s;
        }

        private UIElement BuildSessionSection()
        {
            StackPanel s = new StackPanel();
            s.Children.Add(Section("SESSION"));
            _sessionA = Small("--");
            _sessionB = Small("--");
            s.Children.Add(_sessionA);
            s.Children.Add(_sessionB);
            s.Children.Add(Rule());
            return s;
        }
```

Add to `#region Panel actions (strategy thread)`:

```csharp
        // B5. Every toggle used to call BuildConfigs() straight out of its click
        // handler — i.e. on the WPF thread — swapping _cfg and the engine out
        // from under a running OnBarUpdate. Flipping the bool is a single
        // aligned write and survives that; rebuilding the config object does
        // not. Both now happen on NinjaScript's thread, in order.
        private void Rebuild()
        {
            Dispatch(o => BuildConfigs());
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -5`
Expected: PASS — `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxPanel.cs && \
git commit -m "fix(panel): route config rebuilds through TriggerCustomEvent (B5)

Toggles called BuildConfigs() on the WPF thread, swapping the engine out
from under OnBarUpdate. Risk deliberately does NOT rebuild: it scales
size, not the decision, which is the same reason it is out of the hash.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 68: The history chart — Polyline, Polygon, dashed zero, three views

**Files:**
- Modify: `ninjascript/BreakBoxHistory.cs` (append `View` and `SparkPoints` to `BbHistory`)
- Modify: `ninjascript/BreakBoxPanel.cs` (usings, fields, `#region Chrome`, `BuildPanel` body)
- Test: `tests/HistoryTests.cs`

**Interfaces:**
- Consumes: `BbHistory.CumulativeEquity` (Task 61) · `_history` (Task 63)
- Produces: `public static List<BbTradeRecord> BbHistory.View(IReadOnlyList<BbTradeRecord>, string view)` · `public static double[] BbHistory.SparkPoints(double[] cum, double w, double h, out double zeroY)` · `private string _histView` · `private WPolyline _equityLine` · `private WPolygon _equityFill` · `private WLine _zeroLine`

- [ ] **Step 1: Write the failing test**

Add to `tests/HistoryTests.cs`, plus its call in `Run()`:

```csharp
    public static void Run()
    {
        RoundTrip();
        EquityAndHash();
        WriteGuard();
        GateLadder();
        LogRing();
        ChartMath();
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
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && dotnet run --project tests 2>&1 | grep -E "error CS|FAIL|ALL PASS"`
Expected: FAIL with `error CS0117: 'BbHistory' does not contain a definition for 'View'`

- [ ] **Step 3: Write minimal implementation**

Append inside `BbHistory` in `ninjascript/BreakBoxHistory.cs`:

```csharp
        // The panel's three views. `today` and `20d` are windows measured from
        // the NEWEST RECORD, not from DateTime.Now: a Replay file's last trade
        // is months old, and a wall-clock "today" would render an empty chart
        // with nothing on the panel to explain it.
        public static List<BbTradeRecord> View(IReadOnlyList<BbTradeRecord> rows, string view)
        {
            List<BbTradeRecord> outp = new List<BbTradeRecord>();
            if (rows == null || rows.Count == 0)
                return outp;

            if (view == "100t")
            {
                int from = rows.Count > 100 ? rows.Count - 100 : 0;
                for (int i = from; i < rows.Count; i++)
                    outp.Add(rows[i]);
                return outp;
            }

            int days = view == "today" ? 1 : 20;
            DateTime cut = rows[rows.Count - 1].Ts.Date.AddDays(1 - days);
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].Ts.Date >= cut)
                    outp.Add(rows[i]);
            return outp;
        }

        // Maps a cumulative-equity curve onto panel pixels: x,y pairs plus the y
        // of the zero baseline. y is INVERTED because WPF's origin is top-left —
        // a profit has to have a smaller y or every winning run renders as a
        // slide.
        //
        // Zero is always in frame: it is the reference the whole chart is read
        // against, so an all-negative curve pins it to the top edge rather than
        // scrolling it off the canvas.
        public static double[] SparkPoints(double[] cum, double w, double h, out double zeroY)
        {
            zeroY = h * 0.5;
            if (cum == null || cum.Length == 0 || w <= 0.0 || h <= 0.0)
                return new double[0];

            double lo = 0.0, hi = 0.0;
            for (int i = 0; i < cum.Length; i++)
            {
                if (cum[i] < lo) lo = cum[i];
                if (cum[i] > hi) hi = cum[i];
            }

            double dx = cum.Length == 1 ? 0.0 : w / (cum.Length - 1);
            double[] pts = new double[cum.Length * 2];
            double span = hi - lo;

            // The degenerate case that makes this function worth testing: a
            // curve that never moves divides by zero. It draws down the middle.
            if (span <= 0.0)
            {
                for (int i = 0; i < cum.Length; i++)
                {
                    pts[i * 2] = i * dx;
                    pts[i * 2 + 1] = zeroY;
                }
                return pts;
            }

            zeroY = h * hi / span;
            for (int i = 0; i < cum.Length; i++)
            {
                pts[i * 2] = i * dx;
                pts[i * 2 + 1] = h * (hi - cum[i]) / span;
            }
            return pts;
        }
```

In `ninjascript/BreakBoxPanel.cs`, add to the using block (`:22-32`):

```csharp
using System.Windows.Shapes;
// Aliased, and this is not style. check.sh hoists every file's usings into ONE
// compilation unit, which drags BreakBoxStrategy.cs's
// `using NinjaTrader.NinjaScript.DrawingTools` into scope here — and that
// namespace declares its own Line, Polygon and Polyline. Unaliased, the
// combined build (and only the combined build) fails CS0104 ambiguous
// reference, which is a spectacularly confusing way to lose an afternoon.
using WLine = System.Windows.Shapes.Line;
using WPolyline = System.Windows.Shapes.Polyline;
using WPolygon = System.Windows.Shapes.Polygon;
```

Add to the fields region:

```csharp
        // Chart geometry, in DIP. 300 wide minus 2x10 body margin minus 16 of
        // slack for the scrollbar.
        private const double ChartW = 254;
        private const double ChartH = 64;

        private string _histView = "20d";
        private readonly Button[] _viewBtns = new Button[3];
        private static readonly string[] ViewNames = { "today", "20d", "100t" };
        private TextBlock _equityText, _statsText;
        private Canvas _chart;
        private WPolyline _equityLine;
        private WPolygon _equityFill;
        private WLine _zeroLine;
        private ColumnDefinition _wCol, _beCol, _lCol;
        private readonly TextBlock[] _tradeText = new TextBlock[3];
        private readonly Border[] _tradeBar = new Border[3];
```

Add to `#region Chrome`:

```csharp
        private UIElement BuildHistorySection()
        {
            StackPanel s = new StackPanel();

            UIElement[] views = new UIElement[ViewNames.Length];
            for (int i = 0; i < ViewNames.Length; i++)
            {
                int idx = i;
                _viewBtns[i] = Toggle(ViewNames[i], ViewNames[i] == _histView, delegate
                {
                    _histView = ViewNames[idx];
                    for (int k = 0; k < _viewBtns.Length; k++)
                        Paint(_viewBtns[k], k == idx);
                    // No Rebuild(): the view is a lens on data already in
                    // memory. It must not touch the trading config.
                });
                views[i] = _viewBtns[i];
            }
            s.Children.Add(Row2(Section("HISTORY"), Cols(views)));

            // The dominant number. 22px because it is the one thing on this
            // panel a human reads from across the room.
            _equityText = new TextBlock
            {
                Text = "--",
                Foreground = TextBrush,
                FontSize = 22,
                Margin = new Thickness(0, 2, 0, 2)
            };
            s.Children.Add(_equityText);

            _chart = new Canvas { Height = ChartH, Width = ChartW, Margin = new Thickness(0, 2, 0, 6) };
            _zeroLine = new WLine
            {
                X1 = 0,
                X2 = ChartW,
                Stroke = DimBrush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection(new double[] { 2, 3 })
            };
            _equityFill = new WPolygon { Fill = new SolidColorBrush(Color.FromArgb(0x28, 0x00, 0xC8, 0xFF)) };
            _equityLine = new WPolyline { Stroke = OnBrush, StrokeThickness = 1.5 };
            // Baseline under the fill under the line: the line is the data and
            // must never be the thing that gets covered.
            _chart.Children.Add(_zeroLine);
            _chart.Children.Add(_equityFill);
            _chart.Children.Add(_equityLine);
            s.Children.Add(_chart);

            _statsText = Small("--");
            s.Children.Add(_statsText);

            // Stacked W / BE / L. The widths are star weights set at update
            // time, so WPF does the arithmetic and a zero-count segment simply
            // collapses instead of rendering a 1px sliver.
            Grid bar = new Grid { Height = 6, Margin = new Thickness(0, 3, 0, 6) };
            _wCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            _beCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            _lCol = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            bar.ColumnDefinitions.Add(_wCol);
            bar.ColumnDefinitions.Add(_beCol);
            bar.ColumnDefinitions.Add(_lCol);
            Border wSeg = new Border { Background = OkBrush };
            Border beSeg = new Border { Background = DimBrush };
            Border lSeg = new Border { Background = LossBrush };
            Grid.SetColumn(wSeg, 0); Grid.SetColumn(beSeg, 1); Grid.SetColumn(lSeg, 2);
            bar.Children.Add(wSeg); bar.Children.Add(beSeg); bar.Children.Add(lSeg);
            s.Children.Add(bar);

            // The last three trades. The row BACKGROUND is the magnitude bar —
            // a separate bar column would cost 60 of the 300 DIP and say the
            // same thing.
            for (int i = 0; i < 3; i++)
            {
                Grid g = new Grid { Height = 16, Margin = new Thickness(0, 1, 0, 1) };
                _tradeBar[i] = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x30, 0x4C, 0xC3, 0x8C)),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Width = 0
                };
                _tradeText[i] = Small("");
                g.Children.Add(_tradeBar[i]);
                g.Children.Add(_tradeText[i]);
                s.Children.Add(g);
            }

            return s;
        }
```

And add it to `BuildPanel`'s body, last:

```csharp
                _body.Children.Add(BuildHistorySection());
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -5`
Expected: PASS — `ALL PASS (n checks)` then `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxHistory.cs ninjascript/BreakBoxPanel.cs tests/HistoryTests.cs && \
git commit -m "feat(panel): history chart — Polyline + Polygon on a dashed zero

Views window off the newest RECORD, not the wall clock, so a Replay file
still renders. SparkPoints is pure and asserted on the two cases that
bite: a flat curve (divide by zero) and an all-negative one (zero pinned
to the top rather than off canvas). WPF shapes aliased — the combined
compilation unit drags DrawingTools' Line/Polygon/Polyline into scope.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```

---

### Task 69: One batched dispatcher update per bar

**Files:**
- Modify: `ninjascript/BreakBoxPanel.cs:369-432` (the `Readouts` region — `UpdatePanelStatus` and `UpdateHud` in full)
- Modify: `ninjascript/BreakBoxStrategy.cs:378-397` (the tail of `OnBarUpdate`, which calls `UpdateHud()` twice)
- Test: `scripts/check.sh`

**Interfaces:**
- Consumes: `BbGateReport.RowState` (Task 65) · `BbLogRing.Newest` (Task 66) · `BbHistory.View` / `CumulativeEquity` / `SparkPoints` (Tasks 61, 68) · `_history`, `_cfgHash` (Task 63) · from Phases 2–3: `private BbCloudState _cloudState;`, `private BbEngineState _engState;`, `private BbEntryEngine _owningEngine;`, `private int BarSeconds();`, `_uiCloudOn`
- Produces: `private sealed class PanelSnap` · `private void UpdatePanelStatus()` (now the only panel entry point per bar)

- [ ] **Step 1: Write the failing test**

Replace the whole `#region Readouts` (`:369-432`) of `ninjascript/BreakBoxPanel.cs` with the caller half:

```csharp
        #region Readouts

        // ONE snapshot per bar, built on the STRATEGY thread and applied by
        // exactly ONE dispatcher callback. v1 posted five separate InvokeAsync
        // closures per bar, each capturing live fields — which is both a torn
        // read (the fields move between callbacks) and five context switches on
        // a thread the strategy is forbidden from blocking.
        private sealed class PanelSnap
        {
            public string Status = "", Headline = "", SessionA = "", SessionB = "", Equity = "", Stats = "";
            public int StatusState;                             // 0 dim, 1 ok, 2 warn, 3 loss
            public readonly string[] GateName = new string[GateRows];
            public readonly string[] GateVal = new string[GateRows];
            // 0 passed · 1 the blocker · 2 never evaluated · 3 not a gate on the
            // active engine's ladder at all (the cloud's is shorter than the box's)
            public readonly int[] GateState = new int[GateRows];
            public readonly string[] Log = new string[3];
            public double[] Pts = new double[0];
            public double ZeroY;
            public double WinN, BeN, LossN;
            public readonly string[] TradeText = new string[3];
            public readonly double[] TradeBar = new double[3];
            public readonly int[] TradeState = new int[3];      // 0 dim (other config), 1 win, 2 loss
        }

        private void UpdatePanelStatus()
        {
            if (_panelRoot == null || ChartControl == null)
                return;

            PanelSnap s = new PanelSnap();
            FillStatus(s);
            FillGates(s);
            FillHistory(s);
            for (int i = 0; i < 3; i++)
                s.Log[i] = _log.Newest(i);

            ChartControl.Dispatcher.InvokeAsync(new Action(() => ApplySnap(s)));
        }

        #endregion
```

And drop the now-dead HUD calls from `OnBarUpdate` in `ninjascript/BreakBoxStrategy.cs` — the `UpdateHud();` before the `return` in the flatten branch and the pair at the tail become:

```csharp
            if (TimeToFlatten(secs))
            {
                FlattenAll("session_window");
                UpdatePanelStatus();
                return;
            }
```

```csharp
            UpdatePanelStatus();
        }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | grep -E "error CS|compiles clean"`
Expected: FAIL with `error CS0103: The name 'FillStatus' does not exist in the current context` (and `FillGates`, `FillHistory`, `ApplySnap`, plus `UpdateHud` no longer defined)

- [ ] **Step 3: Write minimal implementation**

Add to `#region Readouts` in `ninjascript/BreakBoxPanel.cs`:

```csharp
        // Shell-level blocks PREEMPT the gate ladder and replace the headline
        // (§9.3). This ordering is the fix for the defect that started the
        // rewrite: v1 showed READY while the box sat "(out of band)" and never
        // connected the two, and the user watched a dead strategy for an hour.
        private void FillStatus(PanelSnap s)
        {
            if (_lockout)
            {
                s.Status = "LOCKED OUT (" + _lockoutWhy + ")";
                s.StatusState = 3;
                s.Headline = _lockoutWhy == "manual"
                    ? "locked by hand — click LOCK OUT again to resume"
                    : "day P&L " + _dayPnl.ToString("C2", CultureInfo.CurrentCulture);
            }
            else if (_inTrade)
            {
                s.Status = "IN TRADE " + (_dir > 0 ? "LONG" : "SHORT");
                s.StatusState = 1;
                s.Headline = _qty + " @ " + _bracket.EntryPx.ToString("0.00", CultureInfo.InvariantCulture)
                           + "  stop " + _bracket.StopPx.ToString("0.00", CultureInfo.InvariantCulture)
                           + (_bracket.BeApplied ? " (BE)" : "");
            }
            else if (_entryPending)
            {
                s.Status = "ENTRY WORKING";
                s.StatusState = 2;
                s.Headline = "trigger " + _pendingAction.TriggerPx.ToString("0.00", CultureInfo.InvariantCulture)
                           + " — " + _entryBarsWaiting + " bars waiting";
            }
            else if (!_atr.IsWarm)
            {
                s.Status = "WARMING";
                s.StatusState = 0;
                s.Headline = "ATR " + _atr.BarsFed + "/" + AtrPeriod + " bars";
            }
            else if (!_uiAutoTrade)
            {
                s.Status = "AUTO-TRADE OFF";
                s.StatusState = 2;
                s.Headline = "the engines still track state — only entries are suppressed";
            }
            else
            {
                s.Status = "READY";
                s.StatusState = 1;
            }

            int secs = Time[0].Hour * 3600 + Time[0].Minute * 60 + Time[0].Second;
            int left = BbMath.HhmmToSecs(FlattenHhmm) - secs;
            if (left < 0) left += 24 * 3600;
            s.SessionA = string.Format(CultureInfo.InvariantCulture,
                "atr {0:0.00}   bar {1}s   flat in {2}h{3:00}m",
                _atr.IsWarm ? _atr.Value : 0.0, BarSeconds(), left / 3600, (left % 3600) / 60);
            // The active stop source is NAMED, not merely lit on a button:
            // `MA` means "far ribbon edge" at MaPeriod = RibbonSlow and
            // something else entirely otherwise, and that is invisible in a
            // three-letter toggle (§9.5).
            s.SessionB = "stop " + _uiStopSource + " (period " + MaPeriod + ")   cfg " + _cfgHash;
        }

        // §4.2: the ladder shown is the report of the engine that would act
        // NEXT under §4.1 ordering — Cloud when it is on, else Box. The two
        // reports are never merged; merging them is how one engine's blocker
        // ends up labelled with the other's gate names.
        private void FillGates(PanelSnap s)
        {
            bool cloud = _uiCloudOn;
            BbGateReport g = cloud ? _cloudState.Gate : _engState.Gate;
            string[] names = cloud ? CloudGates : BoxGates;
            int depth = g == null ? -1 : g.GateDepth;

            for (int i = 0; i < GateRows; i++)
            {
                // The two ladders are not the same length — the box's is ten
                // deep, the cloud's six. Rows past the end of the ACTIVE
                // engine's ladder are marked unused (3) and render as nothing,
                // rather than borrowing the other engine's name for that index.
                // Reaching for `names[i]` unguarded is an IndexOutOfRange on
                // every cloud bar, and padding CloudGates with box names would
                // be the quieter, worse version of the same bug.
                bool has = i < names.Length;
                s.GateName[i] = has ? names[i] : "";
                s.GateState[i] = has ? BbGateReport.RowState(i, depth) : 3;
                s.GateVal[i] = "";
            }

            // Passed rows carry the value the shell can read without reaching
            // into engine internals; the blocker carries the engine's own
            // BlockDetail, which is the only place "has 0.42, needs 0.60" is
            // known. A dimmed row deliberately carries nothing.
            if (s.GateState[0] == 0)
                s.GateVal[0] = _atr.Value.ToString("0.00", CultureInfo.InvariantCulture)
                             + " (" + AtrPeriod + " bars)";

            if (cloud && _cloudState != null)
            {
                if (s.GateState[1] == 0)
                    s.GateVal[1] = (_cloudState.RegimeLatched > 0 ? "long" : _cloudState.RegimeLatched < 0 ? "short" : "none")
                                 + " (latched " + Mins(_cloudState.RegimeLatchedAgeBars) + ")";
                if (s.GateState[2] == 0)
                    s.GateVal[2] = _cloudState.Armed
                        ? "armed " + _cloudState.AgeBars + " bars ago"
                        : "no token — " + _cloudState.BarsSinceLastArm + " bars since";
            }

            // Bounded by the ACTIVE ladder, not by GateRows: a depth the ladder
            // has no name for is an engine/panel mismatch, and writing its
            // detail into a blank row would hide the mismatch instead of it
            // showing up as an unlabelled blocker.
            if (depth >= 0 && depth < names.Length && g != null)
                s.GateVal[depth] = g.BlockDetail == null ? "" : g.BlockDetail;

            if (s.Headline.Length == 0)
                s.Headline = g == null || g.Block == null || g.Block.Length == 0
                    ? "all gates clear — waiting for the trigger bar"
                    : g.Block;
        }

        private string Mins(int bars)
        {
            int sec = bars * BarSeconds();
            return sec < 60 ? sec + "s" : (sec / 60) + "m";
        }

        private void FillHistory(PanelSnap s)
        {
            List<BbTradeRecord> view = BbHistory.View(_history, _histView);
            double[] cum = BbHistory.CumulativeEquity(view);
            double zeroY;
            // The POINTS are computed here, as plain doubles. PointCollection is
            // a Freezable and building one on this thread is exactly the
            // cross-thread ownership bug the frozen brushes avoid.
            s.Pts = BbHistory.SparkPoints(cum, ChartW, ChartH, out zeroY);
            s.ZeroY = zeroY;

            double total = cum.Length == 0 ? 0.0 : cum[cum.Length - 1];
            s.Equity = (total >= 0 ? "+" : "") + total.ToString("C2", CultureInfo.CurrentCulture);

            double biggest = 1.0;
            for (int i = 0; i < view.Count; i++)
            {
                if (view[i].Pnl > 0) s.WinN++;
                else if (view[i].Pnl < 0) s.LossN++;
                else s.BeN++;
                double abs = Math.Abs(view[i].Pnl);
                if (abs > biggest) biggest = abs;
            }
            double decided = s.WinN + s.LossN;
            s.Stats = string.Format(CultureInfo.InvariantCulture,
                "{0} trades   W{1} BE{2} L{3}   ·   {4} win",
                view.Count, (int)s.WinN, (int)s.BeN, (int)s.LossN,
                decided > 0 ? ((100.0 * s.WinN / decided).ToString("0", CultureInfo.InvariantCulture) + "%") : "--");

            for (int i = 0; i < 3; i++)
            {
                int idx = view.Count - 1 - i;
                if (idx < 0)
                {
                    s.TradeText[i] = "";
                    s.TradeBar[i] = 0.0;
                    continue;
                }
                BbTradeRecord r = view[idx];
                s.TradeText[i] = string.Format(CultureInfo.InvariantCulture, "#{0} {1} {2}   {3}",
                    idx + 1, r.Dir > 0 ? "LONG " : "SHORT",
                    r.Ts.ToString("HH:mm", CultureInfo.InvariantCulture),
                    (r.Pnl >= 0 ? "+" : "") + r.Pnl.ToString("0.00", CultureInfo.InvariantCulture));
                s.TradeBar[i] = ChartW * Math.Abs(r.Pnl) / biggest;
                // Trades from another configuration render DIMMED (§10). A
                // parameter change has to show as a visible seam — silently
                // mixing them is the contamination the hash exists to expose.
                s.TradeState[i] = r.CfgHash != _cfgHash ? 0 : (r.Pnl >= 0 ? 1 : 2);
            }
        }

        // The ONLY code in this file that runs on the WPF thread. It reads the
        // snapshot and nothing else — no strategy field is touched from here,
        // which is what makes the whole arrangement safe.
        private void ApplySnap(PanelSnap s)
        {
            if (_panelRoot == null)
                return;

            Brush st = s.StatusState == 1 ? OkBrush : s.StatusState == 2 ? WarnBrush
                     : s.StatusState == 3 ? LossBrush : DimBrush;
            if (_statusDot != null) _statusDot.Foreground = st;
            if (_statusText != null) _statusText.Text = s.Status;
            if (_headline != null) _headline.Text = s.Headline;
            if (_sessionA != null) _sessionA.Text = s.SessionA;
            if (_sessionB != null) _sessionB.Text = s.SessionB;

            for (int i = 0; i < GateRows; i++)
            {
                if (_gateName[i] == null) continue;
                _gateName[i].Text = s.GateName[i];
                _gateVal[i].Text = s.GateVal[i];
                if (s.GateState[i] == 3)
                {
                    // Past the end of the active engine's ladder: not a gate at
                    // all. Blank, not "not evaluated" — the cloud engine does
                    // not HAVE four more gates it skipped.
                    _gateMark[i].Text = ""; _gateVal[i].Text = "";
                }
                else if (s.GateState[i] == 0)
                {
                    _gateMark[i].Text = "OK"; _gateMark[i].Foreground = OkBrush;
                    _gateName[i].Foreground = TextBrush; _gateVal[i].Foreground = DimBrush;
                }
                else if (s.GateState[i] == 1)
                {
                    _gateMark[i].Text = "\u2715"; _gateMark[i].Foreground = WarnBrush;
                    _gateName[i].Foreground = WarnBrush; _gateVal[i].Foreground = WarnBrush;
                }
                else
                {
                    _gateMark[i].Text = "\u00B7"; _gateMark[i].Foreground = DimBrush;
                    _gateName[i].Foreground = DimBrush;
                    _gateVal[i].Text = "not evaluated"; _gateVal[i].Foreground = DimBrush;
                }
            }

            for (int i = 0; i < LogRows; i++)
                if (_logText[i] != null) _logText[i].Text = s.Log[i];

            if (_equityText != null)
            {
                _equityText.Text = s.Equity;
                _equityText.Foreground = s.Equity.StartsWith("-", StringComparison.Ordinal) ? LossBrush : OkBrush;
            }
            if (_statsText != null) _statsText.Text = s.Stats;

            if (_equityLine != null)
            {
                PointCollection line = new PointCollection(s.Pts.Length / 2);
                for (int i = 0; i < s.Pts.Length; i += 2)
                    line.Add(new Point(s.Pts[i], s.Pts[i + 1]));
                _equityLine.Points = line;

                // The fill is the same polyline closed down to the zero
                // baseline, not to the bottom of the box: an underwater segment
                // has to shade the WRONG side of zero or the picture lies.
                PointCollection fill = new PointCollection(line.Count + 2);
                if (line.Count > 0)
                {
                    fill.Add(new Point(line[0].X, s.ZeroY));
                    for (int i = 0; i < line.Count; i++) fill.Add(line[i]);
                    fill.Add(new Point(line[line.Count - 1].X, s.ZeroY));
                }
                _equityFill.Points = fill;
                _zeroLine.Y1 = s.ZeroY;
                _zeroLine.Y2 = s.ZeroY;
            }

            // Star weights, so a zero-count segment collapses instead of
            // rendering a misleading sliver.
            if (_wCol != null)
            {
                _wCol.Width = new GridLength(s.WinN, GridUnitType.Star);
                _beCol.Width = new GridLength(s.BeN, GridUnitType.Star);
                _lCol.Width = new GridLength(s.LossN, GridUnitType.Star);
            }

            for (int i = 0; i < 3; i++)
            {
                if (_tradeText[i] == null) continue;
                _tradeText[i].Text = s.TradeText[i];
                _tradeText[i].Foreground = s.TradeState[i] == 0 ? DimBrush : TextBrush;
                _tradeBar[i].Width = s.TradeBar[i];
                _tradeBar[i].Background = s.TradeState[i] == 0
                    ? new SolidColorBrush(Color.FromArgb(0x18, 0x6A, 0x72, 0x7E))
                    : s.TradeState[i] == 1
                        ? new SolidColorBrush(Color.FromArgb(0x30, 0x4C, 0xC3, 0x8C))
                        : new SolidColorBrush(Color.FromArgb(0x30, 0xD9, 0x53, 0x4F));
            }

            Paint(_lockBtn, _lockout);
            Paint(_autoBtn, _uiAutoTrade);
        }
```

Add the two usings `BreakBoxPanel.cs` now needs (`System.Collections.Generic` for `List<BbTradeRecord>`, `System.Windows` already present for `Point`):

```csharp
using System.Collections.Generic;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && bash scripts/check.sh 2>&1 | tail -6`
Expected: PASS — `ALL PASS (n checks)` then `compiles clean`

- [ ] **Step 5: Commit**

```bash
cd "/home/javlo/Code Projects/main-project/projects/Trading/BreakBox" && \
git add ninjascript/BreakBoxPanel.cs ninjascript/BreakBoxStrategy.cs && \
git commit -m "feat(panel): one batched snapshot per bar drives the whole panel

Built on the strategy thread, applied by a single dispatcher callback
that reads nothing but the snapshot. v1 posted five closures per bar,
each capturing live fields — a torn read and five context switches on a
thread the strategy must not block.

Shell blocks now preempt the ladder, so READY can no longer sit above
(out of band), and trades from another cfgHash render dimmed.

Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>"
```
