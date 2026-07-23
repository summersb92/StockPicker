using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using StockPicker.Models;
using StockPicker.Services;
using Xunit;

namespace StockPicker.Core.Tests
{
    /// <summary>
    /// Ledger invariants of PortfolioService, isolated from the user's real store by
    /// the constructor seam: every test gets its own unique temp folder (xUnit builds
    /// a fresh test-class instance per test), so %LOCALAPPDATA%\StockPicker is never
    /// read or written. FlushAsync forces the debounced save where persistence is
    /// itself under test.
    /// </summary>
    public class PortfolioServiceTests : IDisposable
    {
        private readonly string _folder = Path.Combine(
            Path.GetTempPath(), "StockPickerTests", Guid.NewGuid().ToString("N"));

        private PortfolioService NewService() => new(_folder);

        public void Dispose()
        {
            // Best-effort: a debounced save may still land after this (it recreates
            // only this GUID-named folder under %TEMP%, which is harmless).
            try { Directory.Delete(_folder, recursive: true); } catch { /* ignore */ }
        }

        private static HeldPosition CashBuy(string symbol, decimal price, int shares) => new()
        {
            Symbol      = symbol,
            CompanyName = symbol + " Inc.",
            EntryPrice  = price,
            ShareCount  = shares,
            EntryDate   = DateTime.Today,
        };

        // ── Fresh store ───────────────────────────────────────────────────────

        [Fact]
        public void FreshStore_StartsEmptyWithZeroCash()
        {
            var svc = NewService();

            Assert.Equal(0m, svc.GetCash());
            Assert.Empty(svc.GetHeld());
            Assert.Empty(svc.GetWatchList());
            Assert.Empty(svc.GetTransactions());
            Assert.Null(svc.StartupLoadError);
        }

        // ── Deposit / withdraw ────────────────────────────────────────────────

        [Fact]
        public void Deposit_IncreasesCashAndRecordsTransaction()
        {
            var svc = NewService();

            svc.DepositCash(1000m, new DateTime(2025, 6, 2), "seed");

            Assert.Equal(1000m, svc.GetCash());
            var txn = Assert.Single(svc.GetTransactions());
            Assert.Equal(TransactionType.Deposit, txn.Type);
            Assert.Equal(1000m, txn.CashDelta);
            Assert.Equal(new DateTime(2025, 6, 2), txn.Date);
            Assert.Equal("seed", txn.Note);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-50)]
        public void Deposit_NonPositiveAmount_IsANoOp(int amount)
        {
            var svc = NewService();

            svc.DepositCash(amount, DateTime.Today, "bad");

            Assert.Equal(0m, svc.GetCash());
            Assert.Empty(svc.GetTransactions());
        }

        [Fact]
        public void Withdraw_DecreasesCashAndRecordsNegativeDelta()
        {
            var svc = NewService();
            svc.DepositCash(1000m, DateTime.Today, "");

            svc.WithdrawCash(400m, new DateTime(2025, 6, 3), "rent");

            Assert.Equal(600m, svc.GetCash());
            var txn = svc.GetTransactions().Last();
            Assert.Equal(TransactionType.Withdrawal, txn.Type);
            Assert.Equal(-400m, txn.CashDelta);
            Assert.Equal("rent", txn.Note);
        }

        [Fact]
        public void Withdraw_MoreThanBalance_IsClampedToAvailableCash()
        {
            // Actual behavior: the service clamps to the balance rather than rejecting.
            var svc = NewService();
            svc.DepositCash(100m, DateTime.Today, "");

            svc.WithdrawCash(250m, DateTime.Today, "overdraw");

            Assert.Equal(0m, svc.GetCash());
            var txn = svc.GetTransactions().Last();
            Assert.Equal(TransactionType.Withdrawal, txn.Type);
            Assert.Equal(-100m, txn.CashDelta);   // only what was actually there
        }

        [Fact]
        public void Withdraw_WithZeroCash_IsANoOpWithNoLedgerEntry()
        {
            var svc = NewService();

            svc.WithdrawCash(50m, DateTime.Today, "nothing there");

            Assert.Equal(0m, svc.GetCash());
            Assert.Empty(svc.GetTransactions());
        }

        [Fact]
        public void SetCash_ClampsNegativeToZero_AndLogsNoTransaction()
        {
            var svc = NewService();

            svc.SetCash(750m);
            Assert.Equal(750m, svc.GetCash());

            svc.SetCash(-10m);
            Assert.Equal(0m, svc.GetCash());

            Assert.Empty(svc.GetTransactions());   // SetCash is a direct override, not a ledger event
        }

        // ── Buy (UpsertHeld) ──────────────────────────────────────────────────

