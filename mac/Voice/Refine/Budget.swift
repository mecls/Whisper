enum Budget {
    /// docs/PLAN.md §3.5: clamp(2500 + 12 × rawChars, 3500, 15000)
    static func ms(rawChars: Int) -> Int { min(15000, max(3500, 2500 + 12 * rawChars)) }
}
