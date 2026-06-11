using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskbarQuota.Usage;

namespace TaskbarQuota.ViewModels
{
    public sealed partial class ProviderSettingItemViewModel : ObservableObject
    {
        public ProviderId Id { get; }
        public string DisplayName { get; }

        [ObservableProperty] public partial bool IsDashboardVisible { get; set; }
        [ObservableProperty] public partial bool IsWidgetVisible { get; set; }

        public string StatusText { get; private set; }

        public ProviderSettingItemViewModel(ProviderId id, string displayName, bool dashboardVisible, bool widgetVisible, string statusText)
        {
            Id = id;
            DisplayName = displayName;
            IsDashboardVisible = dashboardVisible;
            IsWidgetVisible = widgetVisible;
            StatusText = statusText;
        }

        public void UpdateStatus(string statusText)
        {
            StatusText = statusText;
            OnPropertyChanged(nameof(StatusText));
        }
    }
    public enum CredentialKind { ApiKey, Cookie }

    public sealed partial class ProviderCredentialViewModel : ObservableObject
    {
        private readonly ProviderId _id;

        public string Title { get; }
        public string Description { get; }
        public CredentialKind Kind { get; }
        public string ApiKeyHeader { get; }

        [ObservableProperty] public partial string ApiKey { get; set; }
        [ObservableProperty] public partial string CookieHeader { get; set; }
        [ObservableProperty] public partial bool Saved { get; set; }

        public bool IsApiKey => Kind == CredentialKind.ApiKey;
        public bool IsCookie => Kind == CredentialKind.Cookie;

        public Microsoft.UI.Xaml.Visibility ApiKeyVisibility => IsApiKey ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        public Microsoft.UI.Xaml.Visibility CookieVisibility => IsCookie ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
        public Microsoft.UI.Xaml.Visibility SavedVisibility => Saved ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

        partial void OnSavedChanged(bool value) => OnPropertyChanged(nameof(SavedVisibility));

        public ProviderCredentialViewModel(ProviderId id, string title, string description, CredentialKind kind,
            string apiKeyHeader = "")
        {
            _id = id;
            Title = title;
            Description = description;
            Kind = kind;
            ApiKeyHeader = apiKeyHeader;

            var e = CredentialStore.Instance.For(id);
            ApiKey = e.ApiKey ?? "";
            CookieHeader = e.CookieHeader ?? "";
        }

        [RelayCommand]
        private void Save()
        {
            var e = CredentialStore.Instance.For(_id);
            e.ApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim();
            e.CookieHeader = string.IsNullOrWhiteSpace(CookieHeader) ? null : CookieHeader.Trim();
            CredentialStore.Instance.Save();
            Saved = true;
        }
    }

    public sealed partial class SettingsViewModel : ObservableObject
    {
        public ObservableCollection<ProviderSettingItemViewModel> Providers { get; } = new();

        public void ReloadProviders()
        {
            Providers.Clear();
            foreach (var provider in UsageCoordinator.Instance.Service.All)
            {
                string status = ProviderDiscoveryService.IsConfigured(provider.Id)
                    ? "Configured"
                    : ProviderDiscoveryService.IsProbed(provider.Id)
                        ? ProviderInstallDetector.IsInstalled(provider.Id)
                            ? "Waiting for app"
                            : "Not set up"
                        : "Unknown";

                Providers.Add(new ProviderSettingItemViewModel(
                    provider.Id,
                    provider.DisplayName,
                    WidgetSettingsService.IsProviderDashboardVisible(provider.Id),
                    WidgetSettingsService.IsProviderVisible(provider.Id),
                    status));
            }
        }

        public void ApplyDashboardVisibility(ProviderSettingItemViewModel item, bool visible)
        {
            if (visible)
                ProviderDiscoveryService.EnableProvider(item.Id);
            else
                ProviderDiscoveryService.DisableProvider(item.Id);

            item.IsDashboardVisible = WidgetSettingsService.IsProviderDashboardVisible(item.Id);
        }

        public void ApplyWidgetVisibility(ProviderSettingItemViewModel item, bool visible)
        {
            WidgetSettingsService.SetProviderVisible(item.Id, visible);
            item.IsWidgetVisible = WidgetSettingsService.IsProviderVisible(item.Id);
        }
    }
}
