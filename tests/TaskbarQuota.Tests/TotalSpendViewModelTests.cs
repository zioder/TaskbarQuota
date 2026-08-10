using System;
using TaskbarQuota.Controls;
using TaskbarQuota.Usage;
using TaskbarQuota.ViewModels;
using Xunit;

namespace TaskbarQuota.Tests
{
    public sealed class TotalSpendViewModelTests
    {
        [Fact]
        public void LiveHistoryChanges_DoNotRefreshPopulatedSnapshot()
        {
            Assert.False(TotalSpendViewModel.ShouldRefreshSnapshot(1, 2, true, false));
        }

        [Fact]
        public void SurfaceState_ShowsLoadingUntilDataArrives()
        {
            var vm = new TotalSpendViewModel();
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, vm.IsLoadingVisibility);

            vm.IsLoading = true;
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, vm.IsLoadingVisibility);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, vm.EmptyVisibility);

            vm.IsLoading = false;
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, vm.IsLoadingVisibility);
            Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, vm.EmptyVisibility);
        }

        [Fact]
        public void CompletedRefresh_ForcesLatestSnapshotImmediately()
        {
            Assert.True(TotalSpendViewModel.ShouldRefreshSnapshot(1, 2, true, true));
        }

        [Fact]
        public void IdenticalHistory_DoesNotRefreshAfterInterval()
        {
            Assert.False(TotalSpendViewModel.ShouldRefreshSnapshot(7, 7, true, false));
        }

        [Fact]
        public void InitialHistory_IsShownWithoutWaiting()
        {
            Assert.True(TotalSpendViewModel.ShouldRefreshSnapshot(null, 1, false, false));
        }

        [Fact]
        public void OneDayPeriod_UsesTodayInsteadOfRollingHistory()
        {
            var today = new UsagePeriod(100, 1.25);
            var history = new UsageHistory
            {
                Today = today,
                Last7Days = new UsagePeriod(700, 8.75),
                Last30Days = new UsagePeriod(3_000, 37.50),
                Last90Days = new UsagePeriod(9_000, 112.50),
            };

            Assert.Equal(1, TotalSpendViewModel.DaysInPeriod("1"));
            Assert.Same(today, TotalSpendViewModel.SelectPeriod(history, "1"));
        }

        [Fact]
        public void SpendRingNormalizesTinyProvidersWithoutChangingTheTotalCircle()
        {
            var arcs = SpendRingLayout.BuildArcs(new[] { 99d, 1d });

            Assert.Equal(2, arcs.Count);
            Assert.Equal(0, arcs[0].Start, 8);
            Assert.Equal(1, arcs[^1].End, 8);
            Assert.True(arcs[1].End - arcs[1].Start >= 0.02);
        }

        [Theory]
        [InlineData(1_250, "1.3K")]
        [InlineData(35_400_000, "35.4M")]
        [InlineData(1_300_000_000, "1.3B")]
        public void RingCenterUsesCompactReadableTotals(double value, string expected)
            => Assert.Equal(expected, TotalSpendViewModel.FormatCompactValue(value));
    }
}
