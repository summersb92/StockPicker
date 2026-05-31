using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Data;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using StockPicker.Models;
using StockPicker.Services;

namespace StockPicker.ViewModels
{
    /// <summary>
    /// Orchestrates the weekly scan pipeline in two distinct phases:
    ///
    ///   Phase 1 — FETCH (triggered by the ↺ refresh button or the startup scan):
    ///     Downloads price history and live quote data from all enabled data sources
    ///     and caches everything in memory.
    ///
    ///   Phase 2 — APPLY (triggered automatically whenever Strategy, Target Profit,
    ///     or Universe Size changes, or immediately after Phase 1):
    ///     Runs the analysis and recommendation engine against the cached data.
    ///     No network calls — completes in milliseconds without flashing the grid.
    ///
    /// Switching strategies / target after a scan is therefore instant.
    /// Multiple data sources fetch history in parallel; OHLCV bars are merged
    /// (averaged by date) before analysis so the engine always sees one clean series.
    /// </summary>
    public class MainViewModel : ViewModelBase
    {
        private readonly IStockDataService      _dataService;
        private readonly IAnalysisService       _analysisService;
        private readonly IRecommendationService _recommendationService;
        private readonly IStrategyProvider      _strategyProvider;
        private readonly IPortfolioService      _portfolioService;
        private readonly ScanCacheService       _scanCacheService;
        private readonly UserSettingsService    _userSettingsService;
        private readonly IDayPickService        _dayPickService;
        private UserSettings                    _userSettings;

        // ── Market index refresh timer ────────────────────────────────────────
        private readonly DispatcherTimer _marketTimer;
        private DateTime? _marketIndexUpdatedAt;

        /// <summary>
        /// Index symbols and their display names.
        /// Yahoo Finance serves these the same as regular equity quotes.
        /// </summary>
        private static readonly (string Symbol, string Name)[] _indexSymbols =
        {
            ("^DJI",  "DOW"),
            ("^GSPC", "S&P 500"),
            ("^IXIC", "NASDAQ"),
            ("^RUT",  "Russell 2K"),
        };

        // ── Cache (populated during Phase 1) ─────────────────────────────────
        private IReadOnlyList<Stock>?                          _cachedUniverse;
        private Dictionary<string, IReadOnlyList<StockQuote>>? _cachedHistory;
        private Dictionary<string, QuoteSummary>?              _cachedSummaries;
        private Dictionary<string, (string Name, string Sector)>? _cachedNameLookup;
        private DateTime                                        _cachedWeekStart;
        private DateTime                                        _cachedWeekEnd;

        // ── Multi-source caches ───────────────────────────────────────────────
        // Per-source history: source → (symbol → history)
        private Dictionary<DataSourceType, Dictionary<string, IReadOnlyList<StockQuote>>> _cachedHistoryPerSource = new();
        // Which sources contributed data for each symbol (after merge)
        private Dictionary<string, List<DataSourceType>> _cachedSourcesBySymbol = new();

        // ── Auto-refresh timer ────────────────────────────────────────────────
        private readonly DispatcherTimer _refreshTimer;

        public MainViewModel()
            : this(new YahooFinanceStockDataService(),
                   new AnalysisService(),
                   new RecommendationService(),
                   new StrategyProvider(),
                   new PortfolioService(),
                   new ScanCacheService(),
                   new UserSettingsService(),
                   new DayPickService()) { }

        public MainViewModel(
            IStockDataService      dataService,
            IAnalysisService       analysisService,
            IRecommendationService recommendationService,
            IStrategyProvider      strategyProvider,
            IPortfolioService      portfolioService,
            ScanCacheService       scanCacheService,
            UserSettingsService    userSettingsService,
            IDayPickService        dayPickService)
        {
            _dataService           = dataService;
            _analysisService       = analysisService;
            _recommendationService = recommendationService;
            _strategyProvider      = strategyProvider;
            _portfolioService      = portfolioService;
            _scanCacheService      = scanCacheService;
            _userSettingsService   = userSettingsService;
            _dayPickService        = dayPickService;

            // Load user settings synchronously (tiny file — safe in constructor).
            _userSettings = _userSettingsService.Load();

            Strategies        = new ObservableCollection<TradingStrategy>(_strategyProvider.GetStrategies());
            _selectedStrategy = _strategyProvider.GetDefault();

            // Restore last-used strategy (falls back to provider default if name not found).
            if (!string.IsNullOrEmpty(_userSettings.LastStrategyName))
            {
                var restored = Strategies.FirstOrDefault(
                    s => s.Name.Equals(_userSettings.LastStrategyName, StringComparison.Ordinal));
                if (restored != null)
                    _selectedStrategy = restored;
            }

            ScanCommand            = new RelayCommand(async _ => await RunWeeklyScanAsync(), _ => !IsBusy);
            RefreshDayPicksCommand      = new RelayCommand(async _ => await GenerateDayPicksAsync(),       _ => !IsBusy && _cachedHistory != null);
            ForceRefreshDayPicksCommand  = new RelayCommand(async _ => await GenerateDayPicksAsync(force: true), _ => !IsBusy && _cachedHistory != null);
            AskAIAboutPicksCommand       = new RelayCommand(_ => AskAIAboutPicks(),  _ => DayPicks.Count > 0);
            RefreshWatchPricesCommand    = new RelayCommand(async _ => await RefreshWatchPricesAsync(),    _ => !IsBusy && WatchList.Count > 0);

            AddDayPickToWatchCommand = new RelayCommand(p => AddDayPickToWatch(p),    _ => SelectedDayPick != null);
            AddDayPickToHeldCommand  = new RelayCommand(p => AddDayPickToHeld(p),    _ => SelectedDayPick != null);

            AddToWatchCommand              = new RelayCommand(_ => AddSelectedToWatch(),    _ => SelectedRecommendation != null);
            AddToHeldCommand               = new RelayCommand(_ => AddSelectedToHeld(),    _ => SelectedRecommendation != null);
            RemoveFromWatchCommand         = new RelayCommand(_ => RemoveSelectedWatch(),  _ => SelectedWatch           != null);
            RemoveFromHeldCommand          = new RelayCommand(_ => RemoveSelectedHeld(),   _ => SelectedHeld            != null);
            PromoteWatchToPositionCommand  = new RelayCommand(_ => PromoteWatchToPosition(), _ => SelectedWatch         != null);
            OpenInBrowserCommand           = new RelayCommand(p =>
            {
                if (p is string sym && !string.IsNullOrEmpty(sym))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        $"https://finance.yahoo.com/quote/{Uri.EscapeDataString(sym)}")
                    { UseShellExecute = true });
            });

            // Parameter: "claude" | "gemini" | "copilot"
            AskAICommand = new RelayCommand(
                p => AskAI(p as string ?? "claude"),
                _ => ActiveSelectedSymbol() != null);

            IncrementWeeklyCommand  = new RelayCommand(_ => TargetProfitMarginPercent  = Math.Round(TargetProfitMarginPercent  + 0.10m, 2));
            DecrementWeeklyCommand  = new RelayCommand(_ => TargetProfitMarginPercent  = Math.Round(Math.Max(0m, TargetProfitMarginPercent  - 0.10m), 2));
            IncrementMonthlyCommand = new RelayCommand(_ => TargetMonthlyProfitPercent = Math.Round(TargetMonthlyProfitPercent + 0.10m, 2));
            DecrementMonthlyCommand = new RelayCommand(_ => TargetMonthlyProfitPercent = Math.Round(Math.Max(0m, TargetMonthlyProfitPercent - 0.10m), 2));

            // Restore saved index (falls back to SP500 if absent from settings file).
            if (Enum.TryParse<IndexUniverse>(_userSettings.SelectedIndex, out var savedIndex))
                _selectedIndex = savedIndex;

            // Restore Daily Picks strategy and universe cap
            if (Enum.TryParse<DayPickStrategy>(_userSettings.DayPickStrategy, out var savedStrat))
                _selectedDayPickStrategy = savedStrat;
            _dayPickUniverseSize = _userSettings.DayPickUniverseSize;

            // Restore saved weekly target (falls back to field default of 2.0m if not in settings).
            _targetProfitMarginPercent = _userSettings.TargetProfitMarginPercent;

            // Initialise monthly from the restored (or default) weekly value.
            _syncingProfit = true;
            _targetMonthlyProfitPercent = Math.Round(
                (decimal)((Math.Pow((double)(1m + _targetProfitMarginPercent / 100m), 52.0 / 12.0) - 1.0) * 100.0), 2);
            _syncingProfit = false;

            // ── Column toggles ────────────────────────────────────────────────
            ColSource       = new ColumnToggle("Source",       true);
            ColPrice        = new ColumnToggle("Last Price",  true);
            ColDayChange    = new ColumnToggle("Change $",    true);
            ColDayChangePct = new ColumnToggle("Change %",    true);
            ColRSI          = new ColumnToggle("RSI14",       true);
            ColWeekReturn   = new ColumnToggle("Week Ret%",   true);
            ColConf         = new ColumnToggle("Confidence",  false);
            ColBuyDate      = new ColumnToggle("Buy Date",    true);
            ColSellDate     = new ColumnToggle("Sell Date",   true);
            ColVolume       = new ColumnToggle("Volume",      false);
            ColAvgVolume    = new ColumnToggle("Avg Volume",  false);
            ColMarketCap    = new ColumnToggle("Mkt Cap",     false);
            ColPE           = new ColumnToggle("P/E",         false);
            ColForwardPE    = new ColumnToggle("Fwd P/E",     false);
            ColEPS          = new ColumnToggle("EPS",         false);
            ColPriceToBook  = new ColumnToggle("P/B",         false);
            Col52WkHigh     = new ColumnToggle("52W High",    false);
            Col52WkLow      = new ColumnToggle("52W Low",     false);
            ColBeta         = new ColumnToggle("Beta",        false);
            ColDivYield     = new ColumnToggle("Div Yield%",  false);
            ColShortRatio   = new ColumnToggle("Short Ratio", false);
            ColIV           = new ColumnToggle("Impl. Vol%",  false);
            ColTheta        = new ColumnToggle("Theta",       false);
            ColSMA20        = new ColumnToggle("SMA20",       false);
            ColSMA50        = new ColumnToggle("SMA50",       false);
            ColVolTrend     = new ColumnToggle("Vol Trend",   false);
            ColReasoning    = new ColumnToggle("Reasoning",   true);

