using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaskbarQuota.Services;
using Windows.System;

namespace TaskbarQuota.Controls;

public enum UpdateActionBarPlacement
{
  Flyout,
  Settings,
}

public sealed partial class UpdateActionBar : UserControl
{
  private static readonly DependencyProperty PlacementProperty =
      DependencyProperty.Register(
          nameof(Placement),
          typeof(UpdateActionBarPlacement),
          typeof(UpdateActionBar),
          new PropertyMetadata(UpdateActionBarPlacement.Flyout, OnPlacementChanged));

  private readonly UpdateAvailabilityService _updates = UpdateAvailabilityService.Instance;

  public UpdateActionBarPlacement Placement
  {
    get => (UpdateActionBarPlacement)GetValue(PlacementProperty);
    set => SetValue(PlacementProperty, value);
  }

  public UpdateActionBar()
  {
    InitializeComponent();
    Visibility = Visibility.Collapsed;
    Loaded += OnLoaded;
    Unloaded += OnUnloaded;
  }

  private static void OnPlacementChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
  {
    if (d is UpdateActionBar bar)
      bar.ApplyAvailability();
  }

  private void OnLoaded(object sender, RoutedEventArgs e)
  {
    _updates.Changed += OnAvailabilityChanged;
    ApplyAvailability();
  }

  private void OnUnloaded(object sender, RoutedEventArgs e)
  {
    _updates.Changed -= OnAvailabilityChanged;
  }

  private void OnAvailabilityChanged() =>
      _ = DispatcherQueue.TryEnqueue(ApplyAvailability);

  private bool IsSettingsMode => Placement == UpdateActionBarPlacement.Settings;

  private void ApplyAvailability()
  {
    if (_updates.IsChecking)
    {
      if (IsSettingsMode)
        ApplySettingsChecking();
      else
        Visibility = Visibility.Collapsed;

      return;
    }

    if (!_updates.IsBannerVisible)
    {
      if (IsSettingsMode)
      {
        ApplySettingsIdle();
        return;
      }

      Visibility = Visibility.Collapsed;
      return;
    }

    Visibility = Visibility.Visible;
    ActionButton.ClearValue(StyleProperty);
    ActionButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];

    switch (_updates.UiState)
    {
      case UpdateAvailabilityUiState.UpdateAvailable:
        StatusText.Text = _updates.StatusMessage ?? "New update available!";
        DetailText.Visibility = Visibility.Collapsed;
        BusyRing.Visibility = Visibility.Collapsed;
        BusyRing.IsActive = false;
        DownloadProgress.Visibility = Visibility.Collapsed;
        ActionButton.Visibility = Visibility.Visible;
        ActionButton.IsEnabled = true;
        ActionButton.Content = _updates.AvailableUpdate?.DeliveryChannel == UpdateDeliveryChannel.MicrosoftStore
            ? "Open Store"
            : _updates.AvailableUpdate?.DownloadUrl is null
                ? "View release"
                : "Download update";
        break;

      case UpdateAvailabilityUiState.Downloading:
        StatusText.Text = _updates.StatusMessage ?? "Downloading update…";
        BusyRing.Visibility = Visibility.Collapsed;
        ActionButton.Visibility = Visibility.Collapsed;
        DownloadProgress.Visibility = Visibility.Visible;
        DownloadProgress.IsIndeterminate = true;
        break;

      case UpdateAvailabilityUiState.ReadyToInstall:
        StatusText.Text = _updates.StatusMessage ?? "New update available! Install now.";
        DetailText.Visibility = Visibility.Collapsed;
        BusyRing.Visibility = Visibility.Collapsed;
        DownloadProgress.Visibility = Visibility.Collapsed;
        ActionButton.Visibility = Visibility.Visible;
        ActionButton.IsEnabled = true;
        ActionButton.Content = "Install update";
        break;

      default:
        if (IsSettingsMode)
          ApplySettingsIdle();
        else
          Visibility = Visibility.Collapsed;

        break;
    }
  }

  private void ApplySettingsIdle()
  {
    Visibility = Visibility.Visible;
    StatusText.Text = _updates.UpToDateSummary
        ?? "Check for updates on GitHub when you are ready.";
    DetailText.Visibility = Visibility.Collapsed;
    BusyRing.Visibility = Visibility.Collapsed;
    BusyRing.IsActive = false;
    DownloadProgress.Visibility = Visibility.Collapsed;
    ActionButton.Visibility = Visibility.Visible;
    ActionButton.IsEnabled = true;
    ActionButton.Content = "Check for updates";
    ActionButton.ClearValue(StyleProperty);
  }

  private void ApplySettingsChecking()
  {
    Visibility = Visibility.Visible;
    StatusText.Text = "Checking for updates…";
    DetailText.Visibility = Visibility.Collapsed;
    BusyRing.Visibility = Visibility.Visible;
    BusyRing.IsActive = true;
    DownloadProgress.Visibility = Visibility.Collapsed;
    ActionButton.Visibility = Visibility.Collapsed;
  }

  private async void ActionButton_Click(object sender, RoutedEventArgs e)
  {
    if (IsSettingsMode && !_updates.IsBannerVisible && !_updates.IsChecking)
    {
      await _updates.CheckManuallyAsync();
      return;
    }

    switch (_updates.UiState)
    {
      case UpdateAvailabilityUiState.UpdateAvailable:
        if (_updates.AvailableUpdate?.DeliveryChannel == UpdateDeliveryChannel.MicrosoftStore
            && _updates.AvailableUpdate.ReleaseUrl is { } storeUrl)
        {
          _ = Launcher.LaunchUriAsync(storeUrl);
        }
        else if (_updates.AvailableUpdate?.DownloadUrl is null
            && _updates.AvailableUpdate?.ReleaseUrl is { } releaseUrl)
        {
          _ = Launcher.LaunchUriAsync(releaseUrl);
        }
        else
        {
          await DownloadAsync();
        }

        break;

      case UpdateAvailabilityUiState.ReadyToInstall:
        _updates.InstallAndQuit();
        break;
    }
  }

  private async Task DownloadAsync()
  {
    var progress = new Progress<UpdateDownloadProgress>(report =>
    {
      _ = DispatcherQueue.TryEnqueue(() => ReportDownload(report));
    });

    await _updates.DownloadAsync(progress);
  }

  private void ReportDownload(UpdateDownloadProgress progress)
  {
    if (progress.TotalBytes is > 0)
    {
      DownloadProgress.IsIndeterminate = false;
      DownloadProgress.Value = progress.Percent;
      DetailText.Text =
          $"{FormatByteSize(progress.BytesReceived)} / {FormatByteSize(progress.TotalBytes.Value)} ({progress.Percent:0}%)";
      DetailText.Visibility = Visibility.Visible;
    }
    else
    {
      DownloadProgress.IsIndeterminate = true;
      DetailText.Text = $"{FormatByteSize(progress.BytesReceived)} downloaded";
      DetailText.Visibility = Visibility.Visible;
    }
  }

  private static string FormatByteSize(long bytes)
  {
    if (bytes < 1_024)
      return $"{bytes} B";

    var size = bytes / 1024.0;
    if (size < 1_024)
      return $"{size:0.#} KB";

    return $"{size / 1024.0:0.#} MB";
  }
}
