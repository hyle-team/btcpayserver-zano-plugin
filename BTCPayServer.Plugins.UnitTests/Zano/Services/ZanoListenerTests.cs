using System;
using System.Collections.Generic;

using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Zano.Payments;
using BTCPayServer.Plugins.Zano.RPC.Models;
using BTCPayServer.Plugins.Zano.Services;

using Xunit;

namespace BTCPayServer.Plugins.UnitTests.Zano.Services
{
    public class ZanoListenerTests
    {
        [Trait("Category", "Unit")]
        [Theory]
        [InlineData(SpeedPolicy.HighSpeed, 0)]
        [InlineData(SpeedPolicy.MediumSpeed, 1)]
        [InlineData(SpeedPolicy.LowMediumSpeed, 2)]
        [InlineData(SpeedPolicy.LowSpeed, 6)]
        public void ConfirmationsRequired_NoLock_FollowsSpeedPolicy(SpeedPolicy policy, long expected)
        {
            var details = new ZanoPaymentData { BlockHeight = 13975, ConfirmationCount = 5, LockTime = 0 };
            Assert.Equal(expected, ZanoListener.ConfirmationsRequired(details, policy));
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void ConfirmationsRequired_StoreThreshold_OverridesSpeedPolicy()
        {
            var details = new ZanoPaymentData { BlockHeight = 13975, ConfirmationCount = 5, LockTime = 0, InvoiceSettledConfirmationThreshold = 3 };
            Assert.Equal(3, ZanoListener.ConfirmationsRequired(details, SpeedPolicy.LowSpeed));
        }

        // Regression for the bug where unlock_time was treated as a confirmation count:
        // a tx mined at height 13975 with the wallet's default unlock_time = blockHeight + 10
        // demanded 13980 confs and trapped invoices in Processing forever.
        [Trait("Category", "Unit")]
        [Fact]
        public void ConfirmationsRequired_UnlockTimeAsBlockHeight_RequiresLockExtraOnly()
        {
            var details = new ZanoPaymentData
            {
                BlockHeight = 13975,
                ConfirmationCount = 5,
                LockTime = 13985,
                InvoiceSettledConfirmationThreshold = 1
            };
            Assert.Equal(11, ZanoListener.ConfirmationsRequired(details, SpeedPolicy.MediumSpeed));
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void ConfirmationsRequired_UnlockTimeAlreadyPast_FallsBackToBaseRequirement()
        {
            var details = new ZanoPaymentData
            {
                BlockHeight = 13975,
                ConfirmationCount = 50,
                LockTime = 13900,
                InvoiceSettledConfirmationThreshold = 1
            };
            Assert.Equal(1, ZanoListener.ConfirmationsRequired(details, SpeedPolicy.MediumSpeed));
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void ConfirmationsRequired_UnlockTimeAsTimestamp_DoesNotInflateRequirement()
        {
            var details = new ZanoPaymentData
            {
                BlockHeight = 13975,
                ConfirmationCount = 5,
                LockTime = 1_700_000_000L,
                InvoiceSettledConfirmationThreshold = 1
            };
            Assert.Equal(1, ZanoListener.ConfirmationsRequired(details, SpeedPolicy.MediumSpeed));
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void ConfirmationsRequired_MempoolPayment_IgnoresLockExtra()
        {
            var details = new ZanoPaymentData
            {
                BlockHeight = 0,
                ConfirmationCount = 0,
                LockTime = 13985,
                InvoiceSettledConfirmationThreshold = 1
            };
            Assert.Equal(1, ZanoListener.ConfirmationsRequired(details, SpeedPolicy.MediumSpeed));
        }

        // Regression: a timestamp-form unlock_time must keep the invoice in Processing
        // until wall-clock time reaches the lock, even if the confirmation threshold is
        // already satisfied. ConfirmationsRequired stays at the base value (a count
        // can't express "wait until time T"); GetStatus does the time gate.
        [Trait("Category", "Unit")]
        [Fact]
        public void GetStatus_TimestampLockInFuture_StaysProcessing()
        {
            var details = new ZanoPaymentData
            {
                BlockHeight = 13975,
                ConfirmationCount = 5,
                LockTime = 1_700_000_000L,
                InvoiceSettledConfirmationThreshold = 1
            };
            Assert.False(ZanoListener.GetStatus(details, SpeedPolicy.MediumSpeed, nowUnixSeconds: 1_699_999_999L));
            Assert.True(ZanoListener.IsTimestampLocked(details, 1_699_999_999L));
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void GetStatus_TimestampLockInPast_SettlesOnConfirmations()
        {
            var details = new ZanoPaymentData
            {
                BlockHeight = 13975,
                ConfirmationCount = 5,
                LockTime = 1_700_000_000L,
                InvoiceSettledConfirmationThreshold = 1
            };
            Assert.True(ZanoListener.GetStatus(details, SpeedPolicy.MediumSpeed, nowUnixSeconds: 1_700_000_001L));
            Assert.False(ZanoListener.IsTimestampLocked(details, 1_700_000_001L));
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void GetStatus_NoLock_SettlesOnConfirmationThreshold()
        {
            var details = new ZanoPaymentData
            {
                BlockHeight = 13975,
                ConfirmationCount = 5,
                LockTime = 0,
                InvoiceSettledConfirmationThreshold = 1
            };
            Assert.True(ZanoListener.GetStatus(details, SpeedPolicy.MediumSpeed, nowUnixSeconds: 1_700_000_000L));
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void GetStatus_BlockHeightLockUnsatisfied_StaysProcessing()
        {
            var details = new ZanoPaymentData
            {
                BlockHeight = 13975,
                ConfirmationCount = 5,
                LockTime = 13985,
                InvoiceSettledConfirmationThreshold = 1
            };
            Assert.False(ZanoListener.GetStatus(details, SpeedPolicy.MediumSpeed, nowUnixSeconds: 1_700_000_000L));
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void IsTimestampLocked_BlockHeightLock_IsNotTimestampLock()
        {
            var details = new ZanoPaymentData { BlockHeight = 13975, LockTime = 13985 };
            Assert.False(ZanoListener.IsTimestampLocked(details, 0L));
        }

        // Regression: a single tx with two income subtransfers for the same
        // (asset_id, payment_id) used to be collapsed to one leg by the old dedupe
        // (.OrderByDescending(Height).First()), under-recording the payment. The
        // aggregator must sum amounts within the highest-height tier.
        [Trait("Category", "Unit")]
        [Fact]
        public void AggregateCandidates_SumsSameAssetSubtransfersInOneTx()
        {
            var input = new[]
            {
                (Amount: 200_000_000_000m, PaymentId: "pid1", AssetId: "assetA", TxHash: "tx1", Height: 13975L, UnlockTime: 0L),
                (Amount: 800_000_000_000m, PaymentId: "pid1", AssetId: "assetA", TxHash: "tx1", Height: 13975L, UnlockTime: 0L)
            };
            var result = ZanoListener.AggregateCandidates(input);
            Assert.Single(result);
            Assert.Equal(1_000_000_000_000m, result[0].Amount);
            Assert.Equal(13975L, result[0].Height);
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void AggregateCandidates_PrefersConfirmedOverMempool()
        {
            var input = new[]
            {
                (Amount: 1_000_000_000_000m, PaymentId: "pid1", AssetId: "assetA", TxHash: "tx1", Height: 0L, UnlockTime: 0L),
                (Amount: 1_000_000_000_000m, PaymentId: "pid1", AssetId: "assetA", TxHash: "tx1", Height: 13975L, UnlockTime: 0L)
            };
            var result = ZanoListener.AggregateCandidates(input);
            Assert.Single(result);
            Assert.Equal(1_000_000_000_000m, result[0].Amount);
            Assert.Equal(13975L, result[0].Height);
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void AggregateCandidates_KeepsDifferentAssetsSeparate()
        {
            var input = new[]
            {
                (Amount: 500_000_000_000m, PaymentId: "pid1", AssetId: "assetA", TxHash: "tx1", Height: 13975L, UnlockTime: 0L),
                (Amount: 700_000_000_000m, PaymentId: "pid1", AssetId: "assetB", TxHash: "tx1", Height: 13975L, UnlockTime: 0L)
            };
            var result = ZanoListener.AggregateCandidates(input);
            Assert.Equal(2, result.Count);
            Assert.Contains(result, r => r.AssetId == "assetA" && r.Amount == 500_000_000_000m);
            Assert.Contains(result, r => r.AssetId == "assetB" && r.Amount == 700_000_000_000m);
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void AggregateCandidates_KeepsDifferentPaymentIdsSeparate()
        {
            var input = new[]
            {
                (Amount: 500_000_000_000m, PaymentId: "pidA", AssetId: "assetA", TxHash: "tx1", Height: 13975L, UnlockTime: 0L),
                (Amount: 700_000_000_000m, PaymentId: "pidB", AssetId: "assetA", TxHash: "tx1", Height: 13975L, UnlockTime: 0L)
            };
            var result = ZanoListener.AggregateCandidates(input);
            Assert.Equal(2, result.Count);
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void AggregateCandidates_DropsLowerHeightTier()
        {
            var input = new[]
            {
                (Amount: 100m, PaymentId: "pid1", AssetId: "assetA", TxHash: "tx1", Height: 0L,    UnlockTime: 0L),
                (Amount: 100m, PaymentId: "pid1", AssetId: "assetA", TxHash: "tx1", Height: 13975L, UnlockTime: 0L),
                (Amount: 200m, PaymentId: "pid1", AssetId: "assetA", TxHash: "tx1", Height: 13975L, UnlockTime: 0L)
            };
            var result = ZanoListener.AggregateCandidates(input);
            Assert.Single(result);
            Assert.Equal(300m, result[0].Amount);
            Assert.Equal(13975L, result[0].Height);
        }

        // Regression: an 18-decimal CA payment of 10 units = 10^19 atomic, which exceeds
        // long.MaxValue (~9.22e18). The aggregator must accept and sum these without
        // overflow now that the candidate type is decimal.
        [Trait("Category", "Unit")]
        [Fact]
        public void AggregateCandidates_HandlesAmountAboveLongMaxValue()
        {
            var input = new[]
            {
                (Amount: 5_000_000_000_000_000_000m,  PaymentId: "pid1", AssetId: "assetA", TxHash: "tx1", Height: 13975L, UnlockTime: 0L),
                (Amount: 5_000_000_000_000_000_000m,  PaymentId: "pid1", AssetId: "assetA", TxHash: "tx1", Height: 13975L, UnlockTime: 0L)
            };
            var result = ZanoListener.AggregateCandidates(input);
            Assert.Single(result);
            Assert.Equal(10_000_000_000_000_000_000m, result[0].Amount);
        }

        // Page-overlap guard: the same confirmed tx returned on two adjacent pages (growing
        // history shifts the newest-first window) must collapse to ONE row, so
        // AggregateCandidates does not sum the duplicate and double-credit the payment.
        [Trait("Category", "Unit")]
        [Fact]
        public void DedupTransferRows_CollapsesDuplicateTxRows()
        {
            static ZanoTransfer Row() => new()
            {
                TxHash = "tx1",
                Height = 13975,
                SubtransfersByPid = new List<ZanoPaymentIdGroup>
                {
                    new() { PaymentId = "pid1", Subtransfers = new List<ZanoSubtransfer> { new() { Amount = 100m, AssetId = "a", IsIncome = true } } }
                }
            };
            var result = ZanoListener.DedupTransferRows(new[] { Row(), Row() });
            Assert.Single(result);
            Assert.Equal("tx1", result[0].TxHash);
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void DedupTransferRows_KeepsConfirmedOverMempoolForSameTx()
        {
            static ZanoTransfer Row(long h) => new() { TxHash = "tx1", Height = h, SubtransfersByPid = new List<ZanoPaymentIdGroup>() };
            var result = ZanoListener.DedupTransferRows(new[] { Row(0), Row(13975) });
            Assert.Single(result);
            Assert.Equal(13975L, result[0].Height);
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void DedupTransferRows_KeepsDistinctTxs()
        {
            static ZanoTransfer Row(string h) => new() { TxHash = h, Height = 1, SubtransfersByPid = new List<ZanoPaymentIdGroup>() };
            var result = ZanoListener.DedupTransferRows(new[] { Row("tx1"), Row("tx2") });
            Assert.Equal(2, result.Count);
        }

        // A mempool (BlockHeight==0) transfer carrying a future height-form unlock_time must
        // NOT settle even under HighSpeed/0-conf: the output is locked until a future block,
        // and ConfirmationsRequired can't express that while unconfirmed.
        [Trait("Category", "Unit")]
        [Fact]
        public void GetStatus_HeightLockedMempool_StaysProcessing()
        {
            var details = new ZanoPaymentData
            {
                BlockHeight = 0,
                ConfirmationCount = 0,
                LockTime = 13985,
                InvoiceSettledConfirmationThreshold = 0
            };
            Assert.True(ZanoListener.IsHeightLockedUnconfirmed(details));
            Assert.False(ZanoListener.GetStatus(details, SpeedPolicy.HighSpeed, nowUnixSeconds: 1_700_000_000L));
        }

        [Trait("Category", "Unit")]
        [Fact]
        public void IsHeightLockedUnconfirmed_ConfirmedOrTimestampOrNoLock_IsFalse()
        {
            Assert.False(ZanoListener.IsHeightLockedUnconfirmed(new ZanoPaymentData { BlockHeight = 13975, LockTime = 13985 }));       // confirmed
            Assert.False(ZanoListener.IsHeightLockedUnconfirmed(new ZanoPaymentData { BlockHeight = 0, LockTime = 0 }));               // no lock
            Assert.False(ZanoListener.IsHeightLockedUnconfirmed(new ZanoPaymentData { BlockHeight = 0, LockTime = 1_700_000_000L }));  // timestamp form
        }

        [Trait("Category", "Unit")]
        [Theory]
        [InlineData(PaymentStatus.Processing, 4, false)]
        [InlineData(PaymentStatus.Settled, 4, false)]
        [InlineData(PaymentStatus.Processing, 5, true)]
        [InlineData(PaymentStatus.Settled, 5, true)]
        [InlineData(PaymentStatus.Unaccounted, 5, false)]
        public void ShouldUnaccountMissingPayment_RequiresThresholdAndAccountedStatus(
            PaymentStatus status,
            int missingPollCount,
            bool expected)
        {
            Assert.Equal(expected, ZanoListener.ShouldUnaccountMissingPayment(status, missingPollCount));
        }

        [Trait("Category", "Unit")]
        [Theory]
        [InlineData(InvoiceStatus.Settled, InvoiceExceptionStatus.None, true)]
        [InlineData(InvoiceStatus.Settled, InvoiceExceptionStatus.PaidOver, true)]
        [InlineData(InvoiceStatus.Settled, InvoiceExceptionStatus.Marked, false)]
        [InlineData(InvoiceStatus.Processing, InvoiceExceptionStatus.None, false)]
        public void ShouldReopenSettledInvoice_PreservesManualCompletion(
            InvoiceStatus status,
            InvoiceExceptionStatus exceptionStatus,
            bool expected)
        {
            Assert.Equal(expected, ZanoListener.ShouldReopenSettledInvoice(status, exceptionStatus));
        }
    }
}
