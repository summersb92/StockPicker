using System;
using System.Collections.Generic;

namespace StockPicker.Services
{
    /// <summary>
    /// Implements <see cref="ITradingCalendar"/> and also exposes static helpers
    /// for determining which trading day's picks should be shown.
    /// All logic is expressed in US Eastern time. Skips weekends AND NYSE market
    /// holidays (fixed-date holidays are observed-date adjusted; floating holidays
    /// — MLK, Presidents, Memorial, Labor, Thanksgiving — are computed per year;
    /// Good Friday is derived from the Easter algorithm).
    /// </summary>
    public class TradingCalendar : ITradingCalendar
    {
        private static readonly TimeZoneInfo ET =
            TimeZoneInfo.FindSystemTimeZoneById(
                OperatingSystem.IsWindows() ? "Eastern Standard Time" : "America/New_York");

        // ── ITradingCalendar ──────────────────────────────────────────────────

        /// <inheritdoc/>
        public bool IsTradingDay(DateTime date)
            => date.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday
               && !IsMarketHoliday(date);

        // ── NYSE holiday calendar ─────────────────────────────────────────────

        /// <summary>Cached holiday sets, computed once per calendar year.</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, HashSet<DateTime>>
            _holidayCache = new();

        /// <summary>True when the NYSE is closed on <paramref name="date"/> (full-day holidays only).</summary>
        public static bool IsMarketHoliday(DateTime date)
            => _holidayCache.GetOrAdd(date.Year, ComputeHolidays).Contains(date.Date);

        /// <summary>
        /// Computes the full-day NYSE holidays for a year:
        /// New Year's, MLK, Presidents, Good Friday, Memorial, Juneteenth,
        /// Independence Day, Labor Day, Thanksgiving, Christmas.
        /// Fixed-date holidays observe Friday when they fall on Saturday and
        /// Monday when they fall on Sunday (NYSE convention; a Sat New Year's is
        /// not observed early because Dec 31 belongs to the prior year — the NYSE
        /// does not close Dec 31 for it, so we skip that edge deliberately).
        /// </summary>
        private static HashSet<DateTime> ComputeHolidays(int year)
        {
            var days = new HashSet<DateTime>();

            void AddObserved(int month, int day)
            {
                var d = new DateTime(year, month, day);
                if (d.DayOfWeek == DayOfWeek.Saturday)      d = d.AddDays(-1);
                else if (d.DayOfWeek == DayOfWeek.Sunday)   d = d.AddDays(1);
                if (d.Year == year) days.Add(d.Date);
            }

            static DateTime NthWeekday(int year, int month, DayOfWeek dow, int n)
            {
                var d = new DateTime(year, month, 1);
                int offset = ((int)dow - (int)d.DayOfWeek + 7) % 7;
                return d.AddDays(offset + 7 * (n - 1));
            }

            static DateTime LastWeekday(int year, int month, DayOfWeek dow)
            {
                var d = new DateTime(year, month, DateTime.DaysInMonth(year, month));
                int offset = ((int)d.DayOfWeek - (int)dow + 7) % 7;
                return d.AddDays(-offset);
            }

            AddObserved(1, 1);                                          // New Year's Day
            days.Add(NthWeekday(year, 1, DayOfWeek.Monday, 3).Date);    // MLK Day
            days.Add(NthWeekday(year, 2, DayOfWeek.Monday, 3).Date);    // Presidents Day
            days.Add(EasterSunday(year).AddDays(-2).Date);              // Good Friday
            days.Add(LastWeekday(year, 5, DayOfWeek.Monday).Date);      // Memorial Day
            if (year >= 2022) AddObserved(6, 19);                       // Juneteenth (NYSE since 2022)
            AddObserved(7, 4);                                          // Independence Day
            days.Add(NthWeekday(year, 9, DayOfWeek.Monday, 1).Date);    // Labor Day
            days.Add(NthWeekday(year, 11, DayOfWeek.Thursday, 4).Date); // Thanksgiving
            AddObserved(12, 25);                                        // Christmas

            return days;
        }

        /// <summary>Anonymous Gregorian (Meeus/Jones/Butcher) Easter algorithm.</summary>
        private static DateTime EasterSunday(int year)
        {
            int a = year % 19,
                b = year / 100, c = year % 100,
                d = b / 4, e = b % 4,
                f = (b + 8) / 25, g = (b - f + 1) / 3,
                h = (19 * a + b - d - g + 15) % 30,
                i = c / 4, k = c % 4,
                l = (32 + 2 * e + 2 * i - h - k) % 7,
                m = (a + 11 * h + 22 * l) / 451;
            int month = (h + l - 7 * m + 114) / 31;
            int day   = ((h + l - 7 * m + 114) % 31) + 1;
            return new DateTime(year, month, day);
        }

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

            bool closedToday = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                               || IsMarketHoliday(date);
            bool afterClose  = !closedToday && etNow.TimeOfDay >= TimeSpan.FromHours(16);

            return (closedToday || afterClose) ? NextWeekdayStatic(date) : date;
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
            while (next.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                   || IsMarketHoliday(next))
                next = next.AddDays(1);
            return next;
        }
    }
}
