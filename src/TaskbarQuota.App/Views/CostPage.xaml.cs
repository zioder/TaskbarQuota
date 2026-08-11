using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using TaskbarQuota.ViewModels;

namespace TaskbarQuota.Views
{
    public sealed partial class CostPage : Page
    {
        public DashboardViewModel ViewModel { get; }

        public CostPage()
        {
            ViewModel = DashboardPage.SharedViewModel ?? new DashboardViewModel(DispatcherQueue);
            InitializeComponent();
            Loaded += CostPage_Loaded;
        }

        private void CostPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= CostPage_Loaded;
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => _ = ViewModel.LoadHistoryAsync());
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
        }
    }
}
