using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TaskbarQuota.Services;
using Windows.System;

namespace TaskbarQuota.Controls;

/// <summary>Flyout nudge asking for a GitHub star; yields to the update bar when one is showing.</summary>
public sealed partial class StarPromptBar : UserControl
{
  private readonly UpdateAvailabilityService _updates = UpdateAvailabilityService.Instance;
  private bool _thanking;
  private static bool _remoteChecked;

  public StarPromptBar()
  {
    InitializeComponent();
    Visibility = Visibility.Collapsed;
    Loaded += OnLoaded;
    Unloaded += OnUnloaded;
  }

  private void OnLoaded(object sender, RoutedEventArgs e)
  {
    _updates.Changed += OnStateChanged;
    GitHubStarService.Changed += OnStateChanged;
    ApplyVisibility();
  }

  private void OnUnloaded(object sender, RoutedEventArgs e)
  {
    _updates.Changed -= OnStateChanged;
    GitHubStarService.Changed -= OnStateChanged;
  }

  private void OnStateChanged() =>
      _ = DispatcherQueue.TryEnqueue(ApplyVisibility);

  private async void ApplyVisibility()
  {
    if (_thanking)
      return;

    if (_updates.IsBannerVisible || !GitHubStarService.ShouldPrompt())
    {
      Visibility = Visibility.Collapsed;
      return;
    }

    // Ask GitHub once per session so an account that already starred (e.g. on github.com)
    // never sees the prompt. A star found here marks it locally and raises Changed.
    if (!_remoteChecked)
    {
      _remoteChecked = true;
      if (await GitHubStarService.IsStarredAsync() == true)
      {
        Visibility = Visibility.Collapsed;
        return;
      }
      if (_updates.IsBannerVisible || !GitHubStarService.ShouldPrompt())
        return;
    }

    Visibility = Visibility.Visible;
  }

  private async void StarButton_Click(object sender, RoutedEventArgs e)
  {
    StarButton.IsEnabled = false;
    DismissButton.IsEnabled = false;
    StarButton.Content = "Starring…";

    var result = await GitHubStarService.StarAsync();
    if (result == GitHubStarResult.Starred)
    {
      // Hold a short thank-you before the bar goes away.
      _thanking = true;
      StatusText.Text = "Thanks for the star!";
      StarButton.Visibility = Visibility.Collapsed;
      DismissButton.Visibility = Visibility.Collapsed;
      await Task.Delay(TimeSpan.FromSeconds(2.5));
      _thanking = false;
      ApplyVisibility();
      return;
    }

    // No signed-in gh CLI: open the repo so the user can star it there, and stop asking.
    _ = Launcher.LaunchUriAsync(GitHubStarService.RepoUri);
    GitHubStarService.DismissPrompt();
  }

  private void DismissButton_Click(object sender, RoutedEventArgs e) =>
      GitHubStarService.DismissPrompt();
}
