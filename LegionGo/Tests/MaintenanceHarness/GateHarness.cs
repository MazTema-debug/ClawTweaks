// SPDX-License-Identifier: AGPL-3.0-only
// Synthetic test entry point. It never enters LegionTweaks.Program.Main.
using System;
internal static partial class Program
{
    private static int passed;
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Test(string name, Action body) { body(); passed++; Console.WriteLine("PASS " + name); }
    private static void Main(string[] args)
    {
        if (TryRunY3GateFixture(args)) return;
        FpsY3ExclusiveGateTests();
        FpsY3AdmissionTests();
        Console.WriteLine(passed + " synthetic gate tests passed.");
    }
}
