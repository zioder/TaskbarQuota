using System;
using System.Collections.Specialized;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TaskbarQuota.Usage;
using TaskbarQuota.ViewModels;

namespace TaskbarQuota
{
    internal sealed class DashboardNavigationBinder
    {
        private readonly NavigationView _nav;
        private readonly DashboardViewModel _viewModel;
        private readonly DispatcherQueueTimer _rebuildTimer;
        private bool _rebuildPending;

        public bool IsSyncing { get; private set; }

        public DashboardNavigationBinder(NavigationView nav, DashboardViewModel viewModel)
        {
            _nav = nav;
            _viewModel = viewModel;
            _rebuildTimer = nav.DispatcherQueue.CreateTimer();
            _rebuildTimer.Interval = TimeSpan.FromMilliseconds(50);
            _rebuildTimer.Tick += (_, _) =>
            {
                _rebuildTimer.Stop();
                _rebuildPending = false;
                Rebuild();
            };
            _viewModel.Cards.CollectionChanged += Cards_CollectionChanged;
            _viewModel.SelectedCardChanged += ViewModel_SelectedCardChanged;
            Rebuild();
        }

        /// <summary>
        /// Re-applies the selected item and its icon brush. Call once the NavigationView is loaded —
        /// selection set during construction (before the control realizes its containers) doesn't
        /// paint the active item's icon on first show.
        /// </summary>
        public void ReapplySelection()
        {
            IsSyncing = true;
            SyncSelection();
            IsSyncing = false;
        }

        public bool SelectFromNavigation(NavigationViewSelectionChangedEventArgs args)
        {
            if (IsSyncing)
                return true;

            if (args.SelectedItemContainer is not NavigationViewItem { Tag: ProviderId id })
                return false;

            _viewModel.SelectProvider(id);
            return true;
        }

        private void Cards_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Quota refreshes replace card VMs in place; rebuilding nav recreates PathIcons and crashes WinUI.
            if (e.Action == NotifyCollectionChangedAction.Replace)
                return;

            ScheduleRebuild();
        }

        private void ScheduleRebuild()
        {
            if (_rebuildPending)
                return;

            _rebuildPending = true;
            _rebuildTimer.Stop();
            _rebuildTimer.Start();
        }

        private void Rebuild()
        {
            IsSyncing = true;
            bool iconOnly = _nav.PaneDisplayMode == NavigationViewPaneDisplayMode.Top;
            _nav.MenuItems.Clear();
            foreach (var card in _viewModel.Cards)
            {
                var item = new NavigationViewItem
                {
                    Content = iconOnly ? null : card.DisplayName,
                    Tag = card.ProviderId,
                    Icon = CreateProviderIcon(card.ProviderId),
                    HorizontalAlignment = iconOnly ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
                };
                ToolTipService.SetToolTip(item, card.DisplayName);
                _nav.MenuItems.Add(item);
            }

            SyncSelection();
            IsSyncing = false;
        }

        private void ViewModel_SelectedCardChanged(ProviderCardViewModel? card)
        {
            if (card is null || IsSyncing)
                return;

            IsSyncing = true;
            SyncSelection();
            IsSyncing = false;
        }

        private void SyncSelection()
        {
            var selected = _viewModel.SelectedCard?.ProviderId;
            foreach (var item in _nav.MenuItems)
            {
                if (item is not NavigationViewItem navItem)
                    continue;

                bool isSelected = selected is ProviderId id
                    && navItem.Tag is ProviderId tag
                    && id == tag;

                if (isSelected)
                    _nav.SelectedItem = navItem;

                SetActiveVisual(navItem, isSelected);
            }
        }

        private static IconElement CreateProviderIcon(ProviderId id)
        {
            var brush = GetSelectionBrush(isSelected: false);
            if (ProviderGlyphs.Data.TryGetValue(id, out var pathData)
                && Ui.ParseFreshGeometry(pathData) is { } geometry)
            {
                return new PathIcon
                {
                    Data = geometry,
                    Foreground = brush,
                };
            }

            return new FontIcon { Glyph = "\uE8A5", FontSize = 16, Foreground = brush };
        }

        private static void SetActiveVisual(NavigationViewItem item, bool isSelected)
        {
            var brush = GetSelectionBrush(isSelected);
            item.Foreground = brush;
            ApplyIconBrush(item.Icon, isSelected);
        }

        private static void ApplyIconBrush(IconElement? icon, bool isSelected)
        {
            if (icon is null)
                return;

            var brush = GetSelectionBrush(isSelected);
            if (icon is FontIcon fontIcon)
                fontIcon.Foreground = brush;
            else if (icon is PathIcon pathIcon)
                pathIcon.Foreground = brush;
        }

        private static Brush GetSelectionBrush(bool isSelected) => isSelected
            ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
            : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    }
}