        [Fact]
        public void Buy_DebitsEquityFromCashAndCreatesPositionAndLedgerLine()
        {
            var svc = NewService();
            svc.DepositCash(1000m, DateTime.Today, "");

            svc.UpsertHeld(CashBuy("ACME", price: 50m, shares: 10));

            Assert.Equal(500m, svc.GetCash());     // 1000 − (50 × 10)
            var pos = Assert.Single(svc.GetHeld());
            Assert.Equal("ACME", pos.Symbol);
            Assert.Equal(10, pos.ShareCount);

            var buy = svc.GetTransactions().Last();
            Assert.Equal(TransactionType.Buy, buy.Type);
            Assert.Equal(10, buy.Shares);
            Assert.Equal(50m, buy.Price);
            Assert.Equal(-500m, buy.CashDelta);    // full cost for a cash buy
            Assert.False(buy.OnMargin);
            Assert.Null(buy.RealizedGain);
        }

        [Fact]
        public void MarginBuy_DebitsOnlyTheEquityDownPayment()
        {
            var svc = NewService();
            svc.DepositCash(1000m, DateTime.Today, "");

            var pos = CashBuy("LEV", price: 100m, shares: 10);   // cost 1000
            pos.BoughtOnMargin = true;
            pos.MarginPercent  = 50m;                            // equity 500, borrowed 500
            svc.UpsertHeld(pos);

            Assert.Equal(500m, svc.GetCash());     // only the down payment leaves cash
            var buy = svc.GetTransactions().Last();
            Assert.Equal(-500m, buy.CashDelta);
            Assert.True(buy.OnMargin);
        }

        [Fact]
        public void EditingAPosition_MovesOnlyTheEquityDifference_AndUpdatesTheSameBuyLine()
        {
            var svc = NewService();
            svc.DepositCash(2000m, DateTime.Today, "");
            svc.UpsertHeld(CashBuy("ACME", 50m, 10));            // cash 2000 → 1500

            svc.UpsertHeld(CashBuy("ACME", 50m, 20));            // equity 500 → 1000

            Assert.Equal(1000m, svc.GetCash());                  // only the +500 delta moved
            Assert.Equal(20, Assert.Single(svc.GetHeld()).ShareCount);

            // The open Buy line is synced in place — no second Buy entry.
            var buys = svc.GetTransactions().Where(t => t.Type == TransactionType.Buy).ToList();
            var buy  = Assert.Single(buys);
            Assert.Equal(20, buy.Shares);
            Assert.Equal(-1000m, buy.CashDelta);
        }

        [Fact]
        public void UpsertHeld_NullOrBlankSymbol_IsANoOp()
        {
            var svc = NewService();

            svc.UpsertHeld(null!);
            svc.UpsertHeld(new HeldPosition { Symbol = "  " });

            Assert.Empty(svc.GetHeld());
            Assert.Empty(svc.GetTransactions());
        }

        [Fact]
        public void AddToHeldFromRecommendation_CreatesZeroShareStub_WithNoCashOutlay()
        {
            var svc = NewService();
            svc.DepositCash(1000m, DateTime.Today, "");

            svc.AddToHeld(new Recommendation { Symbol = "STUB", LastPrice = 42m });

            var pos = Assert.Single(svc.GetHeld());
            Assert.Equal(0, pos.ShareCount);       // shares entered later via the edit dialog
            Assert.Equal(42m, pos.EntryPrice);
            Assert.Equal(1000m, svc.GetCash());    // 0 shares → 0 equity → no cash move

            var buy = svc.GetTransactions().Last();
            Assert.Equal(TransactionType.Buy, buy.Type);
            Assert.Equal(0m, buy.CashDelta);
        }

        // ── Sell ──────────────────────────────────────────────────────────────

        [Fact]
        public void SellAll_CreditsProceedsComputesRealizedGainAndClosesPosition()
        {
            var svc = NewService();
            svc.DepositCash(1000m, DateTime.Today, "");
            svc.UpsertHeld(CashBuy("ACME", 50m, 10));            // cash → 500

            var txn = svc.SellHeld("ACME", sellPrice: 60m, shares: 0, date: new DateTime(2025, 6, 6));

            Assert.NotNull(txn);
            Assert.Equal(TransactionType.Sell, txn!.Type);
            Assert.Equal(10, txn.Shares);                        // shares ≤ 0 sells the lot
            Assert.Equal(60m, txn.Price);
            Assert.Equal(600m, txn.CashDelta);                   // gross proceeds, no loan
            Assert.Equal(100m, txn.RealizedGain);                // 600 − 500 equity cost
            Assert.Equal(new DateTime(2025, 6, 6), txn.Date);

            Assert.Equal(1100m, svc.GetCash());                  // 500 + 600
            Assert.Empty(svc.GetHeld());
        }

