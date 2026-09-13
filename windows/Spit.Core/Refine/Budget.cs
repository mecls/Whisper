namespace Spit.Core;

/// How long the client waits for `/v1/refine`. Port of mac/Voice/Refine/Budget.swift.
public static class Budget
{
    public const int MinMs = 3500;
    public const int MaxMs = 15000;

    /// docs/PLAN.md §3.5: clamp(2500 + 12 × rawChars, 3500, 15000). `rawChars` counts what Swift's
    /// `String.count` counts — grapheme clusters — so both clients give one transcript one budget.
    public static int Ms(int rawChars) => (int)Math.Clamp(2500L + 12L * rawChars, MinMs, MaxMs);
}
