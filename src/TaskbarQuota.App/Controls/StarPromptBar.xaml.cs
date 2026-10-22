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
    if (_thanking)
      return;

    // Set before the call: a successful star raises Changed, which must not hide the bar mid-thanks.
    _thanking = true;
    DismissButton.IsEnabled = false;
    StarButtonText.Text = "Starring…";

    var result = await GitHubStarService.StarAsync();
    if (result == GitHubStarResult.Starred)
    {
      StatusText.Text = "Thanks for the star!";
      StarButtonText.Text = "Starred";
      DismissButton.Visibility = Visibility.Collapsed;
      FillStarStoryboard.Begin();
      await Task.Delay(TimeSpan.FromSeconds(2.5));

      FadeOutStoryboard.Begin();
      await Task.Delay(TimeSpan.FromMilliseconds(300));
      Visibility = Visibility.Collapsed;
      return;
    }

    // No signed-in gh CLI: open the repo so the user can star it there, and stop asking.
    _thanking = false;
    _ = Launcher.LaunchUriAsync(GitHubStarService.RepoUri);
    GitHubStarService.DismissPrompt();
  }

  private void DismissButton_Click(object sender, RoutedEventArgs e) =>
      GitHubStarService.DismissPrompt();
}
