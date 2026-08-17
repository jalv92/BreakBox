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
