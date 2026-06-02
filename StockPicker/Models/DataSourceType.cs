namespace StockPicker.Models
{
    public enum DataSourceType
    {
        YahooFinance,
        Stooq,
        Alpaca,
        AlphaVantage,
        Finnhub,
        Polygon,
        Tiingo
    }

    public static class DataSourceTypeExtensions
    {
        public static string DisplayName(this DataSourceType src) => src switch
        {
            DataSourceType.YahooFinance  => "Yahoo Finance",
            DataSourceType.Stooq         => "Stooq",
            DataSourceType.Alpaca        => "Alpaca",
            DataSourceType.AlphaVantage  => "Alpha Vantage",
            DataSourceType.Finnhub       => "Finnhub",
            DataSourceType.Polygon       => "Polygon",
            DataSourceType.Tiingo        => "Tiingo",
            _                            => src.ToString()
        };

        public static string ShortName(this DataSourceType src) => src switch
        {
            DataSourceType.YahooFinance  => "Yahoo",
            DataSourceType.Stooq         => "Stooq",
            DataSourceType.Alpaca        => "Alpaca",
            DataSourceType.AlphaVantage  => "Alpha Vantage",
            DataSourceType.Finnhub       => "Finnhub",
            DataSourceType.Polygon       => "Polygon",
            DataSourceType.Tiingo        => "Tiingo",
            _                            => src.ToString()
        };
    }
}
