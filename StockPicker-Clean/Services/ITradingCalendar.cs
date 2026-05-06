using System;

namespace StockPicker.Services
{
    /// <summary>
    /// Market-date helpers. Kept behind an interface because "what counts as a
    /// trading day" is easy to get wrong and will need a real calendar source
    /// (NYSE holidays, early closes, etc.) before this app is used for real trades.
    /// </summary>
    public interface ITradingCalendar
    {
        /// <summary>True if <paramref name="date"/> is a trading day (not a weekend/holiday).</summary>
        bool IsTradingDay(DateTime date);

        /// <summary>Return the next trading day on or after <paramref name="date"/>.</summary>
        DateTime NextTradingDay(DateTime date);

        /// <summary>
        /// Return the Monday of the upcoming trading week. If <paramref name="date"/>
        /// is a Monday, returns that same Monday. If it's Tue–Sun, returns the
        /// Monday of the following week (so a mid-week scan proposes next week's
        /// trade rather than one that's already started).
        /// </summary>
        DateTime NextWeekStart(DateTime date);

        /// <summary>
        /// Return the Friday of the same trading week as <paramref name="monday"/>.
        /// </summary>
        DateTime WeekEndFor(DateTime monday);
    }
}
