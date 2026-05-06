using System;

namespace StockPicker.Services
{
    /// <summary>
    /// STUB implementation — handles weekends only.
    /// </summary>
    /// <remarks>
    /// ⚠️ INTENT — why this is isolated behind its own service:
    ///   Trading-date calculations are deceptively easy to get wrong. This stub
    ///   treats Saturday and Sunday as non-trading days and that's IT. It will
    ///   happily return Jan 1 / July 4 / Thanksgiving as trading days.
    ///
    /// BEFORE USING THIS FOR REAL TRADES:
    ///   - Replace with a proper market-calendar source. Options:
    ///       * NYSE holiday calendar (hardcoded table, refreshed annually)
    ///       * "NYSE-TradingSchedule" NuGet package
    ///       * Pull from the data provider (Polygon/IEX both expose market calendars)
    ///   - Handle early-close days (e.g. day after Thanksgiving) if the app ever
    ///     surfaces intraday times rather than just dates.
    ///   - Consider international markets if the universe ever includes non-US tickers.
    ///
    /// Because it's behind an interface, the fix lives in exactly one file.
    /// </remarks>
    public class TradingCalendar : ITradingCalendar
    {
        public bool IsTradingDay(DateTime date)
        {
            // TODO: also exclude NYSE holidays. See class remarks.
            return date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        }

        public DateTime NextTradingDay(DateTime date)
        {
            var d = date.Date;
            while (!IsTradingDay(d))
                d = d.AddDays(1);
            return d;
        }

        public DateTime NextWeekStart(DateTime date)
        {
            var d = date.Date;

            // If we're already on a Monday, that IS the next week start.
            // If we're Tue–Fri, the user is mid-week and should plan for NEXT Monday
            // (proposing a Quick trade that starts Wednesday would be nonsense).
            // If we're Sat–Sun, we're already "past" this trading week; next Monday.
            int daysUntilMonday = d.DayOfWeek switch
            {
                DayOfWeek.Monday    => 0,
                DayOfWeek.Tuesday   => 6,
                DayOfWeek.Wednesday => 5,
                DayOfWeek.Thursday  => 4,
                DayOfWeek.Friday    => 3,
                DayOfWeek.Saturday  => 2,
                DayOfWeek.Sunday    => 1,
                _                   => 0
            };

            return d.AddDays(daysUntilMonday);
        }

        public DateTime WeekEndFor(DateTime monday)
        {
            // Friday = Monday + 4 days. Real implementation should verify neither
            // Monday nor Friday is a market holiday, and if so shift in (buy Tuesday,
            // sell Thursday, etc.) — a decision the real calendar service makes.
            return monday.Date.AddDays(4);
        }
    }
}
