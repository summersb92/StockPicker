using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
// WPF-ADAPTATION: replaces System.Windows.Threading (DispatcherTimer) and
// System.Windows.Data (CollectionViewSource/ICollectionView). Avalonia's
// DispatcherTimer lives in Avalonia.Threading; DataGridCollectionView and
// DataGridSortDescription live in Avalonia.Collections (Avalonia.Controls.DataGrid pkg).
using Avalonia.Threading;
using Avalonia.Collections;
using StockPicker.Models;
using StockPicker.Services;

namespace StockPicker.Desktop.ViewModels
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
        private readonly IEarningsScanService   _earningsScanService;
        private readonly ContextExportService   _contextExportService = new();
        private UserSettings                    _userSettings;

        // ── Market index refresh timer ────────────────────────────────────────
        private readonly DispatcherTimer _marketTimer;
        private DateTime? _marketIndexUpdatedAt;

        /// <summary>
        /// Index symbols and their display names.
        /// These are fetched via Yahoo because Alpaca's stock endpoints do not
        /// serve caret-prefixed index symbols like ^GSPC.
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
        private DataSourceType? _cachedPrimaryQuoteSource;

        // ── Finnhub key rejection memo ────────────────────────────────────────
        // The exact key text Finnhub last answered 401/403 for. While a key is on this
        // list the background fundamentals pass is skipped outright, so a dead key costs
        // one request for the whole session instead of one per scan. Cleared when the user
        // edits the key or a Settings "Test" proves it works. Session-only by design —
        // a key revoked today may be reinstated tomorrow, and a restart re-probes.
        private string? _finnhubRejectedKey;

        // ── Scan generation counter ───────────────────────────────────────────
        // Incremented at the start of every ApplyStrategyAsync call.
        // The Finnhub two-pass background task captures the value at launch and
        // bails early if it has changed (i.e. a newer scan has superseded it).
        private long _scanGeneration;

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
                   new DayPickService(),
                   new EarningsScanService()) { }

        public MainViewModel(
            IStockDataService      dataService,
            IAnalysisService       analysisService,
            IRecommendationService recommendationService,
            IStrategyProvider      strategyProvider,
            IPortfolioService      portfolioService,
            ScanCacheService       scanCacheService,
            UserSettingsService    userSettingsService,
            IDayPickService        dayPickService,
            IEarningsScanService   earningsScanService)
        {
            _dataService           = dataService;
            _analysisService       = analysisService;
            _recommendationService = recommendationService;
            _strategyProvider      = strategyProvider;
            _portfolioService      = portfolioService;
            _scanCacheService      = scanCacheService;
            _userSettingsService   = userSettingsService;
            _dayPickService        = dayPickService;
            _earningsScanService   = earningsScanService;

            // Surface portfolio persistence problems instead of losing them silently.
            _portfolioService.PersistenceError += msg => StatusMessage = msg;
            if (_portfolioService.StartupLoadError is string loadError)
                StatusMessage = loadError;

            // Surface context-export failures the same way (see ContextExportService).
            _contextExportService.ExportError += msg => StatusMessage = msg;

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
            AskAIAboutPicksCommand       = new RelayCommand(async _ => await AskAIAboutPicks(),  _ => DayPicks.Count > 0);
            RefreshWatchPricesCommand    = new RelayCommand(async _ => await RefreshWatchPricesAsync(),    _ => !IsBusy && WatchList.Count > 0);

            AddDayPickToWatchCommand = new RelayCommand(p => AddDayPickToWatch(p),    _ => SelectedDayPick != null);
            AddDayPickToHeldCommand  = new RelayCommand(p => AddDayPickToHeld(p),    _ => SelectedDayPick != null);

            ForceRefreshEarningsCommand = new RelayCommand(async _ => await GenerateEarningsPicksAsync(), _ => !IsBusy && _cachedHistory != null);
            AddEarningsToWatchCommand   = new RelayCommand(p => AddEarningsToWatch(p), _ => SelectedEarningsPick != null);
            AddEarningsToHeldCommand    = new RelayCommand(p => AddEarningsToHeld(p),  _ => SelectedEarningsPick != null);

            RegenerateNewsCommand = new RelayCommand(async _ => await GenerateNewsReportAsync());
            CopyNewsCommand       = new RelayCommand(async _ => await CopyNewsReport(), _ => !string.IsNullOrWhiteSpace(NewsReport));
            AskAINewsCommand      = new RelayCommand(async _ => await AskAIAboutNews(),  _ => Recommendations.Count > 0);
            SelectNewsSymbolCommand     = new RelayCommand(p => SelectNewsSymbol(p as string));
            AddNewsSymbolToWatchCommand = new RelayCommand(p => AddNewsSymbolToWatch(p as string));

            AddToWatchCommand              = new RelayCommand(_ => AddSelectedToWatch(),    _ => SelectedRecommendation != null);
            AddToHeldCommand               = new RelayCommand(_ => AddSelectedToHeld(),    _ => SelectedRecommendation != null);
            ClearFiltersCommand            = new RelayCommand(_ =>
            {
                SearchText            = "";
                BuyOnlyFilter         = false;
                CashHeavyLowDebtOnly  = false;
                SelectedActionFilter  = AllActionsOption;
                SelectedSectorFilter  = AllSectorsOption;
            }, _ => IsFilterActive);
            ClearDayPickFiltersCommand     = new RelayCommand(_ => ClearDayPickFilters(), _ => IsDayPickFilterActive);
            RemoveFromWatchCommand         = new RelayCommand(_ => RemoveSelectedWatch(),  _ => SelectedWatch           != null);
            RemoveFromHeldCommand          = new RelayCommand(_ => RemoveSelectedHeld(),   _ => SelectedHeld            != null);
            PromoteWatchToPositionCommand  = new RelayCommand(_ => PromoteWatchToPosition(), _ => SelectedWatch         != null);
            RefreshPerformanceCommand      = new RelayCommand(async _ => await RefreshPerformanceAsync(), _ => HeldList.Count > 0 && !IsPerformanceLoading);
            OpenInBrowserCommand           = new RelayCommand(p =>
            {
                if (p is string sym && !string.IsNullOrEmpty(sym))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                        $"https://finance.yahoo.com/quote/{Uri.EscapeDataString(sym)}")
                    { UseShellExecute = true });
            });

            // Parameter: "claude" | "gemini" | "copilot"
            AskAICommand = new RelayCommand(
                async p => await AskAI(p as string ?? "claude"),
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

            // Restore Earnings scanner settings
            _earningsWindowDays     = _userSettings.EarningsWindowDays;
            _earningsTargetUpPercent = _userSettings.EarningsTargetUpPercent;
            _earningsLookbackDays   = Math.Clamp(_userSettings.EarningsLookbackDays, 1, 30);
            // Unknown/legacy values fall back to Upcoming rather than throwing.
            _earningsMode = Enum.TryParse<EarningsScanMode>(_userSettings.EarningsMode, out var savedMode)
                ? savedMode : EarningsScanMode.Upcoming;
            _earningsUseMargin      = _userSettings.EarningsUseMargin;
            _earningsMarginPercent  = _userSettings.EarningsMarginPercent;
            _earningsMarginRatePct  = _userSettings.EarningsMarginRatePct;

            // Restore News briefing composition
            _newsIncludePositions   = _userSettings.NewsIncludePositions;
            _newsIncludeBestAny     = _userSettings.NewsIncludeBestAny;
            _newsIncludePerStrategy = _userSettings.NewsIncludePerStrategy;
            _newsIncludeEarnings    = _userSettings.NewsIncludeEarnings;
            _newsIncludeTopPicks    = _userSettings.NewsIncludeTopPicks;
            _newsAnalysisPreset     = string.IsNullOrEmpty(_userSettings.NewsAnalysisPreset)
                                          ? "Full" : _userSettings.NewsAnalysisPreset;

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
            ColReasoning       = new ColumnToggle("Reasoning",   true);
            ColCashToMktCap    = new ColumnToggle("Cash/MktCap",  false);
            ColDebtToEquity    = new ColumnToggle("D/E",          false);
            ColNetDebtToEquity = new ColumnToggle("NetDebt/Eq",   false);
            ColRoe             = new ColumnToggle("ROE",          false);
            ColCashHeavyLowDebt = new ColumnToggle("Cash+LowDebt", false);
            ColTargetMean       = new ColumnToggle("1Y Target",    false);
            ColTargetDelta      = new ColumnToggle("Target Δ%",    false);

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
                ColCashToMktCap, ColDebtToEquity, ColNetDebtToEquity, ColRoe,
                ColCashHeavyLowDebt, ColTargetMean, ColTargetDelta,
                ColReasoning,
            };

            // Apply any saved column visibility from disk, then watch for changes.
            // Filtered view for recommendations grid — default sort: Confidence DESC, then action rank ASC
            // WPF-ADAPTATION: WPF used CollectionViewSource.GetDefaultView(...) which returns an
            // ICollectionView. Avalonia has neither type; DataGridCollectionView is the Avalonia
            // equivalent and is constructed directly over the source collection.
            //   • WPF .Filter (Predicate<object>)         → DataGridCollectionView .Filter (Func<object,bool>)
            //   • WPF SortDescriptions.Add(SortDescription) → SortDescriptions.Add(DataGridSortDescription.FromPath(...))
            //   • WPF .Refresh() and enumeration (.Cast<object>()) map 1:1 (used below in SearchText/FilteredCount).
            RecommendationsView = new DataGridCollectionView(Recommendations);
            RecommendationsView.Filter = RecommendationFilter;
            RecommendationsView.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(Recommendation.Confidence),      System.ComponentModel.ListSortDirection.Descending));
            RecommendationsView.SortDescriptions.Add(DataGridSortDescription.FromPath(nameof(Recommendation.ActionSortOrder), System.ComponentModel.ListSortDirection.Ascending));

            // Filtered view for the Daily Picks grids — same pattern as RecommendationsView.
            // No default sort descriptions: the pick service already emits rows in rank order.
            DayPicksView = new DataGridCollectionView(DayPicks);
            DayPicksView.Filter = DayPickFilter;

            ApplySavedColumnVisibility();
            foreach (var col in AllColumns)
                col.PropertyChanged += OnColumnToggleChanged;

            // ── Data source toggles ──────────────────────────────────────────────
            var yahoo  = new DataSourceToggle(DataSourceType.YahooFinance);
            var stooq  = new DataSourceToggle(DataSourceType.Stooq);
            var alpaca = new DataSourceToggle(DataSourceType.Alpaca);
            var av     = new DataSourceToggle(DataSourceType.AlphaVantage);
            var fh     = new DataSourceToggle(DataSourceType.Finnhub);
            var poly   = new DataSourceToggle(DataSourceType.Polygon);
            var tiingo = new DataSourceToggle(DataSourceType.Tiingo);
            DataSources = new[] { yahoo, stooq, alpaca, av, fh, poly, tiingo };

            // Restore enabled state and API keys from settings
            foreach (var ds in DataSources)
            {
                ds.IsEnabled = _userSettings.EnabledDataSources.Contains(ds.SourceType.ToString());
                if (_userSettings.ApiKeys.TryGetValue(ds.SourceType.ToString(), out var key))
                    ds.ApiKey = key;
                ds.KeyValidator = ValidateApiKeyAsync;
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
                // WPF-ADAPTATION: RefreshWatchPricesCommand.CanExecute reads WatchList.Count > 0;
                // WPF auto-requeried it, Avalonia needs the explicit raise.
                ((RelayCommand)RefreshWatchPricesCommand).RaiseCanExecuteChanged();
            };
            HeldList.CollectionChanged += (_, __) =>
            {
                OnPropertyChanged(nameof(PositionsTabHeader));
                OnPropertyChanged(nameof(HeldListIsEmpty));
                OnPropertyChanged(nameof(PortfolioIsEmpty));
                // WPF-ADAPTATION: RefreshPerformanceCommand.CanExecute reads HeldList.Count > 0;
                // WPF auto-requeried it, Avalonia needs the explicit raise.
                ((RelayCommand)RefreshPerformanceCommand).RaiseCanExecuteChanged();
            };

            _cashBalance = _portfolioService.GetCash();
            RefreshPortfolio();
        }

        // ── Collections ───────────────────────────────────────────────────────

        public BulkObservableCollection<Recommendation>  Recommendations { get; } = new();
        // WPF-ADAPTATION: was ICollectionView (System.Windows.Data). DataGridCollectionView
        // (Avalonia.Collections) is the Avalonia equivalent — exposes Filter, Refresh(),
        // SortDescriptions, and IEnumerable, all of which this VM uses.
        public DataGridCollectionView RecommendationsView { get; private set; } = null!;
        public BulkObservableCollection<DayPick>          DayPicks        { get; } = new();
        public BulkObservableCollection<EarningsPick>     EarningsPicks   { get; } = new();
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
        /// <summary>True only when there are no positions AND no cash — the true "empty portfolio".</summary>
        public bool PortfolioIsEmpty => HeldList.Count == 0 && CashBalance <= 0m;

        // ── Cash ──────────────────────────────────────────────────────────────

        private decimal _cashBalance;
        /// <summary>
        /// Un-invested cash on hand. Read-only here — it changes only through logged
        /// transactions (deposits, withdrawals, and sale proceeds) so the ledger stays accurate.
        /// </summary>
        public decimal CashBalance => _cashBalance;

        /// <summary>Formatted cash balance for the read-only display.</summary>
        public string CashDisplay => $"${_cashBalance:N2}";

        /// <summary>Re-reads the cash balance from the store after a logged cash change.</summary>
        private void SyncCashFromService()
        {
            _cashBalance = _portfolioService.GetCash();
            OnPropertyChanged(nameof(CashBalance));
            OnPropertyChanged(nameof(CashDisplay));
            OnPropertyChanged(nameof(PortfolioIsEmpty));
        }

        /// <summary>Snapshot of the full transaction ledger (for the history window).</summary>
        public IReadOnlyList<Transaction> GetTransactions() => _portfolioService.GetTransactions();

        /// <summary>Adds cash and records a Deposit, then refreshes the portfolio value.</summary>
        public async Task DepositCash(decimal amount, DateTime date, string note)
        {
            _portfolioService.DepositCash(amount, date, note);
            SyncCashFromService();
            StatusMessage = $"Deposited {amount:C} to cash.";
            await RefreshPerformanceAsync();
        }

        /// <summary>Removes cash (clamped to balance) and records a Withdrawal.</summary>
        public async Task WithdrawCash(decimal amount, DateTime date, string note)
        {
            _portfolioService.WithdrawCash(amount, date, note);
            SyncCashFromService();
            StatusMessage = $"Withdrew {amount:C} from cash.";
            await RefreshPerformanceAsync();
        }

        /// <summary>
        /// Directly overrides the cash balance (correction / testing). Unlike Deposit/Withdraw
        /// this does NOT record a ledger transaction — it just sets the number.
        /// </summary>
        public async Task EditCash(decimal newBalance)
        {
            _portfolioService.SetCash(newBalance);
            SyncCashFromService();
            StatusMessage = $"Cash balance set to ${_cashBalance:N2} (manual correction — not logged).";
            await RefreshPerformanceAsync();
        }

        /// <summary>
        /// Sells the selected position at <paramref name="price"/>, crediting net proceeds to
        /// cash and recording a Sell in the ledger. Refreshes positions, cash, and performance.
        /// </summary>
        public async Task SellSelectedPosition(decimal price, DateTime date)
        {
            if (SelectedHeld == null) return;
            var symbol = SelectedHeld.Symbol;
            var txn = _portfolioService.SellHeld(symbol, price, SelectedHeld.ShareCount, date);
            RefreshPortfolio();
            SyncCashFromService();
            if (txn != null)
                StatusMessage = $"Sold {txn.Shares} {txn.Symbol}: {txn.CashDeltaDisplay} to cash " +
                                $"(realized {txn.RealizedGainDisplay}).";
            await RefreshPerformanceAsync();
            await GenerateNewsReportAsync();
        }

        // ── Portfolio performance (week / month / quarter / year) ─────────────

        private PortfolioPerformance _performance = PortfolioPerformance.Empty;
        /// <summary>Reconstructed trailing-window performance of the held positions.</summary>
        public PortfolioPerformance Performance
        {
            get => _performance;
            private set => SetProperty(ref _performance, value);
        }

        private bool _isPerformanceLoading;
        public bool IsPerformanceLoading
        {
            get => _isPerformanceLoading;
            private set
            {
                if (SetProperty(ref _isPerformanceLoading, value))
                    ((RelayCommand)RefreshPerformanceCommand).RaiseCanExecuteChanged();
            }
        }

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

        // ── Earnings scanner ────────────────────────────────────────────────
        public static IReadOnlyList<int> EarningsWindowOptions { get; } =
            new[] { 7, 14, 30, 60, 90 };

        private string _earningsStatus = "Run a scan to find upcoming earnings.";
        /// <summary>Status line shown in the Earnings tab header area.</summary>
        public string EarningsStatus
        {
            get => _earningsStatus;
            private set => SetProperty(ref _earningsStatus, value);
        }

        private int _earningsWindowDays = 30;
        /// <summary>How many days ahead the earnings scanner looks.</summary>
        public int EarningsWindowDays
        {
            get => _earningsWindowDays;
            set
            {
                if (SetProperty(ref _earningsWindowDays, value))
                {
                    _userSettings.EarningsWindowDays = value;
                    _ = _userSettingsService.SaveAsync(_userSettings);
                    _ = GenerateEarningsPicksAsync();
                }
            }
        }

        // ── Earnings mode (Upcoming vs Just reported) ─────────────────────────

        /// <summary>Labels for the earnings mode selector; index matches the enum order.</summary>
        public static string[] EarningsModeOptions { get; } =
            { "Upcoming", "Just reported" };

        private EarningsScanMode _earningsMode = EarningsScanMode.Upcoming;
        /// <summary>Which side of the earnings date the scanner looks at.</summary>
        public EarningsScanMode EarningsMode
        {
            get => _earningsMode;
            set
            {
                if (SetProperty(ref _earningsMode, value))
                {
                    _userSettings.EarningsMode = value.ToString();
                    _ = _userSettingsService.SaveAsync(_userSettings);
                    OnPropertyChanged(nameof(SelectedEarningsMode));
                    OnPropertyChanged(nameof(IsJustReportedMode));
                    _ = GenerateEarningsPicksAsync();
                }
            }
        }

        /// <summary>
        /// String-facing adapter for the mode ComboBox. The grid columns bind
        /// <see cref="IsJustReportedMode"/> to hide post-earnings columns in Upcoming mode.
        /// </summary>
        public string SelectedEarningsMode
        {
            get => EarningsModeOptions[(int)_earningsMode];
            set
            {
                var idx = Array.IndexOf(EarningsModeOptions, value ?? "");
                EarningsMode = idx >= 0 ? (EarningsScanMode)idx : EarningsScanMode.Upcoming;
            }
        }

        /// <summary>True when the post-earnings rebound columns are relevant.</summary>
        public bool IsJustReportedMode => _earningsMode == EarningsScanMode.JustReported;

        private int _earningsLookbackDays = 5;
        /// <summary>How many days back "just reported" mode looks.</summary>
        public int EarningsLookbackDays
        {
            get => _earningsLookbackDays;
            set
            {
                var clamped = Math.Clamp(value, 1, 30);
                if (SetProperty(ref _earningsLookbackDays, clamped))
                {
                    _userSettings.EarningsLookbackDays = clamped;
                    _ = _userSettingsService.SaveAsync(_userSettings);
                    if (_earningsMode == EarningsScanMode.JustReported)
                        _ = GenerateEarningsPicksAsync();
                }
            }
        }

        private decimal _earningsTargetUpPercent = 5.0m;
        /// <summary>Target upside % the likelihood flag is measured against.</summary>
        public decimal EarningsTargetUpPercent
        {
            get => _earningsTargetUpPercent;
            set
            {
                var clamped = Math.Round(Math.Max(0.1m, value), 2);
                if (SetProperty(ref _earningsTargetUpPercent, clamped))
                {
                    _userSettings.EarningsTargetUpPercent = clamped;
                    _ = _userSettingsService.SaveAsync(_userSettings);
                    _ = GenerateEarningsPicksAsync();
                }
            }
        }

        private bool _earningsUseMargin;
        /// <summary>Whether the "buy on margin" toggle is on.</summary>
        public bool EarningsUseMargin
        {
            get => _earningsUseMargin;
            set
            {
                if (SetProperty(ref _earningsUseMargin, value))
                {
                    _userSettings.EarningsUseMargin = value;
                    _ = _userSettingsService.SaveAsync(_userSettings);
                    _ = GenerateEarningsPicksAsync();
                }
            }
        }

        private decimal _earningsMarginPercent = 50m;
        /// <summary>Equity margin requirement % (leverage = 100 / value).</summary>
        public decimal EarningsMarginPercent
        {
            get => _earningsMarginPercent;
            set
            {
                var clamped = Math.Round(Math.Min(100m, Math.Max(10m, value)), 0);
                if (SetProperty(ref _earningsMarginPercent, clamped))
                {
                    OnPropertyChanged(nameof(EarningsLeverageDisplay));
                    _userSettings.EarningsMarginPercent = clamped;
                    _ = _userSettingsService.SaveAsync(_userSettings);
                    if (_earningsUseMargin) _ = GenerateEarningsPicksAsync();
                }
            }
        }

        private decimal _earningsMarginRatePct = 12.5m;
        /// <summary>Assumed annualized margin interest rate %.</summary>
        public decimal EarningsMarginRatePct
        {
            get => _earningsMarginRatePct;
            set
            {
                var clamped = Math.Round(Math.Max(0m, value), 2);
                if (SetProperty(ref _earningsMarginRatePct, clamped))
                {
                    _userSettings.EarningsMarginRatePct = clamped;
                    _ = _userSettingsService.SaveAsync(_userSettings);
                    if (_earningsUseMargin) _ = GenerateEarningsPicksAsync();
                }
            }
        }

        /// <summary>Live leverage label derived from the margin %.</summary>
        public string EarningsLeverageDisplay =>
            $"{(100m / Math.Max(1m, _earningsMarginPercent)):0.0}× leverage";

        // ── News briefing ───────────────────────────────────────────────────
        private string _newsReport = "Run a scan to generate the News briefing.";
        /// <summary>
        /// A copy-paste-ready markdown briefing of the top 5 picks plus the active
        /// settings, intended to be pasted into another LLM for analysis.
        /// </summary>
        public string NewsReport
        {
            get => _newsReport;
            private set
            {
                if (SetProperty(ref _newsReport, value))
                    ((RelayCommand)CopyNewsCommand).RaiseCanExecuteChanged();
            }
        }

        private string _newsStatus = "No briefing yet.";
        /// <summary>Status line shown in the News tab header area.</summary>
        public string NewsStatus
        {
            get => _newsStatus;
            private set => SetProperty(ref _newsStatus, value);
        }

        /// <summary>How many top picks the briefing includes.</summary>
        private const int NewsTopCount = 5;

        // ── News briefing composition (persisted; each change re-renders) ─────

        /// <summary>Analysis-request presets for the ComboBox.</summary>
        public static IReadOnlyList<string> NewsAnalysisPresetOptions => NewsBriefingBuilder.AnalysisPresets;

        private bool NewsToggle(ref bool field, bool value, Action<UserSettings, bool> save)
        {
            if (field == value) return false;
            field = value;
            save(_userSettings, value);
            _ = _userSettingsService.SaveAsync(_userSettings);
            _ = GenerateNewsReportAsync();
            return true;
        }

        private bool _newsIncludePositions = true;
        public bool NewsIncludePositions
        {
            get => _newsIncludePositions;
            set { if (NewsToggle(ref _newsIncludePositions, value, (s, v) => s.NewsIncludePositions = v)) OnPropertyChanged(); }
        }

        private bool _newsIncludeBestAny = true;
        public bool NewsIncludeBestAny
        {
            get => _newsIncludeBestAny;
            set { if (NewsToggle(ref _newsIncludeBestAny, value, (s, v) => s.NewsIncludeBestAny = v)) OnPropertyChanged(); }
        }

        private bool _newsIncludePerStrategy = true;
        public bool NewsIncludePerStrategy
        {
            get => _newsIncludePerStrategy;
            set { if (NewsToggle(ref _newsIncludePerStrategy, value, (s, v) => s.NewsIncludePerStrategy = v)) OnPropertyChanged(); }
        }

        private bool _newsIncludeEarnings = true;
        public bool NewsIncludeEarnings
        {
            get => _newsIncludeEarnings;
            set { if (NewsToggle(ref _newsIncludeEarnings, value, (s, v) => s.NewsIncludeEarnings = v)) OnPropertyChanged(); }
        }

        private bool _newsIncludeTopPicks = true;
        public bool NewsIncludeTopPicks
        {
            get => _newsIncludeTopPicks;
            set { if (NewsToggle(ref _newsIncludeTopPicks, value, (s, v) => s.NewsIncludeTopPicks = v)) OnPropertyChanged(); }
        }

        private string _newsAnalysisPreset = "Full";
        /// <summary>Which question set the briefing ends with ("Full", "Risk review", …).</summary>
        public string NewsAnalysisPreset
        {
            get => _newsAnalysisPreset;
            set
            {
                if (SetProperty(ref _newsAnalysisPreset, value))
                {
                    _userSettings.NewsAnalysisPreset = value;
                    _ = _userSettingsService.SaveAsync(_userSettings);
                    _ = GenerateNewsReportAsync();
                }
            }
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
        public ColumnToggle ColReasoning       { get; }
        public ColumnToggle ColCashToMktCap    { get; }
        public ColumnToggle ColDebtToEquity    { get; }
        public ColumnToggle ColNetDebtToEquity { get; }
        public ColumnToggle ColRoe             { get; }
        public ColumnToggle ColCashHeavyLowDebt { get; }
        public ColumnToggle ColTargetMean      { get; }
        public ColumnToggle ColTargetDelta     { get; }

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
                    _ = LoadAnalystRatingsAsync(value?.Symbol);
                }
            }
        }

        private EarningsPick? _selectedEarningsPick;
        public EarningsPick? SelectedEarningsPick
        {
            get => _selectedEarningsPick;
            set
            {
                if (SetProperty(ref _selectedEarningsPick, value))
                {
                    ((RelayCommand)AddEarningsToWatchCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)AddEarningsToHeldCommand).RaiseCanExecuteChanged();
                    if (value != null) ActiveSelection = value;
                    _ = LoadChartAsync(value?.Symbol);
                    _ = LoadOptionsAsync(value?.Symbol);
                    _ = LoadAnalystRatingsAsync(value?.Symbol);
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
                    _ = LoadAnalystRatingsAsync(value?.Symbol);
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
                    _ = LoadAnalystRatingsAsync(value?.Symbol);
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
                    _ = LoadAnalystRatingsAsync(value?.Symbol);
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
                {
                    OnPropertyChanged(nameof(HasActiveSelection));
                    // WPF-ADAPTATION: AskAICommand.CanExecute reads ActiveSelectedSymbol() (derived
                    // from the current selection). WPF auto-requeried; Avalonia needs the explicit raise.
                    ((RelayCommand)AskAICommand).RaiseCanExecuteChanged();
                }
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

        // ── Analyst ratings (Details pane) ────────────────────────────────────

        private AnalystRatings? _selectedAnalystRatings;
        /// <summary>
        /// Analyst consensus data for the selected symbol, fetched on demand (service
        /// caches per symbol for 24h). Null when unavailable — the Details section
        /// collapses rather than showing an error.
        /// </summary>
        public AnalystRatings? SelectedAnalystRatings
        {
            get => _selectedAnalystRatings;
            private set
            {
                if (SetProperty(ref _selectedAnalystRatings, value))
                {
                    OnPropertyChanged(nameof(HasAnalystRatings));
                    OnPropertyChanged(nameof(ShowAnalystSection));
                }
            }
        }

        private bool _isAnalystLoading;
        /// <summary>True while the analyst-ratings fetch for the selection is in flight.</summary>
        public bool IsAnalystLoading
        {
            get => _isAnalystLoading;
            private set
            {
                if (SetProperty(ref _isAnalystLoading, value))
                    OnPropertyChanged(nameof(ShowAnalystSection));
            }
        }

        /// <summary>True when there are ratings to show for the selection.</summary>
        public bool HasAnalystRatings => _selectedAnalystRatings != null;

        /// <summary>Section visibility: shown while loading or when data arrived.</summary>
        public bool ShowAnalystSection => _isAnalystLoading || _selectedAnalystRatings != null;

        // Supersedes stale fetches: only the most recent selection's result lands.
        private System.Threading.CancellationTokenSource? _analystCts;

        /// <summary>
        /// Fetches analyst consensus data for the selected symbol and populates
        /// <see cref="SelectedAnalystRatings"/>. Cancels/supersedes any in-flight
        /// fetch; a cache hit inside the service costs no network round-trip.
        /// Errors leave the property null — no status-bar spam.
        /// </summary>
        private async Task LoadAnalystRatingsAsync(string? symbol = null)
        {
            symbol ??= ActiveSelectedSymbol();

            _analystCts?.Cancel();
            _analystCts?.Dispose();
            var cts = _analystCts = new System.Threading.CancellationTokenSource();

            if (string.IsNullOrWhiteSpace(symbol))
            {
                SelectedAnalystRatings = null;
                IsAnalystLoading = false;
                return;
            }

            SelectedAnalystRatings = null;
            IsAnalystLoading = true;
            try
            {
                var ratings = await _dataService.GetAnalystRatingsAsync(symbol, cts.Token);
                if (cts.IsCancellationRequested) return; // superseded by a newer selection

                // Inject the latest cached price so target displays can show upside.
                if (ratings != null && _cachedSummaries != null &&
                    _cachedSummaries.TryGetValue(symbol, out var qs))
                    ratings.CurrentPrice = qs.Price;

                SelectedAnalystRatings = ratings;
            }
            catch
            {
                if (!cts.IsCancellationRequested)
                    SelectedAnalystRatings = null;
            }
            finally
            {
                if (!cts.IsCancellationRequested)
                    IsAnalystLoading = false;
            }
        }

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

                // The full refresh (tables + News briefing) runs when the Settings
                // dialog closes — see RefreshAfterSettingsAsync — so we don't re-run
                // analysis on every spinner click behind the modal dialog.
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

            // Action dropdown (composes with Buy-Only rather than replacing it).
            if (ActionFromFilterOption(_selectedActionFilter) is RecommendationAction wanted &&
                rec.Action != wanted)
                return false;

            // Sector dropdown ("All Sectors" passes everything).
            if (!string.IsNullOrEmpty(_selectedSectorFilter) &&
                _selectedSectorFilter != AllSectorsOption &&
                !string.Equals(rec.Sector, _selectedSectorFilter, StringComparison.OrdinalIgnoreCase))
                return false;

            if (_cashHeavyLowDebtOnly && !FundamentalScreen.IsCashHeavyLowDebt(rec)) return false;

            var q = _searchText?.Trim();
            if (!string.IsNullOrEmpty(q))
            {
                if (!rec.Symbol.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !(rec.CompanyName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
                    return false;
            }

            return true;
        }

        /// <summary>Maps an Action-filter option string back to its enum value; null = no filter.</summary>
        private static RecommendationAction? ActionFromFilterOption(string option) => option switch
        {
            "Strong Buy"  => RecommendationAction.StrongBuy,
            "Buy"         => RecommendationAction.Buy,
            "Hold"        => RecommendationAction.Hold,
            "Sell"        => RecommendationAction.Sell,
            "Strong Sell" => RecommendationAction.StrongSell,
            _             => null,   // "All Actions" or unknown
        };

        /// <summary>Day-pick row filter for <see cref="DayPicksView"/>.</summary>
        private bool DayPickFilter(object obj)
        {
            if (obj is not DayPick pick) return false;

            // Direction dropdown ("All" passes everything).
            if (_selectedDirectionFilter == "Long"  && pick.Direction != DayPickDirection.Long)  return false;
            if (_selectedDirectionFilter == "Short" && pick.Direction != DayPickDirection.Short) return false;

            // Sector dropdown.
            if (!string.IsNullOrEmpty(_selectedDayPickSectorFilter) &&
                _selectedDayPickSectorFilter != AllSectorsOption &&
                !string.Equals(pick.Sector, _selectedDayPickSectorFilter, StringComparison.OrdinalIgnoreCase))
                return false;

            var q = _dayPickSearchText?.Trim();
            if (!string.IsNullOrEmpty(q))
            {
                if (!pick.Symbol.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                    !(pick.CompanyName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
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
                {
                    RecommendationsView?.Refresh();
                    RefreshFilterStatus();
                }
            }
        }

        private bool _buyOnlyFilter;
        public bool BuyOnlyFilter
        {
            get => _buyOnlyFilter;
            set
            {
                if (SetProperty(ref _buyOnlyFilter, value))
                {
                    RecommendationsView?.Refresh();
                    RefreshFilterStatus();
                }
            }
        }

        private bool _cashHeavyLowDebtOnly;
        /// <summary>
        /// When true, the recommendations grid shows only stocks that pass
        /// <see cref="StockPicker.Services.FundamentalScreen.IsCashHeavyLowDebt"/>:
        /// cash ≥ 10 % of market cap AND D/E ≤ 1.0 (degrades to cash-only when Finnhub
        /// data is unavailable).
        /// </summary>
        public bool CashHeavyLowDebtOnly
        {
            get => _cashHeavyLowDebtOnly;
            set
            {
                if (SetProperty(ref _cashHeavyLowDebtOnly, value))
                {
                    RecommendationsView?.Refresh();
                    RefreshFilterStatus();
                }
            }
        }

        // ── Recommendations: Action + Sector dropdown filters ────────────────

        /// <summary>Sentinel first entry for both sector dropdowns.</summary>
        private const string AllSectorsOption = "All Sectors";

        /// <summary>Sentinel first entry for the Action dropdown.</summary>
        private const string AllActionsOption = "All Actions";

        /// <summary>Sentinel first entry for the Daily Picks Direction dropdown.</summary>
        private const string AllDirectionsOption = "All";

        /// <summary>Options for the Recommendations Action dropdown.</summary>
        public static IReadOnlyList<string> ActionFilterOptions { get; } =
            new[] { AllActionsOption, "Strong Buy", "Buy", "Hold", "Sell", "Strong Sell" };

        private string _selectedActionFilter = AllActionsOption;
        public string SelectedActionFilter
        {
            get => _selectedActionFilter;
            set
            {
                if (SetProperty(ref _selectedActionFilter, value ?? AllActionsOption))
                {
                    RecommendationsView?.Refresh();
                    RefreshFilterStatus();
                }
            }
        }

        /// <summary>
        /// "All Sectors" + the distinct sectors present in the current recommendations.
        /// Rebuilt whenever the Recommendations collection repopulates.
        /// </summary>
        public ObservableCollection<string> SectorFilterOptions { get; } =
            new() { AllSectorsOption };

        private string _selectedSectorFilter = AllSectorsOption;
        public string SelectedSectorFilter
        {
            get => _selectedSectorFilter;
            set
            {
                if (SetProperty(ref _selectedSectorFilter, value ?? AllSectorsOption))
                {
                    RecommendationsView?.Refresh();
                    RefreshFilterStatus();
                }
            }
        }

        /// <summary>
        /// Rebuilds a sector dropdown's options from the rows now in the grid, keeping
        /// the current choice when that sector still exists (else back to "All Sectors").
        /// </summary>
        private static void RebuildSectorOptions(
            ObservableCollection<string> options, IEnumerable<string> sectors, ref string selected)
        {
            var distinct = sectors
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

            options.Clear();
            options.Add(AllSectorsOption);
            foreach (var s in distinct) options.Add(s);

            if (!options.Contains(selected))
                selected = AllSectorsOption;
        }

        /// <summary>Refreshes the Recommendations sector dropdown after a repopulate.</summary>
        private void RefreshSectorFilterOptions()
        {
            var previous = _selectedSectorFilter;
            RebuildSectorOptions(SectorFilterOptions,
                Recommendations.Select(r => r.Sector), ref _selectedSectorFilter);
            if (!string.Equals(previous, _selectedSectorFilter, StringComparison.Ordinal))
            {
                // Selection was reset (its sector vanished) — notify and re-filter,
                // since the field changed without going through the setter.
                OnPropertyChanged(nameof(SelectedSectorFilter));
                RecommendationsView?.Refresh();
                RefreshFilterStatus();
            }
        }

        // ── Daily Picks: view + filters ───────────────────────────────────────

        /// <summary>
        /// Filtered view over <see cref="DayPicks"/> — same pattern as
        /// <see cref="RecommendationsView"/>. Both Daily Picks grids bind here; rows
        /// remain <see cref="DayPick"/> items so multi-select command parameters and
        /// LoadingRow tint handlers are unaffected.
        /// </summary>
        public DataGridCollectionView DayPicksView { get; private set; } = null!;

        private string _dayPickSearchText = "";
        public string DayPickSearchText
        {
            get => _dayPickSearchText;
            set
            {
                if (SetProperty(ref _dayPickSearchText, value))
                {
                    DayPicksView?.Refresh();
                    RefreshDayPickFilterStatus();
                }
            }
        }

        /// <summary>Options for the Daily Picks Direction dropdown.</summary>
        public static IReadOnlyList<string> DirectionFilterOptions { get; } =
            new[] { AllDirectionsOption, "Long", "Short" };

        private string _selectedDirectionFilter = AllDirectionsOption;
        public string SelectedDirectionFilter
        {
            get => _selectedDirectionFilter;
            set
            {
                if (SetProperty(ref _selectedDirectionFilter, value ?? AllDirectionsOption))
                {
                    DayPicksView?.Refresh();
                    RefreshDayPickFilterStatus();
                }
            }
        }

        /// <summary>"All Sectors" + distinct sectors present in the current picks.</summary>
        public ObservableCollection<string> DayPickSectorFilterOptions { get; } =
            new() { AllSectorsOption };

        private string _selectedDayPickSectorFilter = AllSectorsOption;
        public string SelectedDayPickSectorFilter
        {
            get => _selectedDayPickSectorFilter;
            set
            {
                if (SetProperty(ref _selectedDayPickSectorFilter, value ?? AllSectorsOption))
                {
                    DayPicksView?.Refresh();
                    RefreshDayPickFilterStatus();
                }
            }
        }

        /// <summary>Refreshes the Daily Picks sector dropdown after a repopulate.</summary>
        private void RefreshDayPickSectorFilterOptions()
        {
            var previous = _selectedDayPickSectorFilter;
            RebuildSectorOptions(DayPickSectorFilterOptions,
                DayPicks.Select(p => p.Sector), ref _selectedDayPickSectorFilter);
            if (!string.Equals(previous, _selectedDayPickSectorFilter, StringComparison.Ordinal))
            {
                OnPropertyChanged(nameof(SelectedDayPickSectorFilter));
                DayPicksView?.Refresh();
                RefreshDayPickFilterStatus();
            }
        }

        /// <summary>True when any Daily Picks filter is narrowing the list.</summary>
        public bool IsDayPickFilterActive =>
            !string.IsNullOrWhiteSpace(_dayPickSearchText)
            || _selectedDirectionFilter    != AllDirectionsOption
            || _selectedDayPickSectorFilter != AllSectorsOption;

        /// <summary>Count of picks currently passing the filter.</summary>
        private int DayPickFilteredCount =>
            DayPicksView is null ? DayPicks.Count : DayPicksView.Cast<object>().Count();

        /// <summary>"12 picks" when unfiltered, "Showing 4 of 12" when filtered.</summary>
        public string DayPicksCountDisplay
        {
            get
            {
                int total = DayPicks.Count;
                if (total == 0) return "";
                int shown = DayPickFilteredCount;
                return shown == total ? $"{total} picks" : $"Showing {shown} of {total}";
            }
        }

        /// <summary>True when there are picks but the active filter hides them all.</summary>
        public bool HasNoDayPickFilterMatches => DayPicks.Count > 0 && DayPickFilteredCount == 0;

        /// <summary>Re-evaluates Daily Picks filter-derived display properties.</summary>
        private void RefreshDayPickFilterStatus()
        {
            OnPropertyChanged(nameof(IsDayPickFilterActive));
            OnPropertyChanged(nameof(DayPicksCountDisplay));
            OnPropertyChanged(nameof(HasNoDayPickFilterMatches));
            ((RelayCommand)ClearDayPickFiltersCommand).RaiseCanExecuteChanged();
        }

        /// <summary>Resets every Daily Picks filter (search, direction, sector).</summary>
        private void ClearDayPickFilters()
        {
            DayPickSearchText           = "";
            SelectedDirectionFilter     = AllDirectionsOption;
            SelectedDayPickSectorFilter = AllSectorsOption;
        }

        /// <summary>True when the search box, Buy-Only toggle, or a dropdown is narrowing the list.</summary>
        public bool IsFilterActive =>
            !string.IsNullOrWhiteSpace(_searchText)
            || _buyOnlyFilter
            || _cashHeavyLowDebtOnly
            || _selectedActionFilter != AllActionsOption
            || _selectedSectorFilter != AllSectorsOption;

        /// <summary>Count of rows currently passing the filter (post-refresh view count).</summary>
        private int FilteredCount =>
            RecommendationsView is null ? Recommendations.Count : RecommendationsView.Cast<object>().Count();

        /// <summary>
        /// "200 stocks" when unfiltered, "Showing 12 of 200" when filtered, "" before the
        /// first scan. Shown next to the search box.
        /// </summary>
        public string RecommendationsCountDisplay
        {
            get
            {
                int total = Recommendations.Count;
                if (total == 0) return "";
                int shown = FilteredCount;
                return shown == total ? $"{total} stocks" : $"Showing {shown} of {total}";
            }
        }

        /// <summary>True when there are recommendations but the active filter hides them all.</summary>
        public bool HasNoFilterMatches => Recommendations.Count > 0 && FilteredCount == 0;

        /// <summary>Re-evaluates the filter-derived display properties after a filter or data change.</summary>
        private void RefreshFilterStatus()
        {
            OnPropertyChanged(nameof(IsFilterActive));
            OnPropertyChanged(nameof(RecommendationsCountDisplay));
            OnPropertyChanged(nameof(HasNoFilterMatches));
            ((RelayCommand)ClearFiltersCommand).RaiseCanExecuteChanged();
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
                // Reload on ANY range change (1W→1Y sets true, 1Y→1W sets false via
                // IsChartWeek) — guarding on `value` broke the 1Y→1W direction, which
                // then only refreshed on the next stock reselection.
                if (SetProperty(ref _isChartYear, value))
                {
                    OnPropertyChanged(nameof(IsChartWeek));
                    _ = LoadChartAsync();
                }
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
                    // WPF-ADAPTATION: WPF's CommandManager auto-requeried these on UI focus events.
                    // Avalonia has no auto-requery, so every command whose CanExecute reads !IsBusy
                    // must be raised here explicitly. (ForceRefreshDayPicks / ForceRefreshEarnings also
                    // read _cachedHistory != null, which becomes non-null while IsBusy is still true
                    // during a scan; IsBusy → false at the end of the scan re-raises them correctly.)
                    ((RelayCommand)ForceRefreshDayPicksCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)ForceRefreshEarningsCommand).RaiseCanExecuteChanged();
                    ((RelayCommand)RefreshWatchPricesCommand).RaiseCanExecuteChanged();
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
            private set
            {
                if (SetProperty(ref _lastFetchTime, value))
                    OnPropertyChanged(nameof(DetailsAsOfDisplay));
            }
        }

        /// <summary>Provenance line for the Details pane, e.g. "Live data as of 2:15 PM".</summary>
        public string DetailsAsOfDisplay =>
            _lastFetchTime.HasValue ? $"Live data as of {_lastFetchTime.Value:t}" : "";

        private string _refreshStatus = "";
        public string RefreshStatus
        {
            get => _refreshStatus;
            private set => SetProperty(ref _refreshStatus, value);
        }

        // ── View-supplied services ────────────────────────────────────────────

        /// <summary>
        /// WPF-ADAPTATION: WPF called <c>System.Windows.Clipboard.SetText(...)</c> directly from
        /// the view model. Avalonia's clipboard is async and hangs off <c>TopLevel.Clipboard</c>,
        /// which a view model must not reach into (it would couple the VM to a Window/View).
        /// Instead the View (Phase 6B MainWindow code-behind) assigns this delegate to a
        /// <c>TopLevel.GetTopLevel(this)?.Clipboard!.SetTextAsync</c>-backed implementation.
        /// Every former <c>Clipboard.SetText</c> call now routes through <see cref="CopyToClipboard"/>,
        /// which no-ops safely when the delegate is unset (e.g. in tests or before the window loads).
        /// </summary>
        public Func<string, Task>? CopyToClipboardAsync { get; set; }

        /// <summary>Guarded clipboard write — no-ops until the View wires <see cref="CopyToClipboardAsync"/>.</summary>
        private async Task CopyToClipboard(string text)
        {
            if (CopyToClipboardAsync is not null)
                await CopyToClipboardAsync(text);
        }

        // ── Commands ──────────────────────────────────────────────────────────

        public ICommand ScanCommand              { get; }
        public ICommand AddToWatchCommand        { get; }
        public ICommand AddToHeldCommand         { get; }
        public ICommand ClearFiltersCommand      { get; }
        public ICommand ClearDayPickFiltersCommand { get; }
        public ICommand RemoveFromWatchCommand   { get; }
        public ICommand RemoveFromHeldCommand    { get; }
        public ICommand RefreshDayPicksCommand      { get; }
        public ICommand ForceRefreshDayPicksCommand  { get; }
        public ICommand AskAIAboutPicksCommand        { get; }
        public ICommand RefreshWatchPricesCommand     { get; }
        public ICommand AddDayPickToWatchCommand      { get; }
        public ICommand AddDayPickToHeldCommand       { get; }
        public ICommand ForceRefreshEarningsCommand   { get; }
        public ICommand AddEarningsToWatchCommand     { get; }
        public ICommand AddEarningsToHeldCommand      { get; }
        public ICommand RegenerateNewsCommand         { get; }
        public ICommand CopyNewsCommand               { get; }
        public ICommand SelectNewsSymbolCommand       { get; }
        public ICommand AddNewsSymbolToWatchCommand   { get; }
        public ICommand AskAINewsCommand              { get; }
        public ICommand PromoteWatchToPositionCommand { get; }
        public ICommand RefreshPerformanceCommand     { get; }
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
            // Only persistence-relevant properties should trigger a save. The key-test status
            // properties fire on every Test press and would otherwise rewrite settings 3x per
            // press with identical content.
            if (e.PropertyName is not (nameof(DataSourceToggle.IsEnabled)
                                    or nameof(DataSourceToggle.ApiKey)))
                return;

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

            // Compute portfolio performance in the background (held positions were
            // loaded from disk in the constructor).
            _ = RefreshPerformanceAsync();

            var configuredServices = GetConfiguredServices();
            var diskCache = await _scanCacheService.LoadAsync();

            // Hard expiry: never show data older than 24h — prices may be pre-split
            // or otherwise stale enough to be misleading. Treat as no cache at all.
            if (diskCache != null && (DateTime.Now - diskCache.FetchTime).TotalHours >= 24)
            {
                StatusMessage = $"Cached data from {diskCache.FetchTime:ddd MMM d} is over 24h old — discarded; fetching fresh data…";
                diskCache = null;
            }

            if (diskCache != null && IsCacheCompatible(diskCache, configuredServices))
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
                await GenerateDayPicksAsync();

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
                    await GenerateEarningsPicksAsync();

                    var when = IsMarketHours() ? "will auto-refresh shortly" : "market is closed";
                    StatusMessage = $"Showing cached data from {diskCache.FetchTime:HH:mm} — {when}.";
                    UpdateRefreshStatus();
                }
            }
            else if (diskCache != null)
            {
                StatusMessage = "Data-source settings changed. Fetching fresh data…";
                await RunWeeklyScanAsync();
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
        /// Returns null if the toggle is not fully configured.
        /// Yahoo Finance always returns the shared _dataService instance; Alpaca
        /// uses ALPACA_API_KEY and ALPACA_API_SECRET from the environment.
        /// </summary>
        private IStockDataService? BuildServiceForSource(DataSourceToggle ds)
        {
            return ds.SourceType switch
            {
                DataSourceType.YahooFinance => _dataService,
                DataSourceType.Stooq        => new StooqStockDataService(),
                DataSourceType.Alpaca       => AlpacaStockDataService.HasEnvironmentCredentials()
                    ? new AlpacaStockDataService() : null,
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

        /// <summary>Liquid, always-covered symbol used purely as an API-key probe.</summary>
        private const string KeyProbeSymbol = "AAPL";

        /// <summary>
        /// How many top recommendations the background two-pass enriches. Both sources it
        /// draws on accept only one symbol per request, so this is a deliberate coverage
        /// limit, not a page size — rows past it keep blank target/fundamental columns.
        /// </summary>
        private const int TwoPassRowCount = 20;

        /// <summary>Pacing between per-symbol analyst requests in the two-pass.</summary>
        private const int AnalystProbeDelayMs = 150;

        /// <summary>
        /// How many post-earnings picks get EPS/target enrichment. Each one costs up to three
        /// single-symbol requests, so this is bounded rather than the full 50-pick list.
        /// </summary>
        private const int ReportedEnrichCount = 25;

        /// <summary>
        /// How far before the announcement a reported fiscal period may end and still be treated
        /// as that announcement's numbers. Companies report a quarter that closed weeks earlier
        /// (a 30 Jun quarter announced 30 Jul), so a generous window is correct here — but not so
        /// generous that the PREVIOUS quarter, roughly 90 days back, slips through.
        /// </summary>
        private const int ReportedPeriodToleranceDays = 45;

        /// <summary>
        /// Backs the Settings "Test" button: returns true only when <paramref name="ds"/>'s
        /// API key actually returns usable data.
        ///
        /// Finnhub is probed through <c>/stock/metric</c> rather than a quote endpoint,
        /// because that is the endpoint the cash-heavy &amp; low-debt screen depends on — a
        /// key that fetches quotes but is not entitled to fundamentals would otherwise test
        /// "OK" while leaving the D/E, NetDebt/Eq, and ROE columns permanently blank.
        ///
        /// Every other keyed source is probed with a single latest-quote call.
        /// </summary>
        /// <remarks>
        /// Intentionally coarse — see <see cref="DataSourceToggle.KeyValidator"/>. Callers get
        /// a bare pass/fail, never a reason, and this never throws.
        /// </remarks>
        private async Task<bool> ValidateApiKeyAsync(DataSourceToggle ds)
        {
            if (string.IsNullOrWhiteSpace(ds.ApiKey)) return false;

            try
            {
                if (ds.SourceType == DataSourceType.Finnhub)
                {
                    var finnhub = new FinnhubStockDataService(ds.ApiKey);
                    var ok = await finnhub.GetFundamentalsAsync(KeyProbeSymbol) != null;
                    // A key that just proved itself must not stay on the skip list, or the
                    // scan would keep ignoring a key Settings reports as working.
                    if (ok && string.Equals(_finnhubRejectedKey, ds.ApiKey, StringComparison.Ordinal))
                        _finnhubRejectedKey = null;
                    return ok;
                }

                var svc = BuildServiceForSource(ds);
                if (svc == null) return false;
                return await svc.GetLatestQuoteAsync(KeyProbeSymbol) != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Choose the primary quote source for equities, preferring Alpaca when it is
        /// enabled and configured, then Yahoo Finance, then the first remaining source.
        /// </summary>
        private static IStockDataService SelectPrimaryQuoteService(IReadOnlyList<IStockDataService> services)
        {
            return services.FirstOrDefault(s => s.SourceType == DataSourceType.Alpaca)
                ?? services.FirstOrDefault(s => s.SourceType == DataSourceType.YahooFinance)
                ?? services.First();
        }

        private List<IStockDataService> GetConfiguredServices()
        {
            var services = DataSources
                .Where(d => d.IsEnabled)
                .Select(BuildServiceForSource)
                .Where(svc => svc != null)
                .Select(svc => svc!)
                .ToList();

            if (services.Count == 0)
                services.Add(_dataService);

            return services;
        }

        private bool IsCacheCompatible(ScanCache cache, IReadOnlyList<IStockDataService> configuredServices)
        {
            var cachedSources = cache.EnabledSources
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

            var currentSources = configuredServices
                .Select(s => s.SourceType.ToString())
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

            if (!cachedSources.SequenceEqual(currentSources, StringComparer.Ordinal))
                return false;

            var currentPrimary = SelectPrimaryQuoteService(configuredServices).SourceType.ToString();
            return string.Equals(cache.PrimaryQuoteSource, currentPrimary, StringComparison.Ordinal);
        }

        /// <summary>
        /// Merges per-source history dictionaries into a single symbol → bars map.
        ///
        /// Split/dividend-adjusted bars (Tiingo, Polygon) must never be combined with
        /// raw bars (Yahoo, Alpaca, Finnhub, Alpha Vantage, Stooq) — after a split they
        /// differ by the split ratio, and averaging them corrupts every indicator built
        /// on the series. So per symbol we keep ONE homogeneous class: whichever of
        /// adjusted/raw covers more trading days (ties prefer adjusted). Within that
        /// class, same-date bars from multiple sources are averaged.
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
                // Gather bars per source, split into adjusted vs raw classes.
                var adjByDate  = new Dictionary<DateTime, List<StockQuote>>();
                var rawByDate  = new Dictionary<DateTime, List<StockQuote>>();
                var adjSources = new List<DataSourceType>();
                var rawSources = new List<DataSourceType>();

                foreach (var (srcType, srcDict) in perSource)
                {
                    if (!srcDict.TryGetValue(sym, out var bars) || bars.Count == 0)
                        continue;

                    // A source's series is "adjusted" only if every bar is (Tiingo can
                    // fall back to raw per-bar; a partially-adjusted series is unsafe).
                    bool srcAdjusted = bars.All(b => b.IsAdjusted);
                    var byDate      = srcAdjusted ? adjByDate : rawByDate;
                    (srcAdjusted ? adjSources : rawSources).Add(srcType);

                    foreach (var bar in bars)
                    {
                        var day = bar.Timestamp.Date;
                        if (!byDate.TryGetValue(day, out var list))
                            byDate[day] = list = new List<StockQuote>(4);
                        list.Add(bar);
                    }
                }

                // Pick the class with better coverage; ties prefer adjusted.
                bool useAdjusted = adjByDate.Count >= rawByDate.Count && adjByDate.Count > 0;
                var barsByDate           = useAdjusted ? adjByDate  : rawByDate;
                var contributingSources  = useAdjusted ? adjSources : rawSources;

                if (barsByDate.Count == 0)
                {
                    merged[sym] = Array.Empty<StockQuote>();
                    srcMap[sym] = adjSources.Concat(rawSources).ToList();
                    continue;
                }

                // Average multi-source bars on the same date (same class only).
                var mergedBars = new List<StockQuote>(barsByDate.Count);
                foreach (var (day, bars) in barsByDate.OrderBy(kv => kv.Key))
                {
                    int n = bars.Count;
                    mergedBars.Add(new StockQuote
                    {
                        Symbol     = sym,
                        Timestamp  = day,
                        Open       = bars.Sum(b => b.Open)   / n,
                        High       = bars.Sum(b => b.High)   / n,
                        Low        = bars.Sum(b => b.Low)    / n,
                        Close      = bars.Sum(b => b.Close)  / n,
                        Volume     = bars.Sum(b => b.Volume) / n,
                        IsAdjusted = useAdjusted,
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
                var services = GetConfiguredServices();

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

                // ── 1c. Live quote summaries — Alpaca preferred when enabled ───
                var primaryQuoteService = SelectPrimaryQuoteService(services);
                _cachedPrimaryQuoteSource = primaryQuoteService.SourceType;
                StatusMessage = $"Fetching live market data from {primaryQuoteService.SourceType.ShortName()} for {total} stocks…";
                _cachedSummaries = await primaryQuoteService.GetQuoteSummariesAsync(
                    _cachedUniverse.Select(s => s.Symbol));

                // Supplement with data from remaining sources for any symbols the
                // primary quote service missed.
                foreach (var svc in services.Where(s => s.SourceType != primaryQuoteService.SourceType))
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

        // ── Settings-change refresh ───────────────────────────────────────────

        /// <summary>
        /// Snapshot of the scanning/analysis settings that should trigger a refresh
        /// when changed. Captured before the Settings dialog opens and compared
        /// against the live values once it closes.
        /// </summary>
        public sealed record ScanSettingsSnapshot(
            IndexUniverse Index,
            int UniverseSize,
            decimal TargetWeekly,
            string EnabledSources);

        /// <summary>Captures the current scanning/analysis settings for later comparison.</summary>
        public ScanSettingsSnapshot CaptureScanSettings() => new(
            SelectedIndex,
            UniverseSize,
            TargetProfitMarginPercent,
            string.Join(",", DataSources.Where(d => d.IsEnabled)
                                        .Select(d => d.SourceType.ToString())
                                        .OrderBy(s => s, StringComparer.Ordinal)));

        /// <summary>
        /// Re-runs the pipeline after the Settings dialog closes, but only when a
        /// scanning/analysis setting actually changed:
        ///   • Universe, scan limit, or enabled data sources changed → full network
        ///     re-scan (refreshes Recommendations, Day Picks, Earnings, and the News
        ///     briefing from fresh data).
        ///   • Only the profit target changed → re-run analysis against the cached
        ///     data and refresh every table + the briefing (no network round-trip).
        /// Nothing relevant changed → no work is done.
        /// </summary>
        public async Task RefreshAfterSettingsAsync(ScanSettingsSnapshot? before)
        {
            if (before is null || IsBusy) return;

            var after = CaptureScanSettings();

            bool dataChanged = before.Index          != after.Index
                            || before.UniverseSize   != after.UniverseSize
                            || before.EnabledSources != after.EnabledSources;

            bool analysisChanged = before.TargetWeekly != after.TargetWeekly;

            if (dataChanged)
            {
                // New universe / limit / sources → the cached data no longer matches
                // the request, so fetch fresh. RunWeeklyScanAsync → ApplyStrategyAsync
                // (isScan: true) regenerates all three tables and the News briefing.
                StatusMessage = "Settings updated — rescanning with the new data set…";
                await RunWeeklyScanAsync();
            }
            else if (analysisChanged && _cachedUniverse != null)
            {
                // Targets changed but the underlying data is still valid — re-run
                // against the cache (no network) and refresh every table + briefing.
                StatusMessage = "Settings updated — refreshing analysis, picks, and briefing…";
                await ApplyStrategyAsync(isScan: false);   // Recommendations + News briefing
                await GenerateDayPicksAsync(force: true);  // Day Picks table
                await GenerateEarningsPicksAsync();        // Earnings table
                StatusMessage = "Updated for the new targets.";
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
                EnabledSources = _cachedHistoryPerSource.Keys
                    .Select(s => s.ToString())
                    .OrderBy(s => s, StringComparer.Ordinal)
                    .ToList(),
                PrimaryQuoteSource = _cachedPrimaryQuoteSource?.ToString() ?? string.Empty,
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

            // Capture the generation token before any async work so the Finnhub background
            // pass can detect that a newer scan has superseded it.
            var scanGen = System.Threading.Interlocked.Increment(ref _scanGeneration);

            var context = new ScanContext
            {
                Strategy                  = SelectedStrategy,
                TargetProfitMarginPercent = TargetProfitMarginPercent,
                WeekStart                 = _cachedWeekStart,
                WeekEnd                   = _cachedWeekEnd,
                Summaries                 = _cachedSummaries,
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
                    rec.DayOpen          = qs.DayOpen;
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
                    rec.TotalCash          = qs.TotalCash;
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

            // Apply cash-strength confidence tilt using Yahoo cash data (available for all recs).
            // Finnhub D/E is intentionally excluded here — it is only populated for the top-20
            // in the background two-pass and would unfairly bias partial rows if used in ranking.
            FundamentalScreen.ApplyCashStrengthTilt(recs);

            // Re-sort after tilt so ranking reflects the adjusted confidence scores.
            recs = recs
                .OrderByDescending(r => r.Confidence)
                .ThenBy(r => r.ActionSortOrder)
                .ThenBy(r => r.Symbol)
                .ToList();

            // Flash-free grid update
            Recommendations.ReplaceAll(recs);
            RefreshSectorFilterOptions();
            RefreshFilterStatus();
            // WPF-ADAPTATION: AskAINewsCommand.CanExecute reads Recommendations.Count > 0.
            // ReplaceAll fires a Reset (not per-item Add), and WPF auto-requeried on it;
            // Avalonia needs the explicit raise so the button enables after the first scan.
            ((RelayCommand)AskAINewsCommand).RaiseCanExecuteChanged();

            // ── Background two-pass enrichment ────────────────────────────────────
            // Fetches the data that cannot ride along on the batch quote call and patches it
            // into the top rows, then redraws once. Two sources, both limited to one symbol
            // per request, which is why coverage stops at TwoPassRowCount:
            //   • Yahoo quoteSummary → analyst 1Y price targets (always available, 24h cached)
            //   • Finnhub /stock/metric → D/E, net-D/E, ROE (only when a key is configured)
            //
            // Intentionally fire-and-forget: the grid is already usable from the ReplaceAll
            // above. If a newer scan starts (_scanGeneration changes), this bails rather than
            // clobbering fresh results. Both fetches share one pass so the grid redraws once
            // instead of two passes racing each other through ReplaceAll.
            var finnhubDs = DataSources.FirstOrDefault(
                d => d.SourceType == DataSourceType.Finnhub && d.IsEnabled &&
                     !string.IsNullOrWhiteSpace(d.ApiKey));

            // A key Finnhub already rejected will be rejected again, so don't spend a request
            // per scan rediscovering it. Keyed on the key text, so pasting a new one in Settings
            // clears the block automatically.
            if (finnhubDs != null &&
                string.Equals(_finnhubRejectedKey, finnhubDs.ApiKey, StringComparison.Ordinal))
            {
                finnhubDs = null;
            }

            {
                var topRows = recs.Take(TwoPassRowCount).ToList();
                var topSyms = topRows.Select(r => r.Symbol).ToList();
                var finnhubSvc = finnhubDs != null ? new FinnhubStockDataService(finnhubDs.ApiKey) : null;
                var capturedGen = scanGen;
                var capturedRecs = recs; // same list that was handed to ReplaceAll
                var capturedFinnhubDs = finnhubDs;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        // ── Analyst price targets (Yahoo, no key needed) ──────────────
                        foreach (var rec in topRows)
                        {
                            if (System.Threading.Interlocked.Read(ref _scanGeneration) != capturedGen) return;

                            var ratings = await _dataService.GetAnalystRatingsAsync(rec.Symbol);
                            if (ratings != null)
                            {
                                rec.TargetMeanPrice         = ratings.TargetMeanPrice;
                                rec.NumberOfAnalystOpinions = ratings.NumberOfAnalystOpinions;
                            }

                            // Gentle pacing so a 20-symbol sweep doesn't look like a burst.
                            // Cache hits still pay this, but 20 x 150 ms is invisible against a
                            // pass that is already off the UI thread.
                            await Task.Delay(AnalystProbeDelayMs);
                        }

                        if (System.Threading.Interlocked.Read(ref _scanGeneration) != capturedGen) return;

                        // ── Finnhub fundamentals (only when a key is configured) ──────
                        if (finnhubSvc != null && capturedFinnhubDs != null)
                        {
                            var fundamentals = await finnhubSvc.GetFundamentalsBatchAsync(topSyms);

                            // Key rejected: remember it so later scans skip this entirely, and
                            // show the verdict in Settings so it stops being invisible.
                            if (finnhubSvc.AuthFailed)
                            {
                                var rejectedKey = capturedFinnhubDs.ApiKey;
                                await Dispatcher.UIThread.InvokeAsync(() =>
                                {
                                    _finnhubRejectedKey = rejectedKey;
                                    capturedFinnhubDs.MarkKeyInvalid();
                                });
                            }
                            else
                            {
                                foreach (var rec in topRows)
                                {
                                    if (fundamentals.TryGetValue(rec.Symbol, out var f))
                                    {
                                        rec.DebtToEquity      = f.DebtToEquity;
                                        rec.NetDebtToEquity   = f.NetDebtToEquity;
                                        rec.ReturnOnEquityPct = f.ReturnOnEquity;
                                    }
                                }
                            }
                        }

                        // Bail again in case a scan started while we were fetching.
                        if (System.Threading.Interlocked.Read(ref _scanGeneration) != capturedGen) return;

                        // Refresh the grid on the UI thread so bindings update.
                        // WPF-ADAPTATION: was System.Windows.Application.Current?.Dispatcher.
                        // Avalonia exposes the UI thread statically, so there is no null case.
                        // InvokeAsync (not Invoke) avoids blocking the thread-pool thread on
                        // the UI thread, eliminating a latent deadlock risk.
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (System.Threading.Interlocked.Read(ref _scanGeneration) != capturedGen) return;
                            Recommendations.ReplaceAll(capturedRecs);
                            RefreshFilterStatus();
                        });
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[two-pass] background enrichment error: {ex.Message}");
                    }
                });
            }

            // Update live prices on watch and held items from the fresh summary cache
            // BEFORE building the briefing, so the positions section reflects current P/L.
            UpdatePortfolioPrices();

            // The scan collections now hold real data, so context exports may safely
            // overwrite the previous session's bundle from here on (the constructor-time
            // RefreshPortfolio fires before any data exists — see _contextExportEnabled).
            _contextExportEnabled = true;

            // Refresh the News briefing so it always reflects the latest picks/settings.
            await GenerateNewsReportAsync();

            if (isScan)
            {
                _lastScanTime = DateTime.Now.ToString("HH:mm");
                OnPropertyChanged(nameof(LastScanTimeDisplay));
                StatusMessage = $"Scan complete — {recs.Count} recommendations generated.";
                UpdateRefreshStatus();
                await GenerateDayPicksAsync();
                await GenerateEarningsPicksAsync();
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
                    RefreshDayPickSectorFilterOptions();
                    RefreshDayPickFilterStatus();
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
                RefreshDayPickSectorFilterOptions();
                RefreshDayPickFilterStatus();
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

        // ── Earnings scanner ────────────────────────────────────────────────────

        private async Task GenerateEarningsPicksAsync()
        {
            if (_cachedUniverse == null || _cachedHistory == null || _cachedSummaries == null)
            {
                EarningsStatus = "No cached data — run a scan first.";
                return;
            }

            bool reported = _earningsMode == EarningsScanMode.JustReported;

            EarningsStatus = reported
                ? $"Scanning for earnings reported in the last {_earningsLookbackDays} days…"
                : $"Scanning for earnings in the next {_earningsWindowDays} days…";

            try
            {
                var readonlyHistory =
                    new System.Collections.ObjectModel.ReadOnlyDictionary<string, IReadOnlyList<StockQuote>>(
                        _cachedHistory.ToDictionary(
                            kv => kv.Key,
                            kv => (IReadOnlyList<StockQuote>)kv.Value,
                            StringComparer.OrdinalIgnoreCase));

                var picks = await _earningsScanService.GenerateAsync(
                    _cachedUniverse,
                    readonlyHistory,
                    _cachedSummaries,
                    _cachedNameLookup,
                    _earningsWindowDays,
                    _earningsTargetUpPercent,
                    _earningsUseMargin,
                    _earningsMarginPercent,
                    _earningsMarginRatePct,
                    _earningsMode,
                    _earningsLookbackDays);

                EarningsPicks.ReplaceAll(picks);

                if (!reported)
                {
                    int flagged = picks.Count(p => p.MeetsThreshold);
                    EarningsStatus = picks.Count > 0
                        ? $"{picks.Count} with earnings ≤ {_earningsWindowDays}d  •  {flagged} flagged ≥ {_earningsTargetUpPercent:0.#}%"
                        : $"No upcoming earnings within {_earningsWindowDays} days (data source may not report dates for these symbols).";
                    return;
                }

                if (picks.Count == 0)
                {
                    EarningsStatus =
                        $"No earnings reported in the last {_earningsLookbackDays} days " +
                        "(data source may not report dates for these symbols).";
                    return;
                }

                // The rebound score needs EPS surprise and analyst targets, both one request per
                // symbol. The grid is already populated and sorted by selloff size, so enrich in
                // the background and rescore as data lands.
                EarningsStatus =
                    $"{picks.Count} reported in the last {_earningsLookbackDays}d  •  " +
                    "fetching EPS surprise and analyst targets…";
                await EnrichReportedPicksAsync(picks);
            }
            catch (Exception ex)
            {
                EarningsStatus = $"Earnings scan error: {ex.Message}";
            }
        }

        /// <summary>
        /// Fills EPS surprise and analyst price targets onto post-earnings picks, then rescores
        /// them so the rebound ranking reflects the new data.
        ///
        /// Both fetches are one symbol per request, so this is capped at
        /// <see cref="ReportedEnrichCount"/> and runs after the grid is already usable. EPS
        /// prefers Finnhub — Yahoo's earningsHistory lags the freshest prints by a few days,
        /// which is exactly the window this mode targets — and falls back to Yahoo when Finnhub
        /// is absent, unkeyed, or rejected.
        /// </summary>
        private async Task EnrichReportedPicksAsync(IReadOnlyList<EarningsPick> picks)
        {
            var finnhubDs = DataSources.FirstOrDefault(
                d => d.SourceType == DataSourceType.Finnhub && d.IsEnabled &&
                     !string.IsNullOrWhiteSpace(d.ApiKey) &&
                     !string.Equals(_finnhubRejectedKey, d.ApiKey, StringComparison.Ordinal));

            var finnhubSvc = finnhubDs != null ? new FinnhubStockDataService(finnhubDs.ApiKey) : null;
            var targets = picks.Take(ReportedEnrichCount).ToList();
            int withSurprise = 0;

            foreach (var pick in targets)
            {
                // Abandon quietly if the user switched modes or a rescan replaced the list.
                if (!ReferenceEquals(EarningsPicks.FirstOrDefault(), picks.FirstOrDefault())) return;

                EarningsSurprise? surprise = null;
                if (finnhubSvc != null && !finnhubSvc.AuthFailed)
                    surprise = await finnhubSvc.GetEarningsSurpriseAsync(pick.Symbol);

                // Finnhub off, unkeyed, rejected, or simply without coverage → try Yahoo.
                // GetEarningsSurpriseAsync is Yahoo-specific rather than on IStockDataService,
                // matching how the Finnhub fundamentals call is kept off the shared interface.
                if (surprise == null && _dataService is YahooFinanceStockDataService yahooSvc)
                    surprise = await yahooSvc.GetEarningsSurpriseAsync(pick.Symbol);

                // Only accept a surprise that actually belongs to THIS announcement. Yahoo often
                // still serves the previous quarter for a fresh print; attributing that to today's
                // report would invent a beat that has not been published.
                if (surprise != null &&
                    surprise.PeriodEnd >= pick.NextEarningsDate.AddDays(-ReportedPeriodToleranceDays))
                {
                    pick.Surprise = surprise;
                    withSurprise++;
                }

                var ratings = await _dataService.GetAnalystRatingsAsync(pick.Symbol);
                if (ratings != null)
                    pick.TargetMeanPrice = ratings.TargetMeanPrice;

                EarningsScanService.ScoreRebound(pick);
                await Task.Delay(AnalystProbeDelayMs);
            }

            if (finnhubSvc is { AuthFailed: true } && finnhubDs != null)
            {
                _finnhubRejectedKey = finnhubDs.ApiKey;
                finnhubDs.MarkKeyInvalid();
            }

            // Rebound score only becomes meaningful now, so reorder on it.
            var rescored = picks.OrderByDescending(p => p.OpportunityScore)
                                .ThenBy(p => p.PostEarningsMovePct ?? 0)
                                .ToList();
            EarningsPicks.ReplaceAll(rescored);

            int flagged = rescored.Count(p => p.MeetsThreshold);
            EarningsStatus =
                $"{rescored.Count} reported in the last {_earningsLookbackDays}d  •  " +
                $"{flagged} sold off but beat with ≥ {_earningsTargetUpPercent:0.#}% upside  •  " +
                $"EPS surprise for {withSurprise}/{targets.Count}";
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

            // Buys/edits/removes move cash (equity outlay), so keep the displayed balance in sync.
            SyncCashFromService();

            UpdatePortfolioPrices();

            // Every portfolio mutation funnels through here — keep the on-disk
            // LLM context bundle in step with what the user now holds.
            ScheduleContextExport();
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

        // ── Context export (LLM-consumable bundle) ────────────────────────────

        private CancellationTokenSource _contextExportCts = new();

        /// <summary>
        /// True once <see cref="RefreshPerformanceAsync"/> has produced a real
        /// <see cref="Performance"/> value. The VM property itself is non-nullable
        /// (initialized to <see cref="PortfolioPerformance.Empty"/>), so without this
        /// flag the first export after launch would write a misleading $0
        /// performance.json instead of skipping the file.
        /// </summary>
        private bool _performanceComputed;

        /// <summary>
        /// Gates <see cref="ScheduleContextExport"/> until the first
        /// <see cref="ApplyStrategyAsync"/> has populated the scan collections.
        /// The constructor-time <see cref="RefreshPortfolio"/> would otherwise
        /// overwrite the previous session's context files with empty lists
        /// ~500 ms after launch.
        /// </summary>
        private bool _contextExportEnabled;

        /// <summary>
        /// Schedules a debounced export of the current app state to
        /// %LOCALAPPDATA%\StockPicker\context\ (same debounce pattern as
        /// PortfolioService.SaveAsync: cancel any pending export and start a fresh
        /// 500 ms countdown, so bursts of triggers coalesce into one write).
        ///
        /// The bundle is snapshotted HERE, on the UI thread, as IMMUTABLE whitelist
        /// DTOs (ContextProjections.Project* copies — never live model references,
        /// so the deferred export can't observe torn, mid-mutation state; and never
        /// the UserSettings object itself, so API keys can never reach the exporter).
        /// Only the file write runs off the UI thread.
        /// </summary>
        private void ScheduleContextExport()
        {
            if (!_contextExportEnabled) return;

            var bundle = new ContextBundle
            {
                Recommendations      = Recommendations.Select(ContextProjections.ProjectRecommendation).ToList(),
                Earnings             = EarningsPicks.Select(ContextProjections.ProjectEarnings).ToList(),
                DayPicks             = DayPicks.Select(ContextProjections.ProjectDayPick).ToList(),
                Positions            = HeldList.Select(ContextProjections.ProjectPosition).ToList(),
                Transactions         = _portfolioService.GetTransactions().Select(ContextProjections.ProjectTransaction).ToList(),
                CashBalance          = CashBalance,
                Performance          = _performanceComputed
                                           ? ContextProjections.ProjectPerformance(Performance)
                                           : null,
                NewsBriefingMarkdown = NewsReport,
                DataFetchTime        = LastFetchTime,
                EnabledSources       = _userSettings.EnabledDataSources.ToList(),
                UniverseDescription  = SelectedIndexDescription,
                StrategyName         = SelectedStrategy?.Name ?? string.Empty,
                GeneratedAt          = DateTime.Now,

                // ── App-state snapshot ("what's going on right now") ──────────
                ActiveStrategy       = SelectedStrategy?.Id ?? string.Empty,
                ActiveStrategyName   = SelectedStrategy?.Name ?? string.Empty,
                Universe             = SelectedIndexDescription,
                SelectedSymbol       = ActiveSelectedSymbol(),
                // The VM has no explicit active-tab property; Recommendations is the
                // primary grid, so it is reported as the active view.
                ActiveView           = "Recommendations",
                Sort                 = new SortState(
                                           SavedSortColumn,
                                           string.Equals(SavedSortDirection, "Descending",
                                               StringComparison.OrdinalIgnoreCase)),
                LastScanUtc          = LastFetchTime?.ToUniversalTime(),
                StalenessHours       = LastFetchTime.HasValue
                                           ? Math.Round((DateTime.Now - LastFetchTime.Value).TotalHours, 1)
                                           : null,
            };

            // Cancel the previously scheduled export (if any), dispose the spent
            // CTS, and start a new countdown.
            _contextExportCts.Cancel();
            _contextExportCts.Dispose();
            _contextExportCts = new CancellationTokenSource();
            var token = _contextExportCts.Token;

            _ = Task.Delay(500, token)
                    .ContinueWith(
                        _ => _contextExportService.ExportAsync(bundle),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnRanToCompletion,
                        TaskScheduler.Default)
                    .Unwrap();
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
        // ── News briefing ───────────────────────────────────────────────────────

        /// <summary>
        /// Builds a copy-paste-ready markdown briefing of the top picks (ranked by the
        /// app's global settings) and stores it in <see cref="NewsReport"/>.
        /// </summary>
        private async Task GenerateNewsReportAsync()
        {
            if (Recommendations.Count == 0)
            {
                NewsReport = "Run a scan to generate the News briefing.";
                NewsStatus = "No briefing yet — run a scan first.";
                return;
            }

            // Both cross-strategy views (best-any + per-strategy tops), independent of
            // the selected strategy. Memoized against the cached data, so strategy/target
            // flips are instant.
            var cross = await GetCrossStrategyAsync();

            var input = new BriefingInput
            {
                StrategyName         = SelectedStrategy?.Name ?? "(default)",
                UniverseDescription  = SelectedIndexDescription,
                TargetWeeklyPercent  = TargetProfitMarginPercent,
                TargetMonthlyPercent = TargetMonthlyProfitPercent,
                DataSources          = _userSettings.EnabledDataSources is { Count: > 0 }
                                           ? _userSettings.EnabledDataSources
                                           : new List<string> { "YahooFinance" },
                LastDataRefresh      = _lastScanTime,
                Recommendations      = Recommendations.ToList(),
                Positions            = HeldList.ToList(),
                Earnings             = EarningsPicks.ToList(),
                BestAnyStrategy      = cross.Best,
                PerStrategy          = cross.PerStrategy,
                MarketIndices        = MarketIndices.ToList(),
                Performance          = _performanceComputed ? Performance : null,
                CashBalance          = _portfolioService.GetCash(),
                EarningsWindowDays   = _earningsWindowDays,
                TopCount             = NewsTopCount,
                GeneratedAt          = DateTime.Now,
                IncludePositions     = NewsIncludePositions,
                IncludeBestAny       = NewsIncludeBestAny,
                IncludePerStrategy   = NewsIncludePerStrategy,
                IncludeEarnings      = NewsIncludeEarnings,
                IncludeTopPicks      = NewsIncludeTopPicks,
                AnalysisPreset       = NewsAnalysisPreset,
            };

            NewsReport = NewsBriefingBuilder.Build(input);
            NewsStatus = $"Briefing ready — positions, earnings & cross-strategy picks · {DateTime.Now:HH:mm}";

            // The briefing runs last in the scan pipeline, so this snapshot captures
            // recommendations, earnings, day picks, and the fresh briefing together.
            ScheduleContextExport();
        }

        // Cross-strategy cache (best-any + per-strategy tops). Recomputed only when the
        // underlying price data (referenced by _cachedHistory) is replaced by a fresh fetch.
        private CrossStrategyResult? _crossStrategy;
        private object? _crossStrategyComputedFor;

        /// <summary>
        /// Both cross-strategy views: top Buy/StrongBuy per symbol across every strategy
        /// (score-ranked, with consensus counts) plus each strategy's own top picks.
        /// Delegates to the shared <see cref="ScanEngine"/> and memoizes against the
        /// current cached data set so strategy/target flips don't recompute.
        /// </summary>
        private async Task<CrossStrategyResult> GetCrossStrategyAsync()
        {
            if (_cachedUniverse == null || _cachedHistory == null)
                return new CrossStrategyResult();

            if (_crossStrategy != null && ReferenceEquals(_crossStrategyComputedFor, _cachedHistory))
                return _crossStrategy;

            var cross = await ScanEngine.CrossStrategyAsync(
                BuildScanData(), Strategies.ToList(), TargetProfitMarginPercent,
                _analysisService, _recommendationService, NewsTopCount);

            _crossStrategy            = cross;
            _crossStrategyComputedFor = _cachedHistory;
            return cross;
        }

        /// <summary>Snapshots the in-memory cache fields into a <see cref="ScanData"/> for the engine.</summary>
        private ScanData BuildScanData() => new()
        {
            Universe   = _cachedUniverse  ?? Array.Empty<Stock>(),
            History    = _cachedHistory   ?? new Dictionary<string, IReadOnlyList<StockQuote>>(StringComparer.OrdinalIgnoreCase),
            Summaries  = _cachedSummaries ?? new Dictionary<string, QuoteSummary>(StringComparer.OrdinalIgnoreCase),
            NameLookup = _cachedNameLookup ?? new Dictionary<string, (string Name, string Sector)>(StringComparer.OrdinalIgnoreCase),
            WeekStart  = _cachedWeekStart,
            WeekEnd    = _cachedWeekEnd,
        };

        /// <summary>Copy the current News briefing to the clipboard.</summary>
        private async Task CopyNewsReport()
        {
            if (string.IsNullOrWhiteSpace(NewsReport)) return;
            await CopyToClipboard(NewsReport);   // WPF-ADAPTATION: was System.Windows.Clipboard.SetText
            NewsStatus  = $"Copied to clipboard · {DateTime.Now:HH:mm}";
            StatusMessage = "News briefing copied to clipboard — paste it into any LLM.";
        }

        // ── News tab interactivity ──────────────────────────────────────────────

        /// <summary>
        /// A symbol was clicked inside the rendered briefing: surface it in the shared
        /// Details pane and load its chart/options, using the richest object we have.
        /// </summary>
        private void SelectNewsSymbol(string? symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return;

            object? selection =
                (object?)Recommendations.FirstOrDefault(r => r.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                ?? HeldList.FirstOrDefault(p => p.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
                ?? (object?)EarningsPicks.FirstOrDefault(e => e.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

            // Fall back to a minimal record built from the quote cache so the Details
            // pane still shows something for cross-strategy picks outside the current list.
            if (selection == null)
            {
                var rec = new Recommendation { Symbol = symbol.ToUpperInvariant(), SourceTag = "News" };
                if (_cachedSummaries != null && _cachedSummaries.TryGetValue(symbol, out var qs))
                {
                    rec.CompanyName  = qs.LongName ?? qs.ShortName ?? symbol;
                    rec.LastPrice    = qs.Price;
                    rec.DayChangePct = qs.DayChangePct;
                }
                selection = rec;
            }

            ActiveSelection = selection;
            _ = LoadChartAsync(symbol);
            _ = LoadOptionsAsync(symbol);
            _ = LoadAnalystRatingsAsync(symbol);
            StatusMessage = $"{symbol.ToUpperInvariant()} selected from the News briefing.";
        }

        /// <summary>An inline [+ watch] link was clicked next to a pick in the briefing.</summary>
        private void AddNewsSymbolToWatch(string? symbol)
        {
            if (string.IsNullOrWhiteSpace(symbol)) return;
            symbol = symbol.ToUpperInvariant();

            var rec = Recommendations.FirstOrDefault(r => r.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));
            if (rec == null)
            {
                rec = new Recommendation
                {
                    Symbol    = symbol,
                    Action    = RecommendationAction.Buy,
                    SourceTag = "News",
                };
                if (_cachedSummaries != null && _cachedSummaries.TryGetValue(symbol, out var qs))
                {
                    rec.CompanyName = qs.LongName ?? qs.ShortName ?? symbol;
                    rec.Sector      = qs.Sector ?? "";
                    rec.LastPrice   = qs.Price;
                }
            }
            else
            {
                rec.SourceTag = "News";
            }

            rec.WatchedPrice = rec.LastPrice;
            rec.WatchedAt    = DateTime.Now;
            _portfolioService.AddToWatch(rec);
            RefreshPortfolio();
            StatusMessage = $"{symbol} added to Watch from the News briefing.";
        }

        /// <summary>True when there is a real briefing to save (not the placeholder text).</summary>
        public bool HasNewsReport =>
            !string.IsNullOrWhiteSpace(NewsReport) && Recommendations.Count > 0;

        /// <summary>A sensible default file name for saving the briefing.</summary>
        public string SuggestedNewsFileName =>
            $"StockPicker-Briefing-{DateTime.Now:yyyy-MM-dd-HHmm}.md";

        /// <summary>
        /// Writes the current News briefing to <paramref name="path"/>. The View supplies
        /// the path via a Save dialog; this method owns the write and status update.
        /// </summary>
        public void SaveNewsReport(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(NewsReport)) return;

            try
            {
                System.IO.File.WriteAllText(path, NewsReport);
                NewsStatus    = $"Saved to {System.IO.Path.GetFileName(path)} · {DateTime.Now:HH:mm}";
                StatusMessage = $"News briefing saved to {path}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Could not save briefing: {ex.Message}";
            }
        }

        /// <summary>Copy the briefing and open Claude in the browser.</summary>
        private async Task AskAIAboutNews()
        {
            if (Recommendations.Count == 0) return;
            await GenerateNewsReportAsync();
            await CopyToClipboard(NewsReport);   // WPF-ADAPTATION: was System.Windows.Clipboard.SetText
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo("https://claude.ai/new") { UseShellExecute = true });
            StatusMessage = "News briefing copied — paste into Claude!";
        }

        private async Task AskAIAboutPicks()
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
            await CopyToClipboard(prompt);   // WPF-ADAPTATION: was System.Windows.Clipboard.SetText

            var url = $"https://claude.ai/new";
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

            StatusMessage = $"Daily Picks batch prompt ({DayPicks.Count} stocks) copied to clipboard — paste into Claude!";
        }

        private async Task AskAI(string ai)
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
            await CopyToClipboard(prompt);   // WPF-ADAPTATION: was System.Windows.Clipboard.SetText

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
            _ = RefreshPerformanceAsync();
            StatusMessage = picks.Count == 1
                ? $"{picks[0].Symbol} added to Positions."
                : $"{picks.Count} picks added to Positions.";
        }

        // ── Earnings portfolio actions ──────────────────────────────────────────

        /// <summary>
        /// Resolves the command parameter from the Earnings DataGrid multi-select into a flat
        /// list of <see cref="EarningsPick"/> items, falling back to <see cref="SelectedEarningsPick"/>.
        /// </summary>
        private List<EarningsPick> ResolveSelectedEarnings(object? parameter)
        {
            if (parameter is System.Collections.IList list)
            {
                var picks = new List<EarningsPick>(list.Count);
                foreach (var item in list)
                    if (item is EarningsPick ep) picks.Add(ep);
                if (picks.Count > 0) return picks;
            }
            return SelectedEarningsPick != null
                ? new List<EarningsPick> { SelectedEarningsPick }
                : new List<EarningsPick>();
        }

        /// <summary>Add all selected Earnings picks to the watch list.</summary>
        private void AddEarningsToWatch(object? parameter)
        {
            var picks = ResolveSelectedEarnings(parameter);
            if (picks.Count == 0) return;

            foreach (var pick in picks)
            {
                var rec = new Recommendation
                {
                    Symbol       = pick.Symbol,
                    CompanyName  = pick.CompanyName,
                    Sector       = pick.Sector,
                    SourceTag    = "Earnings",
                    LastPrice    = pick.LastPrice,
                    WatchedPrice = pick.LastPrice,
                    WatchedAt    = DateTime.Now,
                    Action       = RecommendationAction.Buy,
                };
                _portfolioService.AddToWatch(rec);
            }

            RefreshPortfolio();
            StatusMessage = picks.Count == 1
                ? $"{picks[0].Symbol} added to Watch."
                : $"{picks.Count} picks added to Watch.";
        }

        /// <summary>Add all selected Earnings picks to held positions.</summary>
        private void AddEarningsToHeld(object? parameter)
        {
            var picks = ResolveSelectedEarnings(parameter);
            if (picks.Count == 0) return;

            foreach (var pick in picks)
            {
                var rec = new Recommendation
                {
                    Symbol      = pick.Symbol,
                    CompanyName = pick.CompanyName,
                    Sector      = pick.Sector,
                    SourceTag   = "Earnings",
                    LastPrice   = pick.LastPrice,
                    Action      = RecommendationAction.Buy,
                };
                _portfolioService.AddToHeld(rec);
            }

            RefreshPortfolio();
            _ = RefreshPerformanceAsync();
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
            _ = RefreshPerformanceAsync();
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
            _ = RefreshPerformanceAsync();
        }

        /// <summary>Promote the selected watch item to an open position.</summary>
        private void PromoteWatchToPosition()
        {
            if (SelectedWatch == null) return;
            _portfolioService.AddToHeld(SelectedWatch);
            _portfolioService.RemoveFromWatch(SelectedWatch.Symbol);
            RefreshPortfolio();
            _ = RefreshPerformanceAsync();
            StatusMessage = $"{SelectedWatch?.Symbol ?? "Stock"} moved to Positions.";
        }

        // ── Manual position entry + performance ───────────────────────────────

        /// <summary>
        /// Adds a new manually-entered position or updates an existing one, then refreshes
        /// the Positions table, the performance panel, and the News briefing.
        /// </summary>
        public async Task UpsertPosition(HeldPosition position)
        {
            if (position == null) return;

            _portfolioService.UpsertHeld(position);
            RefreshPortfolio();                  // reload HeldList + inject cached prices
            StatusMessage = $"{position.Symbol} saved to Positions.";
            await RefreshPerformanceAsync();     // recompute W/M/Q/Y
            await GenerateNewsReportAsync();     // briefing reflects the updated holdings
        }

        /// <summary>
        /// Recomputes trailing-window performance for the held positions by pulling each
        /// symbol's price history. Safe to call with an empty portfolio (clears the panel).
        /// </summary>
        public async Task RefreshPerformanceAsync()
        {
            if (HeldList.Count == 0)
            {
                // No positions, but still surface cash on hand as the portfolio value.
                Performance = new PortfolioPerformance { CashBalance = CashBalance };
                _performanceComputed = true;   // real (cash-only) figures — exportable
                return;
            }

            IsPerformanceLoading = true;
            try
            {
                Performance = await PerformanceService.ComputeAsync(
                    HeldList.ToList(), _dataService, CashBalance);
                _performanceComputed = true;   // context exports may now include performance.json
            }
            catch (Exception ex)
            {
                StatusMessage = $"Performance unavailable ({ex.Message}).";
            }
            finally
            {
                IsPerformanceLoading = false;
            }
        }
    }
}