        [Fact]
        public void PartialSell_ProratesCostAndReducesShareCount()
        {
            var svc = NewService();
            svc.DepositCash(1000m, DateTime.Today, "");
            svc.UpsertHeld(CashBuy("ACME", 50m, 10));

            var txn = svc.SellHeld("ACME", sellPrice: 60m, shares: 4, date: DateTime.Today);

            Assert.NotNull(txn);
            Assert.Equal(4, txn!.Shares);
            Assert.Equal(240m, txn.CashDelta);                   // 60 × 4
            Assert.Equal(40m, txn.RealizedGain);                 // 240 − (500 × 0.4)

            var pos = Assert.Single(svc.GetHeld());
            Assert.Equal(6, pos.ShareCount);
            Assert.Equal(740m, svc.GetCash());                   // 500 + 240
        }

        [Fact]
        public void MarginSell_RepaysLoanAndAccruedInterestBeforeCreditingCash()
        {
            var svc = NewService();
            svc.DepositCash(1000m, DateTime.Today, "");

            var pos = CashBuy("LEV", price: 100m, shares: 10);   // cost 1000
            pos.BoughtOnMargin            = true;
            pos.MarginPercent             = 50m;                 // equity 500, borrowed 500
            pos.MarginInterestRatePercent = 10m;
            pos.EntryDate                 = DateTime.Today.AddDays(-365);
            svc.UpsertHeld(pos);                                 // cash 1000 → 500

            // Interest accrues off the real clock (DaysHeld = 365 → 500 × 10% × 365/365);
            // snapshot the position's own figure so the assertion can't drift.
            var held             = Assert.Single(svc.GetHeld());
            var expectedInterest = held.InterestAccrued;         // 50m for a full year
            Assert.True(expectedInterest > 0m, "margin position must be accruing interest");

            var txn = svc.SellHeld("LEV", sellPrice: 120m, shares: 0, date: DateTime.Today);

            Assert.NotNull(txn);
            // Net proceeds = gross 1200 − loan 500 − interest.
            Assert.Equal(1200m - 500m - expectedInterest, txn!.CashDelta);
            // Realized gain = net proceeds − equity cost 500 (identity holds regardless of clock).
            Assert.Equal(txn.CashDelta - 500m, txn.RealizedGain);
            Assert.True(txn.OnMargin);

            Assert.Equal(500m + txn.CashDelta, svc.GetCash());
            Assert.Empty(svc.GetHeld());
        }

        [Fact]
        public void Sell_UnknownSymbol_ReturnsNullAndChangesNothing()
        {
            var svc = NewService();
            svc.DepositCash(100m, DateTime.Today, "");

            var txn = svc.SellHeld("GHOST", 10m, 1, DateTime.Today);

            Assert.Null(txn);
            Assert.Equal(100m, svc.GetCash());
            Assert.Single(svc.GetTransactions());   // just the deposit
        }

        // ── Remove (undo a mistaken entry) ────────────────────────────────────

        [Fact]
        public void RemoveFromHeld_RefundsTheOpenBuyAndDropsItsLedgerLine()
        {
            var svc = NewService();
            svc.DepositCash(1000m, DateTime.Today, "");
            svc.UpsertHeld(CashBuy("OOPS", 50m, 10));            // cash → 500

            svc.RemoveFromHeld("OOPS");

            Assert.Equal(1000m, svc.GetCash());                  // exact refund of the outlay
            Assert.Empty(svc.GetHeld());
            Assert.DoesNotContain(svc.GetTransactions(), t => t.Type == TransactionType.Buy);
            Assert.Single(svc.GetTransactions());                // the deposit survives
        }

        // ── Persistence round-trip via the seam ───────────────────────────────

        [Fact]
        public async Task FlushAsync_PersistsStateThatANewInstanceReloads()
        {
            var svc = NewService();
            svc.DepositCash(1000m, new DateTime(2025, 6, 2), "seed");
            svc.UpsertHeld(CashBuy("ACME", 50m, 10));
            await svc.FlushAsync();

            var reloaded = new PortfolioService(_folder);

            Assert.Equal(500m, reloaded.GetCash());
            var pos = Assert.Single(reloaded.GetHeld());
            Assert.Equal("ACME", pos.Symbol);
            Assert.Equal(10, pos.ShareCount);
            Assert.Equal(2, reloaded.GetTransactions().Count);
            Assert.Equal(TransactionType.Deposit, reloaded.GetTransactions()[0].Type);
            Assert.Equal(TransactionType.Buy,     reloaded.GetTransactions()[1].Type);
        }

        [Fact]
        public void CorruptStore_IsBackedUpReportedAndReplacedWithFreshState()
        {
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "portfolio.json"), "{ not valid json !!!");

            var svc = NewService();

            Assert.NotNull(svc.StartupLoadError);
            Assert.Equal(0m, svc.GetCash());
            Assert.Empty(svc.GetHeld());
            Assert.Single(Directory.GetFiles(_folder, "portfolio.json.corrupt-*"));
        }
    }
}
