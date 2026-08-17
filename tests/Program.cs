// Test harness for BreakBoxCore.cs and BreakBoxExits.cs — the two pure files
// that carry every decision. No framework: an assert counter and a `Main` that
// exits non-zero, which is all a build gate needs and all anyone has to learn.
//
// This runner is ALSO the compile gate for the pure files: `nt8c check` on a
// single file cannot see its sibling's types and reports CS0246 on every one of
// them (VeeSnapCore.cs, in production, fails the same way). Compiling all three
// together here is what actually proves they build.
//
// Run: dotnet run --project tests
using System;

public static class T
{
    public static int Failures;
    public static int Checks;

    public static void Check(bool ok, string name)
    {
        Checks++;
        if (ok) { Console.WriteLine("  PASS " + name); return; }
        Failures++;
        Console.WriteLine("  FAIL " + name);
    }

    public static void CheckClose(double a, double b, string name, double eps = 1e-9)
    {
        Check(Math.Abs(a - b) <= eps, name + " (" + a.ToString("R") + " vs " + b.ToString("R") + ")");
    }

    public static void CheckInt(int a, int b, string name)
    {
        Check(a == b, name + " (" + a + " vs " + b + ")");
    }

    public static void Section(string name)
    {
        Console.WriteLine();
        Console.WriteLine("== " + name);
    }
}

public static class Program
{
    public static int Main()
    {
        BoxTests.Run();
        BracketTests.Run();
        ShellTests.Run();
        VisionTests.Run();
        CloudTests.Run();
        ArbitrationTests.Run();
        // Phase 4 inserts HistoryTests.Run() after this line. Insert, never replace.
        Console.WriteLine();
        Console.WriteLine(T.Failures == 0
            ? "ALL PASS (" + T.Checks + " checks)"
            : T.Failures + " FAILURES of " + T.Checks + " checks");
        return T.Failures == 0 ? 0 : 1;
    }
}
