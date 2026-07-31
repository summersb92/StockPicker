namespace StockPicker.Models
{
    /// <summary>
    /// Which side of the earnings date the earnings scan looks at.
    /// </summary>
    public enum EarningsScanMode
    {
        /// <summary>
        /// Stocks that have not reported yet, inside a forward window. Scored on the chance of
        /// rising by the target % — implied move, momentum, drift. The original behaviour.
        /// </summary>
        Upcoming = 0,

        /// <summary>
        /// Stocks that reported within the last few days. Scored as a rebound candidate
        /// instead: how hard it sold off on the print, how much upside analysts still see,
        /// and whether EPS actually beat. Looks for a good business punished by the market.
        /// </summary>
        JustReported = 1,
    }
}
