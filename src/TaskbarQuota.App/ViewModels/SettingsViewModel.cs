using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TaskbarQuota.Usage;

namespace TaskbarQuota.ViewModels
{
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
    }
}
