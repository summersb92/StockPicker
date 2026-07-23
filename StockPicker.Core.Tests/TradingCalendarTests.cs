using System;
using StockPicker.Services;
using Xunit;

namespace StockPicker.Core.Tests
{
    /// <summary>
    /// Trading-day math: weekends and NYSE holidays are closed, plain weekdays are
    /// open, and the next-trading-day / week-start helpers roll correctly over
    /// weekends and holidays. All dates are fixed so the tests never depend on
    /// when they run.
    /// </summary>
    public class TradingCalendarTests
    {
        private readonly TradingCalendar _calendar = new();

        // ── IsTradingDay: weekends ────────────────────────────────────────────

        [Theory]
        [InlineData(2025, 6, 7)]   // Saturday
        [InlineData(2025, 6, 8)]   // Sunday
        public void Weekend_IsNotTradingDay(int y, int m, int d)
            => Assert.False(_calendar.IsTradingDay(new DateTime(y, m, d)));

        [Fact]
        public void PlainTuesday_IsTradingDay()
            => Assert.True(_calendar.IsTradingDay(new DateTime(2025, 6, 10)));

        // ── IsTradingDay: 2025 NYSE holidays the implementation encodes ──────

        [Theory]
        [InlineData(2025,  1,  1)]  // New Year's Day (Wednesday)
        [InlineData(2025,  1, 20)]  // MLK Day (3rd Monday of January)
        [InlineData(2025,  2, 17)]  // Presidents Day (3rd Monday of February)
        [InlineData(2025,  4, 18)]  // Good Friday (Easter 2025 = Apr 20)
        [InlineData(2025,  5, 26)]  // Memorial Day (last Monday of May)
        [InlineData(2025,  6, 19)]  // Juneteenth (Thursday)
        [InlineData(2025,  7,  4)]  // Independence Day (Friday)
        [InlineData(2025,  9,  1)]  // Labor Day (1st Monday of September)
        [InlineData(2025, 11, 27)]  // Thanksgiving (4th Thursday of November)
        [InlineData(2025, 12, 25)]  // Christmas (Thursday)
        public void NyseHoliday2025_IsNotTradingDay(int y, int m, int d)
            => Assert.False(_calendar.IsTradingDay(new DateTime(y, m, d)));

        [Fact]
        public void ObservedHoliday_SaturdayFourthOfJuly_ClosesTheFridayBefore()
        {
            // July 4 2026 falls on a Saturday → NYSE observes Friday July 3.
            Assert.False(_calendar.IsTradingDay(new DateTime(2026, 7, 3)));
        }

        [Fact]
        public void ObservedHoliday_SundayFourthOfJuly_ClosesTheMondayAfter()
        {
            // July 4 2027 falls on a Sunday → NYSE observes Monday July 5.
            Assert.False(_calendar.IsTradingDay(new DateTime(2027, 7, 5)));
        }

        // ── NextTradingDay ───────────────────────────────────────────────────

        [Fact]
        public void NextTradingDay_FromFriday_RollsOverWeekendToMonday()
        {
            // Friday Jun 6 2025 → Monday Jun 9 2025.
            Assert.Equal(new DateTime(2025, 6, 9),
                         _calendar.NextTradingDay(new DateTime(2025, 6, 6)));
        }

        [Fact]
        public void NextTradingDay_SkipsHolidayAndWeekend()
        {
            // Thursday Jul 3 2025 → Fri Jul 4 is a holiday, Sat/Sun closed → Monday Jul 7.
            Assert.Equal(new DateTime(2025, 7, 7),
                         _calendar.NextTradingDay(new DateTime(2025, 7, 3)));
        }

        [Fact]
        public void NextTradingDay_MidWeek_IsSimplyTomorrow()
        {
            // Tuesday Jun 10 2025 → Wednesday Jun 11 2025.
            Assert.Equal(new DateTime(2025, 6, 11),
                         _calendar.NextTradingDay(new DateTime(2025, 6, 10)));
        }

        // ── NextWeekStart / WeekEndFor ───────────────────────────────────────

        [Fact]
        public void NextWeekStart_OnMonday_ReturnsSameDay()
        {
            var monday = new DateTime(2025, 6, 9);
            Assert.Equal(monday, _calendar.NextWeekStart(monday));
        }

        [Theory]
        [InlineData(2025, 6, 10)]  // Tuesday
        [InlineData(2025, 6, 13)]  // Friday
        [InlineData(2025, 6, 14)]  // Saturday
        public void NextWeekStart_MidWeek_ReturnsFollowingMonday(int y, int m, int d)
        {
            var result = _calendar.NextWeekStart(new DateTime(y, m, d));
            Assert.Equal(new DateTime(2025, 6, 16), result);   // Monday of the next week
            Assert.Equal(DayOfWeek.Monday, result.DayOfWeek);
        }

        [Fact]
        public void WeekEndFor_ReturnsTheFridayOfTheSameWeek()
        {
            var monday = new DateTime(2025, 6, 9);
            var friday = _calendar.WeekEndFor(monday);
            Assert.Equal(new DateTime(2025, 6, 13), friday);
            Assert.Equal(DayOfWeek.Friday, friday.DayOfWeek);
        }
    }
}
