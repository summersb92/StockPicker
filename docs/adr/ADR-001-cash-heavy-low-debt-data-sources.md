# ADR-001: Cash-Heavy & Low-Debt Screening Data Sources

**Date:** 2025-06-25  
**Status:** Accepted  
**Deciders:** Development Team  

## Context

The app added a fundamental screening feature to identify cash-heavy, financially-strong stocks. The core metric is **cash as a percentage of market cap**, combined with **debt-to-equity ratios**. This required deciding:

1. Where to source debt/equity data (Yahoo vs. Finnhub)
2. How to distribute API calls within rate limits
3. Which metrics feed into the confidence score

## Decision

### 1. Use Finnhub for Debt-to-Equity, NOT Yahoo

**Why:** Yahoo Finance v7 `/quote` endpoint (the app's existing universal quote source) does not return `debtToEquity` or `totalDebt` fields. The alternative Yahoo endpoint—v10 `quoteSummary.financialData`—returns 401 Unauthorized under the app's existing cookie/crumb handshake. Finnhub's `/stock/metric` endpoint provides all three metrics (D/E, net-D/E, ROE) in a single, authenticated call.

**Trade-off:** Finnhub requires an API key (free tier: 60 calls/min). Yahoo would be free but doesn't have the data.

### 2. Two-Pass Enrichment Pattern: Rank First, Enrich Top-20 in Background

**Why:** The free Finnhub tier allows ~60 calls/min. Enriching all ~500 stocks in the scan universe would take ~8.5 minutes, blocking the UI. Instead:
- **Pass 1 (synchronous):** Score and rank all stocks using Yahoo cash data (available for the full universe)
- **Pass 2 (background):** Fetch Finnhub fundamentals for only the top 20 ranked stocks, then patch those fields and refresh the grid

Mechanism: `_scanGeneration` counter increments at the start of `ApplyStrategyAsync`. The background task captures this value and bails early if a newer scan has started, preventing stale updates from clobbering fresh results.

**Trade-off:** Users see partial Finnhub data (top 20 only) until the background pass completes (~1–2 seconds for 20 calls). This is acceptable because:
- The grid is immediately usable with the cash-strength score
- The top 20 are the most actionable picks
- The refresh is seamless (ReplaceAll on the UI thread after acquiring the dispatcher)

### 3. Score Tilt Uses ONLY Cash/MktCap (Yahoo), NOT Debt/Equity (Finnhub)

**Why:** `FundamentalScreen.ApplyCashStrengthTilt` scales the confidence score up to +5% based on Cash/MktCap. This metric is available universe-wide from Yahoo. If the score tilt included Finnhub D/E (only for the post-ranking top-20), it would unfairly bias those ~20 stocks upward while the rest of the universe never gets enriched, skewing the initial ranking.

**Solution:** The tilt is restricted to Cash/MktCap. The filter `IsCashHeavyLowDebt` (applied to the grid) gracefully degrades: it checks D/E if available, net-D/E if D/E is missing, and falls back to cash-only if neither Finnhub field is populated.

## Consequences

### Positive
- **Yahoo baseline:** Cash data is instantly available for all stocks, enabling fast, parallel scoring
- **Finnhub depth:** Top-20 picks receive debt metrics without blocking the UI
- **Graceful degradation:** Stocks outside the top-20 still show as "cash-heavy" (if they meet the threshold), even without D/E
- **Clean separation:** Data sourcing is explicit: Yahoo → universal, Finnhub → post-ranking enrichment

### Risks & Mitigations

**Risk: Finnhub units are unverified**

The parser logs raw values via `Debug.WriteLine("[Finnhub units] ...")` on the first successful call to verify assumptions:
- `DebtToEquity`: assumed to be a raw ratio (e.g., 1.5 = 150% D/E). If Finnhub returns pre-scaled values (e.g., 150 for 150%), thresholds and display logic must be updated.
- `ReturnOnEquity` / `NetDebtToEquity`: similar units caveat.

**Mitigation:** All display formatting and threshold logic are centralized:
- Display helpers: `DebtToEquityDisplay`, `RoeDisplay`, `NetDebtToEquityDisplay` in `Recommendation.cs`
- Thresholds: named constants in `FundamentalScreen` (`LowDebtMaxDebtToEquity`, `NetCashMaxNetDebtToEquity`)

Once a live Finnhub key is available and the first call is made, the debug output will confirm units. Updating the constants and display helpers is a one-line change per metric.

**Risk: Finnhub API key missing or exhausted**

If the key is absent or the free tier is exhausted, the background pass silently fails. No error message is shown to the user. The grid remains usable with the cash-strength score and no Finnhub enrichment.

**Mitigation:** Wrapped in try/catch. Debug output logs the error. Future: add a status indicator showing "Finnhub unavailable" if the user enables the source and it repeatedly fails.

## Alternatives Considered

1. **Use Yahoo v10 quoteSummary instead of Finnhub**
   - Pros: no API key required
   - Cons: returns 401 under the app's existing handshake; would require a separate auth pathway
   - Decision: abandoned—too much rework for a feature that exists in Finnhub

2. **Enrich ALL stocks with Finnhub, not just top-20**
   - Pros: consistent D/E data across the board
   - Cons: ~8.5 min load time for the free tier; blocks the UI
   - Decision: rejected—UX impact outweighs the benefit

3. **Don't include D/E in the score at all**
   - Pros: simplest implementation
   - Cons: loses a key financial strength signal
   - Decision: rejected—folded into the filter instead (grid can be filtered "cash-heavy + low-debt")

## References

- `StockPicker.Services.FundamentalScreen` — screening logic and thresholds
- `StockPicker.ViewModels.MainViewModel.ApplyStrategyAsync` (lines 1697–1872) — two-pass enrichment pattern
- `StockPicker.Models.Recommendation` — display formatters and field definitions
- `FinnhubStockDataService.GetFundamentalsBatchAsync` — Finnhub API integration
