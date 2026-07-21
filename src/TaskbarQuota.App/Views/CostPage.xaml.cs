using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TaskbarQuota.Cost;
using TaskbarQuota.Diagnostics;
using TaskbarQuota.Services;
using TaskbarQuota.ViewModels;

namespace TaskbarQuota.Views
{
    public sealed partial class CostPage : Page
    {
        public CostViewModel ViewModel { get; }

        public CostPage()
        {
            ViewModel = new CostViewModel(DispatcherQueue);
            InitializeComponent();
            ViewModel.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(CostViewModel.IsLoading)
                    or nameof(CostViewModel.IsEmpty)
                    or nameof(CostViewModel.Segments))
                    UpdateStates();
            };
            ViewModel.Segments.CollectionChanged += (_, _) => UpdateStates();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            UpdateStates();
            await ViewModel.LoadAsync();
        }

        private void UpdateStates()
        {
            Spinner.IsActive = ViewModel.IsLoading;
            bool showEmpty = !ViewModel.IsLoading && ViewModel.IsEmpty;
            bool showContent = !ViewModel.IsLoading && !ViewModel.IsEmpty && ViewModel.Segments.Count > 0;
            EmptyState.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
            Content.Visibility = showContent ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnRangeChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
        {
            if (sender.SelectedItem?.Tag is string tag && Enum.TryParse<CostRange>(tag, out var range))
                ViewModel.SelectedRange = range;
        }

        private async void OnShareClick(object sender, RoutedEventArgs e)
        {
            try
            {
                ShareButton.IsEnabled = false;
                await CostShareHelper.ShareAsync(ShareCard, App.MainWindowHandle, $"TaskbarQuota-Cost-{ViewModel.SelectedRange}");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Cost share failed");
            }
            finally
            {
                ShareButton.IsEnabled = true;
            }
        }
    }
}
