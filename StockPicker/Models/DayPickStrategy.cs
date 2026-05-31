namespace StockPicker.Models
{
    /// <summary>
    /// Scoring strategy used by <see cref="StockPicker.Services.DayPickService"/>
    /// when generating intraday picks.
    /// </summary>
    public enum DayPickStrategy
    {
        /// <summary>
        /// Volume surge + gap + strong intraday move + momentum RSI.
        /// Best in trending, high-activity markets.
        /// </summary>
        Momentum,

        /// <summary>
        /// Oversold RSI + below SMA20 + volume drying up.
        /// Targets bounce plays after a sell-off.
        /// </summary>
        MeanReversion,

        /// <summary>
        /// Price breaking above a key resistance level on elevated volume.
        /// Targets continuation breakouts.
        /// </summary>
        Breakout,

        /// <summary>
        /// Stocks with elevated IV and upcoming catalysts (earnings within 2 days).
        /// Targets volatility expansion plays.
        /// </summary>
        EarningsPlay,
    }
}