            AllColumns = new[]
            {
                ColSource,
                ColPrice, ColDayChange, ColDayChangePct,
                ColRSI, ColWeekReturn, ColConf,
                ColBuyDate, ColSellDate,
                ColVolume, ColAvgVolume, ColMarketCap,
                ColPE, ColForwardPE, ColEPS, ColPriceToBook,
                Col52WkHigh, Col52WkLow,
                ColBeta, ColDivYield, ColShortRatio,
                ColIV, ColTheta,
                ColSMA20, ColSMA50, ColVolTrend,
                ColReasoning,
            };

            // Apply any saved column visibility from disk, then watch for changes.
            // Filtered view for recommendations grid — default sort: Confidence DESC, then action rank ASC
            RecommendationsView = CollectionViewSource.GetDefaultView(Recommendations);
            RecommendationsView.Filter = RecommendationFilter;
            RecommendationsView.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(Recommendation.Confidence),      System.ComponentModel.ListSortDirection.Descending));
            RecommendationsView.SortDescriptions.Add(new System.ComponentModel.SortDescription(nameof(Recommendation.ActionSortOrder), System.ComponentModel.ListSortDirection.Ascending));

            ApplySavedColumnVisibility();
            foreach (var col in AllColumns)
                col.PropertyChanged += OnColumnToggleChanged;

            // ── Data source toggles ──────────────────────────────────────────────
            var yahoo  = new DataSourceToggle(DataSourceType.YahooFinance);
            var stooq  = new DataSourceToggle(DataSourceType.Stooq);
            var av     = new DataSourceToggle(DataSourceType.AlphaVantage);
            var fh     = new DataSourceToggle(DataSourceType.Finnhub);
            var poly   = new DataSourceToggle(DataSourceType.Polygon);
            var tiingo = new DataSourceToggle(DataSourceType.Tiingo);
            DataSources = new[] { yahoo, stooq, av, fh, poly, tiingo };

            // Restore enabled state and API keys from settings
            foreach (var ds in DataSources)
            {
                ds.IsEnabled = _userSettings.EnabledDataSources.Contains(ds.SourceType.ToString());
                if (_userSettings.ApiKeys.TryGetValue(ds.SourceType.ToString(), out var key))
                    ds.ApiKey = key;
                ds.PropertyChanged += OnDataSourceToggleChanged;
            }
            // Ensure Yahoo is always at least enabled by default on first run
            if (!DataSources.Any(d => d.IsEnabled))
                yahoo.IsEnabled = true;

            // ── Auto-refresh timer (ticks every minute, re-fetches based on RefreshIntervalMinutes)
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _refreshTimer.Tick += OnRefreshTimerTick;
            _refreshTimer.Start();

            // ── Market index bar — populate placeholders and start refresh timer ──
            foreach (var (sym, name) in _indexSymbols)
                MarketIndices.Add(new MarketIndex { Symbol = sym, Name = name });

            // Refresh market indices periodically.
            // During market hours: every 2 minutes (prices are moving).
            // Outside market hours: every 10 minutes (just keeping last-close current).
            // Always refresh — Yahoo returns the latest available data regardless of session.
            _marketTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(120) };
            _marketTimer.Tick += async (_, __) =>
            {
                _marketTimer.Interval = IsMarketHours()
                    ? TimeSpan.FromSeconds(120)
                    : TimeSpan.FromSeconds(600);
                await RefreshMarketIndicesAsync();
            };
            _marketTimer.Start();

            // Keep tab-header and empty-state properties in sync with collection changes.
            WatchList.CollectionChanged += (_, __) =>
            {
                OnPropertyChanged(nameof(WatchTabHeader));
                OnPropertyChanged(nameof(WatchListIsEmpty));
            };
            HeldList.CollectionChanged += (_, __) =>
            {
                OnPropertyChanged(nameof(PositionsTabHeader));
                OnPropertyChanged(nameof(HeldListIsEmpty));
            };

            RefreshPortfolio();
        }

        // ── Collections ───────────────────────────────────────────────────────

        public BulkObservableCollection<Recommendation>  Recommendations { get; } = new();
        public ICollectionView RecommendationsView { get; private set; } = null!;
        public BulkObservableCollection<DayPick>          DayPicks        { get; } = new();
        public ObservableCollection<MarketIndex>          MarketIndices   { get; } = new();
        public ObservableCollection<Recommendation>       WatchList       { get; } = new();
        public ObservableCollection<HeldPosition>         HeldList        { get; } = new();
        public ObservableCollection<TradingStrategy>      Strategies      { get; }

        // ── Tab header labels (computed strings — avoids WPF StringFormat quirk on object-typed Header) ─
        public string WatchTabHeader     => $"Watch ({WatchList.Count})";
        public string PositionsTabHeader => $"Positions ({HeldList.Count})";

        // ── Empty-state flags ─────────────────────────────────────────────────
        public bool WatchListIsEmpty => WatchList.Count == 0;
        public bool HeldListIsEmpty  => HeldList.Count  == 0;

        // ── Active selection ──────────────────────────────────────────────────
        public bool HasActiveSelection => _activeSelection != null;

        private string _dayPicksStatus = "Run a scan to generate intraday picks.";
        /// <summary>Status line shown in the Day Picks tab header area.</summary>

        // ── Daily Picks strategy & universe ─────────────────────────────────

        public static IReadOnlyList<DayPickStrategy> DayPickStrategyOptions { get; } =
            new[] { DayPickStrategy.Momentum, DayPickStrategy.MeanReversion,
                    DayPickStrategy.Breakout,  DayPickStrategy.EarningsPlay };

        private DayPickStrategy _selectedDayPickStrategy = DayPickStrategy.Momentum;
        public DayPickStrategy SelectedDayPickStrategy
        {
            get => _selectedDayPickStrategy;
            set
            {
                if (SetProperty(ref _selectedDayPickStrategy, value))
                {
                    _userSettings.DayPickStrategy = value.ToString();
                    _ = _userSettingsService.SaveAsync(_userSettings);
                    _ = GenerateDayPicksAsync(force: true);
                }
            }
        }

        public static IReadOnlyList<int> DayPickUniverseSizeOptions { get; } =
            new[] { 0, 50, 100, 250, 503 };

        private int _dayPickUniverseSize = 0;   // 0 = use all cached
        public int DayPickUniverseSize
        {
            get => _dayPickUniverseSize;
            set
            {
                if (SetProperty(ref _dayPickUniverseSize, value))
                {
                    _userSettings.DayPickUniverseSize = value;
                    _ = _userSettingsService.SaveAsync(_userSettings);
                    _ = GenerateDayPicksAsync(force: true);
                }
            }
        }

        public string DayPickUniverseSizeDisplay =>
            _dayPickUniverseSize == 0 ? "All" : _dayPickUniverseSize.ToString();

                public string DayPicksStatus
        {
            get => _dayPicksStatus;
            private set => SetProperty(ref _dayPicksStatus, value);
        }

        private string _marketIndexStatus = "Awaiting market data…";
        /// <summary>
        /// Short status string shown on the right end of the market index bar.
        /// Updates to "Updated HH:mm" once data arrives.
        /// </summary>
        public string MarketIndexStatus
        {
            get => _marketIndexStatus;
            private set => SetProperty(ref _marketIndexStatus, value);
        }

        // ── Data source toggles ───────────────────────────────────────────────

        public IReadOnlyList<DataSourceToggle> DataSources { get; }

        // ── Column toggles ────────────────────────────────────────────────────

        public ColumnToggle ColSource       { get; }
        public ColumnToggle ColPrice        { get; }
        public ColumnToggle ColDayChange    { get; }
        public ColumnToggle ColDayChangePct { get; }
        public ColumnToggle ColRSI          { get; }
        public ColumnToggle ColWeekReturn   { get; }
        public ColumnToggle ColConf         { get; }
        public ColumnToggle ColBuyDate      { get; }
        public ColumnToggle ColSellDate     { get; }
        public ColumnToggle ColVolume       { get; }
        public ColumnToggle ColAvgVolume    { get; }
        public ColumnToggle ColMarketCap    { get; }
        public ColumnToggle ColPE           { get; }
        public ColumnToggle ColForwardPE    { get; }
        public ColumnToggle ColEPS          { get; }
        public ColumnToggle ColPriceToBook  { get; }
        public ColumnToggle Col52WkHigh     { get; }
        public ColumnToggle Col52WkLow      { get; }
        public ColumnToggle ColBeta         { get; }
        public ColumnToggle ColDivYield     { get; }
        public ColumnToggle ColShortRatio   { get; }
        public ColumnToggle ColIV           { get; }
        public ColumnToggle ColTheta        { get; }
        public ColumnToggle ColSMA20        { get; }
        public ColumnToggle ColSMA50        { get; }
        public ColumnToggle ColVolTrend     { get; }
        public ColumnToggle ColReasoning    { get; }

        public IReadOnlyList<ColumnToggle> AllColumns { get; }

        // ── Selection ─────────────────────────────────────────────────────────

        private DayPick? _selectedDayPick;
        public DayPick? SelectedDayPick
        {
            get => _selectedDayPick;
            set
            {
                if (SetProperty(ref _selectedDayPick, value))
                {
                    ((RelayCommand)AddDayPickToWatchCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)AddDayPickToHeldCommand).RaiseCanExecuteChanged();
                    if (value != null) ActiveSelection = value;
                    _ = LoadChartAsync(value?.Symbol);
                    _ = LoadOptionsAsync(value?.Symbol);
                }
            }
        }

        private Recommendation? _selectedRecommendation;
        public Recommendation? SelectedRecommendation
        {
            get => _selectedRecommendation;
            set
            {
                if (SetProperty(ref _selectedRecommendation, value))
                {
                    ((RelayCommand)AddToWatchCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)AddToHeldCommand).RaiseCanExecuteChanged();
                    if (value != null) ActiveSelection = value;
                    _ = LoadChartAsync(value?.Symbol);
                    _ = LoadOptionsAsync(value?.Symbol);
                }
            }
        }

        private Recommendation? _selectedWatch;
        public Recommendation? SelectedWatch
        {
            get => _selectedWatch;
            set
            {
                if (SetProperty(ref _selectedWatch, value))
                {
                    ((RelayCommand)RemoveFromWatchCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)PromoteWatchToPositionCommand).RaiseCanExecuteChanged();
                    if (value != null) ActiveSelection = value;
                    _ = LoadChartAsync(value?.Symbol);
                    _ = LoadOptionsAsync(value?.Symbol);
                }
            }
        }

        private HeldPosition? _selectedHeld;
        public HeldPosition? SelectedHeld
        {
            get => _selectedHeld;
            set
            {
                if (SetProperty(ref _selectedHeld, value))
                {
                    ((RelayCommand)RemoveFromHeldCommand).RaiseCanExecuteChanged();
                    if (value != null) ActiveSelection = value;
                    _ = LoadChartAsync(value?.Symbol);
                    _ = LoadOptionsAsync(value?.Symbol);
                }
            }
        }

        private string _lastScanTime = "";
        /// <summary>Formatted time of the last completed scan. Empty until the first scan runs.</summary>
        public string LastScanTimeDisplay =>
            string.IsNullOrEmpty(_lastScanTime) ? "" : $"Last scan: {_lastScanTime}";

        private object? _activeSelection;
        public object? ActiveSelection
        {
            get => _activeSelection;
            set
            {
                if (SetProperty(ref _activeSelection, value))
                    OnPropertyChanged(nameof(HasActiveSelection));
            }
        }


        // ── Weekly chart ──────────────────────────────────────────────────────

        private IReadOnlyList<StockPicker.Models.WeeklyBar>? _weeklyBars;
        /// <summary>Weekly bars for the currently selected symbol. Bound to the chart control.</summary>
        public IReadOnlyList<StockPicker.Models.WeeklyBar>? WeeklyBars
        {
            get => _weeklyBars;
            private set => SetProperty(ref _weeklyBars, value);
        }

        private double? _detailsIV;
        /// <summary>Implied volatility fetched on-demand for the selected symbol.</summary>
        public double? DetailsIV
        {
            get => _detailsIV;
            private set
            {
                SetProperty(ref _detailsIV, value);
                OnPropertyChanged(nameof(DetailsIVDisplay));
            }
        }

        private double? _detailsTheta;
        /// <summary>Black-Scholes theta ($/day) for the near-term ATM option.</summary>
        public double? DetailsTheta
        {
            get => _detailsTheta;
            private set
            {
                SetProperty(ref _detailsTheta, value);
                OnPropertyChanged(nameof(DetailsThetaDisplay));
            }
        }

        public string DetailsIVDisplay    => _detailsIV.HasValue    ? $"{_detailsIV.Value * 100.0:F1}%"  : "—";
        public string DetailsThetaDisplay => _detailsTheta.HasValue ? $"{_detailsTheta.Value:F4}/day"    : "—";

        private bool _isChartLoading;
        public bool IsChartLoading
        {
            get => _isChartLoading;
            private set => SetProperty(ref _isChartLoading, value);
        }

        private TradingStrategy _selectedStrategy = new();
        public TradingStrategy SelectedStrategy
        {
            get => _selectedStrategy;
            set
            {
                if (!SetProperty(ref _selectedStrategy, value)) return;

                // Persist immediately so the choice survives a restart.
                _userSettings.LastStrategyName = value?.Name ?? string.Empty;
                _ = _userSettingsService.SaveAsync(_userSettings);

                if (_cachedUniverse != null && !IsBusy)
                    _ = ApplyStrategyAsync(isScan: false);
            }
        }

        // ── Settings ──────────────────────────────────────────────────────────

        // Guard against infinite setter recursion when weekly ↔ monthly sync each other.
        private bool _syncingProfit;

        private decimal _targetProfitMarginPercent = 2.0m;
        public decimal TargetProfitMarginPercent
        {
            get => _targetProfitMarginPercent;
            set
            {
                if (!SetProperty(ref _targetProfitMarginPercent, value)) return;

                // Persist the new value immediately.
                _userSettings.TargetProfitMarginPercent = value;
                _ = _userSettingsService.SaveAsync(_userSettings);

                // Sync monthly via compound formula: monthly = ((1 + weekly/100)^(52/12) - 1) × 100
                if (!_syncingProfit)
                {
                    _syncingProfit = true;
                    TargetMonthlyProfitPercent = Math.Round(
                        (decimal)((Math.Pow((double)(1m + value / 100m), 52.0 / 12.0) - 1.0) * 100.0), 2);
                    _syncingProfit = false;
                }

                if (_cachedUniverse != null && !IsBusy)
                    _ = ApplyStrategyAsync(isScan: false);
            }
        }

        private decimal _targetMonthlyProfitPercent;
        public decimal TargetMonthlyProfitPercent
        {
            get => _targetMonthlyProfitPercent;
            set
            {
                if (!SetProperty(ref _targetMonthlyProfitPercent, value)) return;

                // Sync weekly via inverse: weekly = ((1 + monthly/100)^(12/52) - 1) × 100
                if (!_syncingProfit)
                {
                    _syncingProfit = true;
                    TargetProfitMarginPercent = Math.Round(
                        (decimal)((Math.Pow((double)(1m + value / 100m), 12.0 / 52.0) - 1.0) * 100.0), 2);
                    _syncingProfit = false;
                }
            }
        }

        // ── Profit spinner commands ────────────────────────────────────────────

        public ICommand IncrementWeeklyCommand  { get; }
        public ICommand DecrementWeeklyCommand  { get; }
        public ICommand IncrementMonthlyCommand { get; }
        public ICommand DecrementMonthlyCommand { get; }

        // ── Index / universe selection ─────────────────────────────────────────

        /// <summary>All available index filters shown in the Settings window.</summary>
        public static IReadOnlyList<IndexUniverse> IndexOptions { get; } =
            new[] { IndexUniverse.Dow30, IndexUniverse.SP100, IndexUniverse.Nasdaq100, IndexUniverse.SP500 };

        private IndexUniverse _selectedIndex = IndexUniverse.SP500;
        public IndexUniverse SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                if (SetProperty(ref _selectedIndex, value))
                {
                    OnPropertyChanged(nameof(SelectedIndexDescription));
                    _userSettings.SelectedIndex = value.ToString();
                    _ = _userSettingsService.SaveAsync(_userSettings);
                }
            }
        }

        /// <summary>Short description of the currently selected index (bound to Settings note text).</summary>
        public string SelectedIndexDescription => _selectedIndex.Description();

        /// <summary>Universe size options surfaced in the Settings window.</summary>
        public static IReadOnlyList<int> UniverseSizeOptions { get; } = new[] { 50, 100, 250, 503 };

        private int _universeSize = 503;
        /// <summary>
        /// Optional cap on how many stocks from the selected index to scan.
        /// Clamped to the index's natural size at scan time.
        /// </summary>
        public int UniverseSize
        {
            get => _universeSize;
            set => SetProperty(ref _universeSize, value);
        }

        /// <summary>Auto-refresh interval options surfaced in the Settings window.</summary>
        public static IReadOnlyList<int> RefreshIntervalOptions { get; } = new[] { 5, 10, 15, 30 };

        private int _refreshIntervalMinutes = 15;
        public int RefreshIntervalMinutes
        {
            get => _refreshIntervalMinutes;
            set => SetProperty(ref _refreshIntervalMinutes, value);
        }

        // ── Column order (owned by ViewModel, applied/saved by the View) ──────

        /// <summary>
        /// Maps each column's Header string to its saved DisplayIndex.
        /// Read and written by <see cref="MainWindow"/> code-behind.
        /// Setting this property immediately persists the change to disk.
        /// </summary>
        public Dictionary<string, int> SavedColumnOrder
        {
            get => _userSettings.ColumnOrder;
            set
            {
                _userSettings.ColumnOrder = value;
                _ = _userSettingsService.SaveAsync(_userSettings);
            }
        }

        // ── Sort state (owned by ViewModel, applied/saved by the View) ───────

        /// <summary>
        /// SortMemberPath of the last active sort column in the recommendations grid.
        /// Empty string means no active sort. Persisted across sessions.
        /// </summary>
        public string SavedSortColumn
        {
            get => _userSettings.SortColumn;
            set
            {
                if (_userSettings.SortColumn == value) return;
                _userSettings.SortColumn = value;
                _ = _userSettingsService.SaveAsync(_userSettings);
            }
        }

        /// <summary>"Ascending" or "Descending".</summary>
        public string SavedSortDirection
        {
            get => _userSettings.SortDirection;
            set
            {
                if (_userSettings.SortDirection == value) return;
                _userSettings.SortDirection = value;
                _ = _userSettingsService.SaveAsync(_userSettings);
            }
        }

        // ── Collection filter ────────────────────────────────────────────────

        private bool RecommendationFilter(object obj)
        {
            if (obj is not Recommendation rec) return false;

            if (_buyOnlyFilter && rec.Action != RecommendationAction.Buy && rec.Action != RecommendationAction.StrongBuy) return false;

            var q = _searchText?.Trim();
            if (!string.IsNullOrEmpty(q))
            {
                if (!rec.Symbol.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !(rec.CompanyName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
                    return false;
            }

            return true;
        }

        // ── Layout ────────────────────────────────────────────────────────────

        private LayoutMode _layoutMode = LayoutMode.Full;
        public LayoutMode LayoutMode
        {
            get => _layoutMode;
            set => SetProperty(ref _layoutMode, value);
        }

        /// <summary>Whether the details panel is visible beside the main grid.</summary>
        private bool _showDetails = true;
        public bool ShowDetails
        {
            get => _showDetails;
            set => SetProperty(ref _showDetails, value);
        }

        // ── Filter / search ───────────────────────────────────────────────────

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                    RecommendationsView?.Refresh();
            }
        }

        private bool _buyOnlyFilter;
        public bool BuyOnlyFilter
        {
            get => _buyOnlyFilter;
            set
            {
                if (SetProperty(ref _buyOnlyFilter, value))
                    RecommendationsView?.Refresh();
            }
        }

        private bool _showColumnPicker;
        public bool ShowColumnPicker
        {
            get => _showColumnPicker;
            set => SetProperty(ref _showColumnPicker, value);
        }

        // ── Chart range ───────────────────────────────────────────────────────

        private bool _isChartYear = true;
        public bool IsChartYear
        {
            get => _isChartYear;
            set
            {
                if (SetProperty(ref _isChartYear, value) && value)
                    _ = LoadChartAsync();
            }
        }

        public bool IsChartWeek
        {
            get => !_isChartYear;
            set
            {
                if (value) IsChartYear = false;
            }
        }

        // ── Status ────────────────────────────────────────────────────────────

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    ((RelayCommand)ScanCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)RefreshDayPicksCommand).RaiseCanExecuteChanged();
                }
            }
        }

        private string _statusMessage = "Starting up…";
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        private DateTime? _lastFetchTime;
        public DateTime? LastFetchTime
        {
            get => _lastFetchTime;
            private set => SetProperty(ref _lastFetchTime, value);
        }

        private string _refreshStatus = "";
        public string RefreshStatus
        {
            get => _refreshStatus;
            private set => SetProperty(ref _refreshStatus, value);
        }

        // ── Commands ──────────────────────────────────────────────────────────

        public ICommand ScanCommand              { get; }
        public ICommand AddToWatchCommand        { get; }
        public ICommand AddToHeldCommand         { get; }
        public ICommand RemoveFromWatchCommand   { get; }
        public ICommand RemoveFromHeldCommand    { get; }
        public ICommand RefreshDayPicksCommand      { get; }
        public ICommand ForceRefreshDayPicksCommand  { get; }
        public ICommand AskAIAboutPicksCommand        { get; }
        public ICommand RefreshWatchPricesCommand     { get; }
        public ICommand AddDayPickToWatchCommand      { get; }
        public ICommand AddDayPickToHeldCommand       { get; }
        public ICommand PromoteWatchToPositionCommand { get; }
        public ICommand OpenInBrowserCommand          { get; }
        public ICommand AskAICommand                  { get; }

        // ── Auto-refresh ──────────────────────────────────────────────────────

        private async void OnRefreshTimerTick(object? sender, EventArgs e)
        {
            UpdateRefreshStatus();

            if (IsBusy) return;
            if (!IsMarketHours()) return;

            // Only re-fetch once the configured interval has elapsed since the last fetch.
            if (LastFetchTime.HasValue &&
                (DateTime.Now - LastFetchTime.Value).TotalMinutes < RefreshIntervalMinutes)
                return;

            await RunWeeklyScanAsync();
        }

        private void UpdateRefreshStatus()
        {
            if (!IsMarketHours())
            {
                var now = DateTime.Now;
                RefreshStatus = now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday
                    ? "Market closed (weekend)"
                    : "Market closed";
                return;
            }

            if (!LastFetchTime.HasValue)
            {
                RefreshStatus = "Awaiting first scan…";
                return;
            }

            var minutesSince = (DateTime.Now - LastFetchTime.Value).TotalMinutes;
            var minutesLeft  = RefreshIntervalMinutes - (int)minutesSince;
            RefreshStatus = minutesLeft <= 0
                ? "Refresh pending…"
                : $"Auto-refresh in {minutesLeft} min";
        }

        /// <summary>
        /// Returns true when US equity markets are open (9:30–16:00 ET, Mon–Fri).
        /// Falls back to a local-time estimate if the Eastern timezone cannot be resolved.
        /// </summary>
        private static bool IsMarketHours()
        {
            try
            {
                var eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                var etNow   = TimeZoneInfo.ConvertTime(DateTime.Now, eastern);
                if (etNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
                return etNow.TimeOfDay >= new TimeSpan(9, 30, 0)
                    && etNow.TimeOfDay <= new TimeSpan(16, 0, 0);
            }
            catch
            {
                var now = DateTime.Now;
                return now.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday)
                    && now.TimeOfDay >= new TimeSpan(9, 0, 0)
                    && now.TimeOfDay <= new TimeSpan(17, 0, 0);
            }
        }

        // ── User-settings helpers ─────────────────────────────────────────────

        /// <summary>
        /// Applies the persisted column visibility to each <see cref="ColumnToggle"/>.
        /// Called once from the constructor so columns are correct before first render.
        /// </summary>
        private void ApplySavedColumnVisibility()
        {
            foreach (var col in AllColumns)
            {
                if (_userSettings.ColumnVisibility.TryGetValue(col.Header, out var saved))
                    col.IsVisible = saved;
                // Columns absent from the dictionary keep their compiled default.
            }
        }

        /// <summary>
        /// Triggered whenever a column's IsVisible changes.
        /// Snapshots ALL column states into the settings object and saves asynchronously.
        /// </summary>
        private void OnColumnToggleChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ColumnToggle.IsVisible)) return;

            foreach (var col in AllColumns)
                _userSettings.ColumnVisibility[col.Header] = col.IsVisible;

            _ = _userSettingsService.SaveAsync(_userSettings);
        }

        /// <summary>
        /// Triggered whenever a DataSourceToggle's IsEnabled or ApiKey changes.
        /// Persists the new source configuration to disk.
        /// Yahoo Finance is always kept as a fallback if all other sources are disabled.
        /// </summary>
        private void OnDataSourceToggleChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            // Save enabled sources and API keys to settings
            _userSettings.EnabledDataSources = DataSources
                .Where(d => d.IsEnabled)
                .Select(d => d.SourceType.ToString())
                .ToList();

            if (!_userSettings.EnabledDataSources.Any())
            {
                // Always keep Yahoo as fallback
                DataSources.First(d => d.SourceType == DataSourceType.YahooFinance).IsEnabled = true;
                return;
            }

            _userSettings.ApiKeys = DataSources
                .Where(d => !string.IsNullOrEmpty(d.ApiKey))
                .ToDictionary(d => d.SourceType.ToString(), d => d.ApiKey);

            _ = _userSettingsService.SaveAsync(_userSettings);
        }

        // ── Startup ───────────────────────────────────────────────────────────

        /// <summary>
        /// Called once by <see cref="MainWindow"/> when the window finishes loading.
        ///
        /// Strategy:
        ///   1. Try to restore a previous scan from the disk cache.
        ///      If found, populate the in-memory cache and run Phase 2 immediately
        ///      so the user sees recommendations within ~1 second of launch.
        ///   2a. If no cache exists → run a full network fetch now.
        ///   2b. If cache is stale (older than RefreshIntervalMinutes) AND market is open
        ///       → kick off a background refresh (results replace the cached view seamlessly).
        ///   2c. If cache is fresh OR market is closed → leave the timer to handle it.
        /// </summary>
        public async Task StartupAsync()
        {
            // Populate the ticker from the last-saved cache so it shows instantly,
            // then kick off a live refresh in the background.
            ApplyCachedMarketIndices();
            _ = StartupIndexFetchAsync();

            var diskCache = await _scanCacheService.LoadAsync();

            if (diskCache != null)
            {
                // Restore in-memory state from the persisted snapshot.
                _cachedUniverse  = diskCache.Universe;
                _cachedWeekStart = diskCache.WeekStart;
                _cachedWeekEnd   = diskCache.WeekEnd;

                // Convert List<StockQuote> → IReadOnlyList<StockQuote>
                _cachedHistory = new Dictionary<string, IReadOnlyList<StockQuote>>(
                    diskCache.History.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var kv in diskCache.History)
                    _cachedHistory[kv.Key] = kv.Value;

                _cachedSummaries = diskCache.Summaries;

                _cachedNameLookup = _cachedUniverse.ToDictionary(
                    s => s.Symbol,
                    s => (s.Name, s.Sector),
                    StringComparer.OrdinalIgnoreCase);

                // Restore source provenance from the cache.
                _cachedSourcesBySymbol = diskCache.SourcesBySymbol.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value
                        .Select(s =>
                        {
                            if (Enum.TryParse<DataSourceType>(s, out var t)) return (DataSourceType?)t;
                            return null;
                        })
                        .Where(t => t.HasValue)
                        .Select(t => t!.Value)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);

                LastFetchTime = diskCache.FetchTime;

                var ageMinutes = (DateTime.Now - diskCache.FetchTime).TotalMinutes;
                StatusMessage = $"Loaded cached data from {diskCache.FetchTime:ddd MMM d, HH:mm} " +
                                $"({(int)ageMinutes} min ago). Applying analysis…";

                // Show results immediately — no network call needed.
                await ApplyStrategyAsync(isScan: false);

                // Always refresh on startup if the cache is older than 15 minutes,
                // regardless of market hours — data may be stale from a previous session.
                bool stale = ageMinutes >= 15;
                if (stale)
                {
                    StatusMessage = "Cache is over 15 minutes old — fetching fresh data…";
                    await RunWeeklyScanAsync();
                }
                else
                {
                    // Cache is fresh — generate day picks now (scan won't run to do it).
                    await GenerateDayPicksAsync();

                    var when = IsMarketHours() ? "will auto-refresh shortly" : "market is closed";
                    StatusMessage = $"Showing cached data from {diskCache.FetchTime:HH:mm} — {when}.";
                    UpdateRefreshStatus();
                }
            }
            else
            {
                // No cache on disk — do a full fetch now.
                StatusMessage = "No cached data found. Fetching live data…";
                await RunWeeklyScanAsync();
            }
        }

        // ── Phase 1: Fetch ────────────────────────────────────────────────────

        /// <summary>
        /// Returns the stock universe for the currently selected index.
        /// Hard-coded lists are used for Dow30, S&amp;P 100, and NASDAQ-100.
        /// S&amp;P 500 is fetched live from the Yahoo Finance service (network call).
        /// </summary>
        private async Task<IReadOnlyList<Stock>> GetUniverseForIndexAsync()
        {
            return _selectedIndex switch
            {
                IndexUniverse.Dow30     => BuiltInUniverses.Dow30,
                IndexUniverse.SP100     => BuiltInUniverses.SP100,
                IndexUniverse.Nasdaq100 => BuiltInUniverses.Nasdaq100,
                _                      => await _dataService.GetUniverseAsync(), // SP500 live fetch
            };
        }

        /// <summary>
        /// Builds an IStockDataService instance for the given toggle.
        /// Returns null if the toggle has no API key set (silently skipped).
        /// Yahoo Finance always returns the shared _dataService instance.
        /// </summary>
        private IStockDataService? BuildServiceForSource(DataSourceToggle ds)
        {
            return ds.SourceType switch
            {
                DataSourceType.YahooFinance => _dataService,
                DataSourceType.Stooq        => new StooqStockDataService(),
                DataSourceType.AlphaVantage => string.IsNullOrWhiteSpace(ds.ApiKey)
                    ? null : new AlphaVantageStockDataService(ds.ApiKey),
                DataSourceType.Finnhub      => string.IsNullOrWhiteSpace(ds.ApiKey)
                    ? null : new FinnhubStockDataService(ds.ApiKey),
                DataSourceType.Polygon      => string.IsNullOrWhiteSpace(ds.ApiKey)
                    ? null : new PolygonStockDataService(ds.ApiKey),
                DataSourceType.Tiingo       => string.IsNullOrWhiteSpace(ds.ApiKey)
                    ? null : new TiingoStockDataService(ds.ApiKey),
                _                           => null
            };
        }

        /// <summary>
        /// Merges per-source history dictionaries into a single symbol → bars map.
        /// For each date where multiple sources have data, OHLCV values are averaged
        /// (volume is summed then divided by source count for a fair average).
        /// As a side-effect, populates <see cref="_cachedSourcesBySymbol"/>.
        /// </summary>
        private Dictionary<string, IReadOnlyList<StockQuote>> MergeHistories(
            Dictionary<DataSourceType, Dictionary<string, IReadOnlyList<StockQuote>>> perSource)
        {
            // Collect every symbol seen across all sources.
            var allSymbols = perSource.Values
                .SelectMany(d => d.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var merged   = new Dictionary<string, IReadOnlyList<StockQuote>>(
                               allSymbols.Count, StringComparer.OrdinalIgnoreCase);
            var srcMap   = new Dictionary<string, List<DataSourceType>>(
                               allSymbols.Count, StringComparer.OrdinalIgnoreCase);

            foreach (var sym in allSymbols)
            {
                // Gather every bar from every source that has data for this symbol.
                var contributingSources = new List<DataSourceType>();
                var barsByDate = new Dictionary<DateTime, List<StockQuote>>();

                foreach (var (srcType, srcDict) in perSource)
                {
                    if (!srcDict.TryGetValue(sym, out var bars) || bars.Count == 0)
                        continue;

                    contributingSources.Add(srcType);
                    foreach (var bar in bars)
                    {
                        var day = bar.Timestamp.Date;
                        if (!barsByDate.TryGetValue(day, out var list))
                            barsByDate[day] = list = new List<StockQuote>(4);
                        list.Add(bar);
                    }
                }

                if (barsByDate.Count == 0)
                {
                    merged[sym] = Array.Empty<StockQuote>();
                    srcMap[sym] = contributingSources;
                    continue;
                }

                // Average multi-source bars on the same date.
                var mergedBars = new List<StockQuote>(barsByDate.Count);
                foreach (var (day, bars) in barsByDate.OrderBy(kv => kv.Key))
                {
                    int n = bars.Count;
                    mergedBars.Add(new StockQuote
                    {
                        Symbol    = sym,
                        Timestamp = day,
                        Open      = bars.Sum(b => b.Open)   / n,
                        High      = bars.Sum(b => b.High)   / n,
                        Low       = bars.Sum(b => b.Low)    / n,
                        Close     = bars.Sum(b => b.Close)  / n,
                        Volume    = bars.Sum(b => b.Volume) / n,
                    });
                }

                merged[sym] = mergedBars;
                srcMap[sym] = contributingSources;
            }

            _cachedSourcesBySymbol = srcMap;
            return merged;
        }

        private async Task RunWeeklyScanAsync()
        {
            IsBusy = true;
            try
            {
                var today = DateTime.Today;
                _cachedWeekStart = today.AddDays(-90);   // 90 days for SMA50/RSI14
                _cachedWeekEnd   = today;

                // ── Determine which services are active ────────────────────────
                var enabledToggles = DataSources.Where(d => d.IsEnabled).ToList();
                var services = enabledToggles
                    .Select(ds => BuildServiceForSource(ds))
                    .Where(svc => svc != null)
                    .Select(svc => svc!)
                    .ToList();

                if (services.Count == 0)
                {
                    // Fallback: always use Yahoo
                    services.Add(_dataService);
                }

                var sourceNames = string.Join(", ", services.Select(s => s.SourceType.ShortName()));
                StatusMessage = $"Connecting to {sourceNames}…";

                // ── 1a. Universe — from selected index ───────────────────────
                StatusMessage   = $"Loading {_selectedIndex.DisplayName()} universe…";
                var fullUniverse = await GetUniverseForIndexAsync();
                var cap          = Math.Min(UniverseSize, _selectedIndex.MaxSize());
                _cachedUniverse  = fullUniverse.Take(cap).ToList();
                int total        = _cachedUniverse.Count;

                _cachedNameLookup = _cachedUniverse.ToDictionary(
                    s => s.Symbol,
                    s => (s.Name, s.Sector),
                    StringComparer.OrdinalIgnoreCase);

                // ── 1b. Price history — each enabled source fetches in parallel ─
                _cachedHistoryPerSource = new Dictionary<DataSourceType, Dictionary<string, IReadOnlyList<StockQuote>>>();

                var historyFetchTasks = services.Select(async svc =>
                {
                    var bag     = new ConcurrentDictionary<string, IReadOnlyList<StockQuote>>(StringComparer.OrdinalIgnoreCase);
                    var sem     = new SemaphoreSlim(15);
                    int fetched = 0;

                    var histTasks = _cachedUniverse.Select(async stock =>
                    {
                        await sem.WaitAsync();
                        try
                        {
                            var h = await svc.GetHistoryAsync(stock.Symbol, _cachedWeekStart, _cachedWeekEnd);
                            bag[stock.Symbol] = h;
                            Interlocked.Increment(ref fetched);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[{svc.SourceType}] History fetch error for {stock.Symbol}: {ex.Message}");
                        }
                        finally { sem.Release(); }
                    });

                    await Task.WhenAll(histTasks);
                    return (svc.SourceType, Dict: new Dictionary<string, IReadOnlyList<StockQuote>>(bag));
                }).ToList();

                // Drive all source tasks; poll progress on the UI thread every 300 ms
                var allHistTask = Task.WhenAll(historyFetchTasks);
                while (!allHistTask.IsCompleted)
                {
                    await Task.Delay(300);
                    StatusMessage = $"Fetching price history from {sourceNames}…";
                }

                var sourceResults = await allHistTask;
                foreach (var (srcType, dict) in sourceResults)
                    _cachedHistoryPerSource[srcType] = dict;

                // ── Merge histories from all sources ──────────────────────────
                _cachedHistory = MergeHistories(_cachedHistoryPerSource);

                // ── 1c. Live quote summaries — Yahoo is primary ────────────────
                StatusMessage    = $"Fetching live market data for {total} stocks…";
                _cachedSummaries = await _dataService.GetQuoteSummariesAsync(
                                       _cachedUniverse.Select(s => s.Symbol));

                // Supplement with data from secondary sources for any symbols Yahoo missed.
                foreach (var svc in services.Where(s => s.SourceType != DataSourceType.YahooFinance))
                {
                    try
                    {
                        var missingSymbols = _cachedUniverse
                            .Select(s => s.Symbol)
                            .Where(sym => !_cachedSummaries.ContainsKey(sym))
                            .ToList();

                        if (missingSymbols.Count == 0) break;

                        StatusMessage = $"Supplementing quote data from {svc.SourceType.ShortName()} ({missingSymbols.Count} missing)…";
                        var supplemental = await svc.GetQuoteSummariesAsync(missingSymbols);
                        foreach (var kv in supplemental)
                            _cachedSummaries[kv.Key] = kv.Value;
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[{svc.SourceType}] Quote summary supplemental error: {ex.Message}");
                    }
                }

                LastFetchTime = DateTime.Now;

                // Persist the fresh data to disk so the next startup is instant.
                _ = PersistCacheAsync();

                // Phase 2: analyze + recommend using the freshly cached data
                await ApplyStrategyAsync(isScan: true);

                // Refresh market indices now that we have fresh network access.
                await RefreshMarketIndicesAsync();
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ── Cache persistence ─────────────────────────────────────────────────

        /// <summary>
        /// Serialises the current in-memory cache to disk in the background.
        /// Called fire-and-forget after every successful network scan.
        /// </summary>
        private async Task PersistCacheAsync()
        {
            if (_cachedUniverse == null || _cachedHistory == null || _cachedSummaries == null)
                return;

            // Convert IReadOnlyList back to List for JSON serialisation.
            var histDict = new Dictionary<string, List<StockQuote>>(_cachedHistory.Count);
            foreach (var kv in _cachedHistory)
                histDict[kv.Key] = kv.Value is List<StockQuote> list ? list : new List<StockQuote>(kv.Value);

            var cache = new ScanCache
            {
                FetchTime = LastFetchTime ?? DateTime.Now,
                WeekStart = _cachedWeekStart,
                WeekEnd   = _cachedWeekEnd,
                Universe  = _cachedUniverse.ToList(),
                History   = histDict,
                Summaries = _cachedSummaries,
                SourcesBySymbol = _cachedSourcesBySymbol.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Select(s => s.ToString()).ToList()),
            };

            await _scanCacheService.SaveAsync(cache);
        }

        // ── Phase 2: Apply ────────────────────────────────────────────────────

        /// <summary>
        /// Runs analysis and recommendation against cached price data.
        /// No HTTP calls and no IsBusy toggling — safe to call whenever the strategy,
        /// target profit, or other analysis parameters change, without flashing the grid.
        /// </summary>
        private async Task ApplyStrategyAsync(bool isScan)
        {
            if (_cachedUniverse == null || _cachedHistory == null || _cachedSummaries == null)
                return;

            var context = new ScanContext
            {
                Strategy                  = SelectedStrategy,
                TargetProfitMarginPercent = TargetProfitMarginPercent,
                WeekStart                 = _cachedWeekStart,
                WeekEnd                   = _cachedWeekEnd,
            };

            if (isScan)
                StatusMessage = $"Analyzing {_cachedUniverse.Count} stocks with '{SelectedStrategy.Name}'…";

            // Analysis is CPU-only; run on a thread-pool thread so the UI stays responsive.
            var analyses = await Task.Run(() =>
            {
                var list = new List<AnalysisResult>(_cachedUniverse.Count);
                foreach (var stock in _cachedUniverse)
                {
                    var history = _cachedHistory.TryGetValue(stock.Symbol, out var h)
                        ? h : Array.Empty<StockQuote>();
                    list.Add(_analysisService.AnalyzeAsync(stock, history, context).Result);
                }
                return list;
            });

            if (isScan)
                StatusMessage = "Generating recommendations…";


            var recs = (await _recommendationService.GenerateAsync(analyses, context)).ToList();

            // Enrich each recommendation with name, sector, live market data, and source tags.
            foreach (var rec in recs)
            {
                // Company name & sector — prefer QuoteSummary longName, fall back to universe map
                if (_cachedSummaries.TryGetValue(rec.Symbol, out var qs))
                {
                    rec.CompanyName = qs.LongName ?? qs.ShortName ?? rec.Symbol;
                    if (string.IsNullOrEmpty(rec.Sector))
                        rec.Sector = qs.Sector ?? "";

                    // Live market data
                    rec.LastPrice        = qs.Price;
                    rec.DayChange        = qs.DayChange;
                    rec.DayChangePct     = qs.DayChangePct;
                    rec.Volume           = qs.Volume;
                    rec.AvgVolume        = qs.AvgVolume;
                    rec.MarketCap        = qs.MarketCap;
                    rec.PERatio          = qs.PERatio;
                    rec.ForwardPE        = qs.ForwardPE;
                    rec.EPS              = qs.EPS;
                    rec.PriceToBook      = qs.PriceToBook;
                    rec.Week52High       = qs.Week52High;
                    rec.Week52Low        = qs.Week52Low;
                    rec.Beta             = qs.Beta;
                    rec.DividendYieldPct   = qs.DividendYieldPct;
                    rec.ShortRatio         = qs.ShortRatio;
                    rec.ImpliedVolatility  = qs.ImpliedVolatility;
                    rec.Theta              = qs.Theta;
                }
                else if (_cachedNameLookup != null &&
                         _cachedNameLookup.TryGetValue(rec.Symbol, out var info))
                {
                    if (string.IsNullOrEmpty(rec.CompanyName))
                        rec.CompanyName = info.Name;
                    if (string.IsNullOrEmpty(rec.Sector))
                        rec.Sector = info.Sector;
                }

                // Contributing data sources
                if (_cachedSourcesBySymbol.TryGetValue(rec.Symbol, out var sources))
                    rec.ContributingSources = new System.Collections.Generic.List<DataSourceType>(sources);
            }

            // Flash-free grid update
            Recommendations.ReplaceAll(recs);

            // Update live prices on watch and held items from the fresh summary cache.
            UpdatePortfolioPrices();

            if (isScan)
            {
                _lastScanTime = DateTime.Now.ToString("HH:mm");
                OnPropertyChanged(nameof(LastScanTimeDisplay));
                StatusMessage = $"Scan complete — {recs.Count} recommendations generated.";
                UpdateRefreshStatus();
                await GenerateDayPicksAsync();
            }
        }

        // ── Day picks ─────────────────────────────────────────────────────────

        private async Task GenerateDayPicksAsync(bool force = false)
        {
            if (_cachedUniverse == null || _cachedHistory == null || _cachedSummaries == null)
            {
                DayPicksStatus = "No cached data — run a scan first.";
                return;
            }

            // Determine which trading session these picks belong to
            var targetDay = StockPicker.Services.TradingCalendar.TargetTradingDay();
            var dayLabel  = StockPicker.Services.TradingCalendar.FormatTradingDay(targetDay);

            // Return cached picks unless the caller explicitly wants a fresh run.
            if (!force)
            {
                var cached = _portfolioService.GetCachedDayPicks(targetDay);
                if (cached != null)
                {
                    DayPicks.ReplaceAll(cached);
                    DayPicksStatus = $"{cached.Count} picks for {dayLabel}  (cached)";
                    return;
                }
            }

            DayPicksStatus = $"Generating picks for {dayLabel}…";

            try
            {
                var readonlyHistory =
                    new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<StockQuote>>(
                        _cachedHistory.ToDictionary(
                            kv => kv.Key,
                            kv => (IReadOnlyList<StockQuote>)kv.Value,
                            StringComparer.OrdinalIgnoreCase));

                // Cap the universe for Daily Picks if the user set a limit
                IReadOnlyList<Stock> pickUniverse = _dayPickUniverseSize > 0
                    ? _cachedUniverse.Take(_dayPickUniverseSize).ToList()
                    : _cachedUniverse;

                var picks = await _dayPickService.GenerateAsync(
                    pickUniverse,
                    readonlyHistory,
                    _cachedSummaries,
                    _cachedNameLookup,
                    _selectedDayPickStrategy);

                // Persist so repeated calls today return the same list
                _portfolioService.SaveDayPicksCache(targetDay, picks);

                DayPicks.ReplaceAll(picks);
                ((RelayCommand)AskAIAboutPicksCommand).RaiseCanExecuteChanged();
                DayPicksStatus = picks.Count > 0
                    ? $"{picks.Count} picks for {dayLabel}  [{_selectedDayPickStrategy}]"
                    : $"No picks found for {dayLabel}";
            }
            catch (Exception ex)
            {
                DayPicksStatus = $"Day picks error: {ex.Message}";
            }
        }

        // ── Market index bar ──────────────────────────────────────────────────

        /// <summary>
        /// Fetches index data immediately on startup; if the first call returns no prices
        /// (network not ready yet at launch) waits 10 s and retries once.
        /// Runs on the UI thread so <see cref="MarketIndices"/> can be updated safely.
        /// </summary>
        private async Task StartupIndexFetchAsync()
        {
            await RefreshMarketIndicesAsync();
            if (!MarketIndices.Any(m => m.Price.HasValue))
            {
                await Task.Delay(10_000);
                await RefreshMarketIndicesAsync();
            }
        }

        /// <summary>
        /// Applies the last-persisted index snapshots to <see cref="MarketIndices"/>
        /// so the ticker shows real values instantly on startup before the live fetch arrives.
        /// </summary>
        private void ApplyCachedMarketIndices()
        {
            var cached = _portfolioService.GetCachedMarketIndices();
            if (cached.Count == 0) return;

            var lookup = cached.ToDictionary(s => s.Symbol, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _indexSymbols.Length && i < MarketIndices.Count; i++)
            {
                if (!lookup.TryGetValue(_indexSymbols[i].Symbol, out var snap)) continue;
                var mi = MarketIndices[i];
                mi.Price        = snap.Price;
                mi.DayChange    = snap.DayChange;
                mi.DayChangePct = snap.DayChangePct;
                // INotifyPropertyChanged fires the bindings — no collection replace needed.
            }

            _marketIndexUpdatedAt = cached.Max(s => s.FetchedAt);
            MarketIndexStatus = $"Updated {_marketIndexUpdatedAt:HH:mm} (cached)";
        }

        private async Task RefreshMarketIndicesAsync()
        {
            try
            {
                var symbols = _indexSymbols.Select(x => x.Symbol);
                var quotes  = await _dataService.GetQuoteSummariesAsync(symbols);
                var now     = DateTime.Now;
                var snapshots = new List<MarketIndexSnapshot>();

                for (int i = 0; i < _indexSymbols.Length && i < MarketIndices.Count; i++)
                {
                    var mi = MarketIndices[i];
                    if (quotes.TryGetValue(_indexSymbols[i].Symbol, out var q))
                    {
                        // MarketIndex now implements INotifyPropertyChanged —
                        // mutating the properties fires the bindings directly.
                        mi.Price        = q.Price;
                        mi.DayChange    = q.DayChange;
                        mi.DayChangePct = q.DayChangePct;

                        snapshots.Add(new MarketIndexSnapshot
                        {
                            Symbol       = _indexSymbols[i].Symbol,
                            Price        = q.Price,
                            DayChange    = q.DayChange,
                            DayChangePct = q.DayChangePct,
                            FetchedAt    = now,
                        });
                    }
                }

                // Persist so the next startup shows these values immediately.
                if (snapshots.Count > 0)
                    _portfolioService.SaveMarketIndicesCache(snapshots);

                _marketIndexUpdatedAt = now;
                MarketIndexStatus = $"Updated {now:HH:mm}";
            }
            catch (Exception ex)
            {
                MarketIndexStatus = $"Index data unavailable ({ex.Message})";
            }
        }

        // ── Portfolio helpers ─────────────────────────────────────────────────

        /// <summary>
        /// Loads the watch list and held positions from the portfolio service into
        /// the observable collections, then injects current prices from the cache.
        /// Called once on startup and after every add/remove/promote.
        /// </summary>
        private void RefreshPortfolio()
        {
            WatchList.Clear();
            foreach (var rec in _portfolioService.GetWatchList())
                WatchList.Add(rec);

            HeldList.Clear();
            foreach (var pos in _portfolioService.GetHeld())
                HeldList.Add(pos);

            UpdatePortfolioPrices();
        }

        /// <summary>
        /// Fetches live quotes for every symbol on the Watch list directly from Yahoo,
        /// then updates prices and saves to portfolio. Used by the force-refresh button.
        /// </summary>
        /// <summary>
        /// Loads weekly (or 1-week) bar data for the currently selected symbol and
        /// pushes it into <see cref="WeeklyBars"/> so the chart control re-renders.
        /// Silently does nothing when no symbol is selected or the fetch fails.
        /// </summary>
        /// <summary>
        /// Fetches the near-term implied volatility and theta for the selected symbol
        /// and populates <see cref="DetailsIV"/> / <see cref="DetailsTheta"/>.
        /// Silently clears both values when no symbol is selected or the fetch fails.
        /// </summary>
        private async Task LoadOptionsAsync(string? symbol = null)
        {
            symbol ??= ActiveSelectedSymbol();
            if (string.IsNullOrWhiteSpace(symbol))
            {
                DetailsIV    = null;
                DetailsTheta = null;
                return;
            }

            try
            {
                var (iv, theta) = await _dataService.GetNearTermOptionsAsync(symbol);
                DetailsIV    = iv;
                DetailsTheta = theta;
            }
            catch
            {
                DetailsIV    = null;
                DetailsTheta = null;
            }
        }

        private async Task LoadChartAsync(string? symbol = null)
        {
            symbol ??= ActiveSelectedSymbol();
            if (string.IsNullOrWhiteSpace(symbol)) return;

            IsChartLoading = true;
            try
            {
                var range = _isChartYear ? ChartRange.Year : ChartRange.Week;
                var bars  = await _dataService.GetWeeklyBarsAsync(symbol, range);
                WeeklyBars = bars;
            }
            catch
            {
                WeeklyBars = null;
            }
            finally
            {
                IsChartLoading = false;
            }
        }

        private async Task RefreshWatchPricesAsync()
        {
            if (WatchList.Count == 0) return;

            IsBusy = true;
            StatusMessage = "Refreshing Watch list prices…";
            try
            {
                var symbols = WatchList.Select(r => r.Symbol).Distinct(StringComparer.OrdinalIgnoreCase);
                var quotes  = await _dataService.GetQuoteSummariesAsync(symbols);

                foreach (var rec in WatchList)
                {
                    if (!quotes.TryGetValue(rec.Symbol, out var q)) continue;
                    rec.LastPrice    = q.Price;
                    rec.DayChange    = q.DayChange;
                    rec.DayChangePct = q.DayChangePct;
                    rec.Volume       = q.Volume;
                }

                // Merge into the cached summaries so Details pane stays consistent.
                if (_cachedSummaries != null)
                    foreach (var kv in quotes)
                        _cachedSummaries[kv.Key] = kv.Value;

                StatusMessage = $"Watch prices refreshed at {DateTime.Now:HH:mm}.";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Watch refresh error: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Injects the most recent cached LastPrice into every WatchList and HeldList
        /// item so P&L and watch-change columns stay current without a network call.
        /// </summary>
        private void UpdatePortfolioPrices()
        {
            if (_cachedSummaries == null) return;

            foreach (var rec in WatchList)
                if (_cachedSummaries.TryGetValue(rec.Symbol, out var qs))
                    rec.LastPrice = qs.Price;

            foreach (var pos in HeldList)
                if (_cachedSummaries.TryGetValue(pos.Symbol, out var qs))
                    pos.LastPrice = qs.Price;
        }

        // ── Ask AI ───────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the symbol of whichever item is currently selected across all tabs,
        /// or null if nothing is selected.
        /// </summary>
        private string? ActiveSelectedSymbol() =>
            SelectedRecommendation?.Symbol
            ?? SelectedWatch?.Symbol
            ?? SelectedHeld?.Symbol
            ?? SelectedDayPick?.Symbol;

        /// <summary>
        /// Builds a rich analysis prompt for the selected stock, copies it to the clipboard,
        /// then opens the requested AI in the default browser.
        /// For Copilot the prompt is also injected via the ?q= URL parameter.
        /// </summary>
        /// <summary>
        /// Builds a batch prompt covering all current Daily Picks and opens the chosen AI.
        /// The full list is copied to the clipboard; Copilot also gets it in the URL.
        /// </summary>
        private void AskAIAboutPicks()
        {
            if (DayPicks.Count == 0) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Please review today's Daily Picks generated by my StockPicker app ({_selectedDayPickStrategy} strategy) and give me your assessment of each one.");
            sb.AppendLine();
            sb.AppendLine($"Trading session: {StockPicker.Services.TradingCalendar.FormatTradingDay(StockPicker.Services.TradingCalendar.TargetTradingDay())}");
            sb.AppendLine();
            sb.AppendLine("## Picks");

            int i = 1;
            foreach (var pick in DayPicks)
            {
                sb.AppendLine($"{i++}. **{pick.Symbol}** ({pick.CompanyName}) — {pick.DirectionDisplay}");
                sb.AppendLine($"   Price: ${pick.LastPrice:F2} | Score: {pick.IntraDayScore:F2} | RSI: {pick.RSI14:F0}");
                sb.AppendLine($"   Vol Ratio: {pick.VolumeRatio:F1}× | Gap: {pick.GapPct:+0.##;-0.##}% | ATR: {pick.AtrPct:F1}%");
                sb.AppendLine($"   Entry: ${pick.EntryPrice:F2} | Stop: ${pick.StopLoss:F2} | Target: ${pick.Target:F2} (R:R {pick.RiskRewardRatio:F1})");
                sb.AppendLine($"   Signals: {pick.TriggerReason}");
                sb.AppendLine();
            }

            sb.AppendLine("## Questions");
            sb.AppendLine("1. Which of these picks has the highest conviction setup and why?");
            sb.AppendLine("2. Are there any picks you would avoid? What are the risks?");
            sb.AppendLine("3. Do you see any sector concentration or correlated risks across the list?");
            sb.AppendLine("4. Given current market conditions, does the chosen strategy (") ;
            sb.AppendLine($"   {_selectedDayPickStrategy}) seem appropriate?");
            sb.AppendLine("5. Any adjustments to stop-loss or target levels you would suggest?");

            var prompt = sb.ToString().Trim();
            System.Windows.Clipboard.SetText(prompt);

            var url = $"https://claude.ai/new";
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

            StatusMessage = $"Daily Picks batch prompt ({DayPicks.Count} stocks) copied to clipboard — paste into Claude!";
        }

        private void AskAI(string ai)
        {
            // Resolve the best available data for the selected stock.
            var sym      = ActiveSelectedSymbol();
            if (sym == null) return;

            var rec      = SelectedRecommendation
                           ?? SelectedWatch
                           ?? (SelectedHeld != null ? new Recommendation
                               {
                                   Symbol      = SelectedHeld.Symbol,
                                   CompanyName = SelectedHeld.CompanyName,
                               } : null)
                           ?? (SelectedDayPick != null ? new Recommendation
                               {
                                   Symbol      = SelectedDayPick.Symbol,
                                   CompanyName = SelectedDayPick.CompanyName,
                               } : null);

            QuoteSummary? qs = null;
            _cachedSummaries?.TryGetValue(sym, out qs);

            // Build the prompt.
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Please analyze {sym}{(string.IsNullOrWhiteSpace(rec?.CompanyName) ? "" : $" ({rec!.CompanyName})")} as a potential trade.");
            sb.AppendLine();
            sb.AppendLine("## Current Data");

            if (qs != null)
            {
                if (qs.Price.HasValue)        sb.AppendLine($"- Price: ${qs.Price:N2}");
                if (qs.DayChange.HasValue)    sb.AppendLine($"- Day Change: {(qs.DayChange >= 0 ? "+" : "")}{qs.DayChange:N2} ({(qs.DayChangePct >= 0 ? "+" : "")}{qs.DayChangePct:F2}%)");
                if (qs.Week52High.HasValue)   sb.AppendLine($"- 52-Week Range: ${qs.Week52Low:N2} – ${qs.Week52High:N2}");
                if (qs.PERatio.HasValue)      sb.AppendLine($"- P/E Ratio: {qs.PERatio:F1}");
                if (qs.EPS.HasValue)          sb.AppendLine($"- EPS (TTM): ${qs.EPS:F2}");
                if (qs.MarketCap.HasValue)    sb.AppendLine($"- Market Cap: ${qs.MarketCap / 1_000_000_000.0:F1}B");
                if (qs.Beta.HasValue)         sb.AppendLine($"- Beta: {qs.Beta:F2}");
                if (qs.Volume.HasValue)       sb.AppendLine($"- Volume: {qs.Volume:N0}");
                if (qs.AvgVolume.HasValue)    sb.AppendLine($"- Avg Volume: {qs.AvgVolume:N0}");
                if (qs.DividendYieldPct.HasValue) sb.AppendLine($"- Dividend Yield: {qs.DividendYieldPct:F2}%");
            }

            if (rec != null)
            {
                sb.AppendLine();
                sb.AppendLine("## Algorithmic Signal");
                sb.AppendLine($"- Action: {rec.Action}");
                if (rec.Confidence > 0)        sb.AppendLine($"- Confidence: {rec.Confidence:P0}");
                if (!string.IsNullOrEmpty(rec.Sector)) sb.AppendLine($"- Sector: {rec.Sector}");
                if (rec.RSI14.HasValue)        sb.AppendLine($"- RSI (14): {rec.RSI14:F1}");
                if (rec.SMA20.HasValue)        sb.AppendLine($"- SMA 20: ${rec.SMA20:N2}");
                if (rec.SMA50.HasValue)        sb.AppendLine($"- SMA 50: ${rec.SMA50:N2}");
                if (rec.VolumeTrend.HasValue)  sb.AppendLine($"- Volume Trend: {rec.VolumeTrend:F2}×");
                if (!string.IsNullOrEmpty(rec.Reasoning)) sb.AppendLine($"- Reasoning: {rec.Reasoning}");
            }

            if (DetailsIV.HasValue || DetailsTheta.HasValue)
            {
                sb.AppendLine();
                sb.AppendLine("## Options Data");
                if (DetailsIV.HasValue)    sb.AppendLine($"- Implied Volatility: {DetailsIV.Value * 100:F1}%");
                if (DetailsTheta.HasValue) sb.AppendLine($"- Theta: {DetailsTheta.Value:F4}/day");
            }

            sb.AppendLine();
            sb.AppendLine("## Questions");
            sb.AppendLine("1. Do you agree with the algorithmic signal? What is your overall assessment?");
            sb.AppendLine("2. What are the key risks for this trade?");
            sb.AppendLine("3. Are there any upcoming catalysts (earnings, news, macro events) to be aware of?");
            sb.AppendLine("4. What entry, stop-loss, and target levels would you suggest?");
            sb.AppendLine("5. How does this fit into a diversified portfolio?");

            var prompt = sb.ToString().Trim();

            // Copy to clipboard so the user can paste into any AI.
            System.Windows.Clipboard.SetText(prompt);

            // Open the chosen AI and (where supported) pre-fill the prompt.
            string url = ai.ToLowerInvariant() switch
            {
                "gemini"  => "https://gemini.google.com/app",
                "copilot" => $"https://copilot.microsoft.com/?q={Uri.EscapeDataString(prompt)}",
                _         => "https://claude.ai/new",   // claude (default)
            };

            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

            // Show a status message — Copilot auto-fills, others need a paste.
            StatusMessage = ai.ToLowerInvariant() == "copilot"
                ? $"Opened Copilot with {sym} analysis prompt."
                : $"Prompt for {sym} copied to clipboard — paste it into {char.ToUpper(ai[0]) + ai[1..]}!";
        }

        // ── Day-Pick portfolio actions ────────────────────────────────────────

        /// <summary>
        /// Resolves the command parameter from a Daily Picks DataGrid multi-select
        /// into a flat list of <see cref="DayPick"/> items.
        /// Falls back to the single <see cref="SelectedDayPick"/> when no parameter is supplied.
        /// </summary>
        private List<DayPick> ResolveSelectedPicks(object? parameter)
        {
            if (parameter is System.Collections.IList list)
            {
                var picks = new List<DayPick>(list.Count);
                foreach (var item in list)
                    if (item is DayPick dp) picks.Add(dp);
                if (picks.Count > 0) return picks;
            }
            return SelectedDayPick != null
                ? new List<DayPick> { SelectedDayPick }
                : new List<DayPick>();
        }

        /// <summary>Add all selected Daily Picks to the watch list.</summary>
        private void AddDayPickToWatch(object? parameter)
        {
            var picks = ResolveSelectedPicks(parameter);
            if (picks.Count == 0) return;

            foreach (var pick in picks)
            {
                var rec = new Recommendation
                {
                    Symbol       = pick.Symbol,
                    CompanyName  = pick.CompanyName,
                    Sector       = pick.Sector,
                    SourceTag    = "DayPick",
                    LastPrice    = pick.LastPrice ?? pick.EntryPrice,
                    WatchedPrice = pick.LastPrice ?? pick.EntryPrice,
                    WatchedAt    = DateTime.Now,
                    Action       = pick.Direction == DayPickDirection.Long ? RecommendationAction.Buy : RecommendationAction.Sell,
                    RSI14        = pick.RSI14,
                };
                _portfolioService.AddToWatch(rec);
            }

            RefreshPortfolio();
            StatusMessage = picks.Count == 1
                ? $"{picks[0].Symbol} added to Watch."
                : $"{picks.Count} picks added to Watch.";
        }

        /// <summary>Add all selected Daily Picks to held positions.</summary>
        private void AddDayPickToHeld(object? parameter)
        {
            var picks = ResolveSelectedPicks(parameter);
            if (picks.Count == 0) return;

            foreach (var pick in picks)
            {
                var rec = new Recommendation
                {
                    Symbol      = pick.Symbol,
                    CompanyName = pick.CompanyName,
                    Sector      = pick.Sector,
                    SourceTag   = "DayPick",
                    LastPrice   = pick.LastPrice ?? pick.EntryPrice,
                    Action      = pick.Direction == DayPickDirection.Long ? RecommendationAction.Buy : RecommendationAction.Sell,
                    RSI14       = pick.RSI14,
                };
                _portfolioService.AddToHeld(rec);
            }

            RefreshPortfolio();
            StatusMessage = picks.Count == 1
                ? $"{picks[0].Symbol} added to Positions."
                : $"{picks.Count} picks added to Positions.";
        }

        /// <summary>Add the selected recommendation to the watch list, tagged with the active strategy.</summary>
        private void AddSelectedToWatch()
        {
            if (SelectedRecommendation == null) return;
            var rec = SelectedRecommendation;
            rec.SourceTag    = SelectedStrategy?.Name ?? "Recommendation";
            rec.WatchedPrice = rec.LastPrice;
            rec.WatchedAt    = DateTime.Now;
            _portfolioService.AddToWatch(rec);
            RefreshPortfolio();
            StatusMessage = $"{rec.Symbol} added to Watch ({rec.SourceTag}).";
        }

        /// <summary>Mark the selected recommendation as a held position, tagged with the active strategy.</summary>
        private void AddSelectedToHeld()
        {
            if (SelectedRecommendation == null) return;
            var rec = SelectedRecommendation;
            rec.SourceTag = SelectedStrategy?.Name ?? "Recommendation";
            _portfolioService.AddToHeld(rec);
            RefreshPortfolio();
            StatusMessage = $"{rec.Symbol} added to Positions ({rec.SourceTag}).";
        }

        /// <summary>Remove the single currently-selected watch item.</summary>
        private void RemoveSelectedWatch()
        {
            if (SelectedWatch == null) return;
            _portfolioService.RemoveFromWatch(SelectedWatch.Symbol);
            RefreshPortfolio();
        }

        /// <summary>Remove one or more watch items by symbol. Used by multi-select remove.</summary>
        public void RemoveMultipleFromWatch(IEnumerable<string> symbols)
        {
            foreach (var sym in symbols)
                _portfolioService.RemoveFromWatch(sym);
            RefreshPortfolio();
        }

        /// <summary>Remove the single currently-selected held position.</summary>
        private void RemoveSelectedHeld()
        {
            if (SelectedHeld == null) return;
            _portfolioService.RemoveFromHeld(SelectedHeld.Symbol);
            RefreshPortfolio();
        }

        /// <summary>Promote the selected watch item to an open position.</summary>
        private void PromoteWatchToPosition()
        {
            if (SelectedWatch == null) return;
            _portfolioService.AddToHeld(SelectedWatch);
            _portfolioService.RemoveFromWatch(SelectedWatch.Symbol);
            RefreshPortfolio();
            StatusMessage = $"{SelectedWatch?.Symbol ?? "Stock"} moved to Positions.";
        }
    }
}