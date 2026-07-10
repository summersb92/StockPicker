using System;
using StockPicker.Services;
using Xunit;

namespace StockPicker.Core.Tests
{
    /// <summary>
    /// NYSE trading-day / holiday rules. All dates are EXPLICITLY pinned (never
    /// DateTime.Today-relative) so the suite is deterministic and passes on a Linux
    /// runner, where <see cref="TradingCalendar"/> resolves the "America/New_York"
    /// time-zone id in its static initializer.
    /// </summary>
    public class TradingCalendarTests
    {
        private readonly TradingCalendar _cal = new();

        [Fact]
        public void Weekends_AreNotTradingDays()
        {
            var saturday = new DateTime(2026, 7, 11);
            var sunday   = new DateTime(2026, 7, 12);

            Assert.Equal(DayOfWeek.Saturday, saturday.DayOfWeek);
            Assert.Equal(DayOfWeek.Sunday, sunday.DayOfWeek);
            Assert.False(_cal.IsTradingDay(saturday));
            Assert.False(_cal.IsTradingDay(sunday));
        }

        [Fact]
        public void SaturdayIndependenceDay_2026_IsObservedOnFriday()
        {
            // July 4 2026 falls on a Saturday → NYSE observes it on Friday July 3.
            var july4  = new DateTime(2026, 7, 4);
            var july3  = new DateTime(2026, 7, 3);

            Assert.Equal(DayOfWeek.Saturday, july4.DayOfWeek);

            // The observed (Friday) date is the holiday and is not a trading day.
            Assert.True(TradingCalendar.IsMarketHoliday(july3));
            Assert.False(_cal.IsTradingDay(july3));

            // The nominal Saturday date itself is not in the holiday set.
            Assert.False(TradingCalendar.IsMarketHoliday(july4));
        }

        [Fact]
        public void GoodFriday_2026_IsComputedCorrectly()
        {
            var goodFriday = new DateTime(2026, 4, 3);
            Assert.True(TradingCalendar.IsMarketHoliday(goodFriday));
            Assert.False(_cal.IsTradingDay(goodFriday));
        }

        [Fact]
        public void Juneteenth_2026_IsIncluded()
        {
            var juneteenth = new DateTime(2026, 6, 19);
            Assert.True(TradingCalendar.IsMarketHoliday(juneteenth));
            Assert.False(_cal.IsTradingDay(juneteenth));
        }

        [Fact]
        public void NewYearsDay_2026_IsAHoliday()
        {
            var newYears = new DateTime(2026, 1, 1); // Thursday
            Assert.True(TradingCalendar.IsMarketHoliday(newYears));
            Assert.False(_cal.IsTradingDay(newYears));
        }

        [Fact]
        public void NormalWeekday_IsATradingDay_AndNotAHoliday()
        {
            var wednesday = new DateTime(2026, 7, 8);
            Assert.Equal(DayOfWeek.Wednesday, wednesday.DayOfWeek);
            Assert.False(TradingCalendar.IsMarketHoliday(wednesday));
            Assert.True(_cal.IsTradingDay(wednesday));
        }
    }
}
