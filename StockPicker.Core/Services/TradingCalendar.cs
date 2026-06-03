using System;

namespace StockPicker.Services
{
    /// <summary>
    /// Implements <see cref="ITradingCalendar"/> and also exposes static helpers
    /// for determining which trading day's picks should be shown.
    /// All logic is expressed in US Eastern time. Market holidays are not
    /// accounted for — only weekends are skipped.
    /// </summary>
    public class TradingCalendar : ITradingCalendar
    {
        private static readonly TimeZoneInfo ET =
            TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

        // ── ITradingCalendar ──────────────────────────────────────────────────

        /// <inheritdoc/>
        public bool IsTradingDay(DateTime date)
            => date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday;

        /// <inheritdoc/>
        public DateTime NextTradingDay(DateTime date)
        {
            var next = date.AddDays(1);
            while (!IsTradingDay(next))
                next = next.AddDays(1);
            return next;
        }

        /// <inheritdoc/>
        public DateTime NextWeekStart(DateTime date)
        {
            // If today is Monday, return today; otherwise return the Monday of the NEXT week.
            if (date.DayOfWeek == DayOfWeek.Monday)
                return date;

            // Advance to next Monday
            int daysUntilMonday = ((int)DayOfWeek.Monday - (int)date.DayOfWeek + 7) % 7;
            if (daysUntilMonday == 0) daysUntilMonday = 7; // already past Monday → next Monday
            return date.AddDays(daysUntilMonday);
        }

        /// <inheritdoc/>
        public DateTime WeekEndFor(DateTime monday)
            => monday.AddDays(4); // Monday + 4 = Friday

        // ── Static helpers (used by MainViewModel / PortfolioService) ─────────

        /// <summary>
        /// Returns the date of the trading session the user should see picks for right now:
        /// <list type="bullet">
        ///   <item>Before 4:00 PM ET on a weekday → today</item>
        ///   <item>At or after 4:00 PM ET on a weekday, or any time on a weekend → next weekday</item>
        /// </list>
        /// </summary>
        public static DateTime TargetTradingDay()
        {
            var etNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ET);
            var date  = etNow.Date;

            bool isWeekend  = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            bool afterClose = !isWeekend && etNow.TimeOfDay >= TimeSpan.FromHours(16);

            return (isWeekend || afterClose) ? NextWeekdayStatic(date) : date;
        }

        /// <summary>
        /// Formats a trading-day date for display, e.g. "Wednesday, May 7 2026".
        /// </summary>
        public static string FormatTradingDay(DateTime date)
            => date.ToString("dddd, MMM d yyyy");

        /// <summary>Returns true if <paramref name="date"/> is the same calendar day as today in ET.</summary>
        public static bool IsToday(DateTime date)
        {
            var etNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ET);
            return date.Date == etNow.Date;
        }

        // ── Private ───────────────────────────────────────────────────────────

        private static DateTime NextWeekdayStatic(DateTime from)
        {
            var next = from.AddDays(1);
            while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                next = next.AddDays(1);
            return next;
        }
    }
}
