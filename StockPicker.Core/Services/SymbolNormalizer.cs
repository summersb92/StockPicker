using System;

namespace StockPicker.Services
{
    /// <summary>
    /// Canonicalizes ticker symbols so every universe, cache key, and merge key uses
    /// one form. Class shares are written differently by different providers
    /// (BRK.B vs BRK-B); the app's canonical form is DASH (Yahoo's convention),
    /// because Yahoo is the default universe/data source and existing caches
    /// already store dash-form symbols.
    ///
    /// Providers that require dot form should convert at their request boundary.
    /// </summary>
    public static class SymbolNormalizer
    {
        /// <summary>Uppercase, trimmed, dot→dash (e.g. " brk.b " → "BRK-B").</summary>
        public static string ToCanonical(string? symbol) =>
            string.IsNullOrWhiteSpace(symbol)
                ? string.Empty
                : symbol.Trim().ToUpperInvariant().Replace('.', '-');
    }
}
