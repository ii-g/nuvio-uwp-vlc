using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nuvio.Bridge;
using Nuvio.Core;
using Nuvio.Diagnostics;
using Nuvio.Input;
using Nuvio.Models;
using Nuvio.Services;
using Nuvio.ViewModels;
using Windows.ApplicationModel.Resources;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Nuvio.Views
{
    /// <summary>One root-list row: section id, label key/fallback, description key/fallback.</summary>
    internal sealed class SettingsRootRow
    {
        public string Id { get; set; }
        public string LabelKey { get; set; }
        public string LabelFallback { get; set; }
        public string DescriptionKey { get; set; }
        public string DescriptionFallback { get; set; }
    }

    /// <summary>
    /// Settings (Milestone 6, Addons + Debrid only): root list, the Addons section
    /// and the Debrid section. Ported from web/js/views/settings.js (settingsView /
    /// addonsPage / debridPage). The ViewModel owns state and operations; this class
    /// renders rows and addon cards imperatively so focus can be restored by key
    /// (transportUrl|action) across re-renders. No custom input handling: the
    /// router owns the D-pad and back is the frame's GoBack.
    /// </summary>
    internal sealed partial class SettingsPage : FocusablePage
    {
        private readonly SettingsViewModel _viewModel;
        private readonly ResourceLoader _strings = ResourceLoader.GetForViewIndependentUse("Resources");
        private string _section = string.Empty;
        private Button _firstFocusTarget;

        public SettingsPage()
        {
            InitializeComponent();
            NavigationCacheMode = Windows.UI.Xaml.Navigation.NavigationCacheMode.Disabled;
            var shell = MainPage.Current;
            _viewModel = new SettingsViewModel(shell.Stremio, shell.Debrid, shell.Settings, shell.Navigation);
            ApplyFlowDirection(AppSettings.EffectiveIsRightToLeft());
            Loaded += (s, e) => ApplyFlowDirection(AppSettings.EffectiveIsRightToLeft());
            _viewModel.AddonsChanged += OnViewModelAddonsChanged;
            _viewModel.DebridChanged += OnViewModelDebridChanged;
        }

        private void ApplyFlowDirection(bool rtl)
        {
            FlowDirection = rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
            // Page padding comes from the shared ContentPagePadding
            // (content-start + visual anchor, 28 top) — do not hardcode it here.
            ContentStack.Padding = FlowDirectionToThicknessConverter.ContentPagePadding(rtl);
        }

        protected override void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ApplyFlowDirection(AppSettings.EffectiveIsRightToLeft());
            var param = e.Parameter as SettingsNavigation;
            _section = param != null && !string.IsNullOrEmpty(param.Section)
                ? param.Section.ToLowerInvariant()
                : string.Empty;
            ShowSection();
            // Sync page: ShowSection builds the rows imperatively, so the
            // arrival target (_firstFocusTarget) exists now.
            RaiseContentReady();
            // Arrival focus (fresh or back) is owned by the router's
            // SettleArrivalFocus, which calls TryFocusArrival / TryFocusKey on
            // this page — nothing focuses here.
        }

        protected override void OnNavigatedFrom(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _viewModel.AddonsChanged -= OnViewModelAddonsChanged;
            _viewModel.DebridChanged -= OnViewModelDebridChanged;
            _viewModel.Close();
        }

        // ---------- Arrival focus (IArrivalFocus, router-owned) ----------

        /// <summary>Fresh-arrival target: _firstFocusTarget (first root row,
        /// Install Addon, or the first debrid row). Called by the router's
        /// SettleArrivalFocus, which retries when this returns false (the rows
        /// are not focusable until layout runs). Bails out when focus is
        /// already inside the page, so a late settle never yanks focus away.</summary>
        public override bool TryFocusArrival()
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused != null && IsDescendantOf(focused, this)) return true;
            var target = _firstFocusTarget;
            return target != null && target.Focus(FocusState.Programmatic);
        }

        /// <summary>Back-arrival target: the root row or section control for a
        /// recorded key. Returns false when the key is gone so the router falls
        /// back to TryFocusArrival. Keys are the control's Tag verbatim: root
        /// rows "root|&lt;id&gt;" (a bare row id is accepted too), addon actions
        /// "&lt;manifestUrl&gt;|&lt;action&gt;" (the existing re-render key — a
        /// manifest URL, not a stream URL or secret), debrid rows
        /// "debrid|&lt;providerId&gt;" / "debrid|disconnect", and the addons
        /// section chrome "addons|install" / "addons|reset".</summary>
        public override bool TryFocusKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            if (RootRows.Any(r => r.Id == key)) key = "root|" + key;
            if (key.StartsWith("root|", StringComparison.Ordinal))
            {
                // Root rows only exist on the root list; in a section the key is gone.
                if (_section != string.Empty) return false;
                var rootButton = FindButtonByTag(RootPanel, key);
                return rootButton != null && rootButton.Focus(FocusState.Programmatic);
            }
            if (_section == "addons")
            {
                var addonButton = FindButtonByTag(AddonsHost, key);
                if (addonButton != null && addonButton.IsEnabled) return addonButton.Focus(FocusState.Programmatic);
                if (key == "addons|install") return InstallAddonButton.Focus(FocusState.Programmatic);
                if (key == "addons|reset") return ResetAddonsButton.Focus(FocusState.Programmatic);
                return false;
            }
            if (_section == "debrid")
            {
                var debridButton = FindButtonByTag(DebridHost, key);
                return debridButton != null && debridButton.Focus(FocusState.Programmatic);
            }
            return false;
        }

        private string S(string key, string fallback)
        {
            var text = _strings.GetString(key);
            return string.IsNullOrEmpty(text) ? fallback : text;
        }

        private void OnViewModelAddonsChanged(object sender, EventArgs e)
        {
            if (_section == "addons") RenderAddons();
        }

        private void OnViewModelDebridChanged(object sender, EventArgs e)
        {
            if (_section == "debrid") RenderDebrid();
        }

        // ---------- Sections ----------

        private void ShowSection()
        {
            RootPanel.Visibility = Visibility.Collapsed;
            AddonsPanel.Visibility = Visibility.Collapsed;
            DebridPanel.Visibility = Visibility.Collapsed;
            _firstFocusTarget = null;
            if (_section == "addons")
            {
                PageTitle.Text = S("SettingsAddonsTitle", "Addons");
                InstallAddonButton.Content = S("SettingsInstallAddon", "Install Addon");
                ResetAddonsButton.Content = S("SettingsResetToDefaults", "Reset to Defaults");
                AddonsHint.Text = S("SettingsAddonsHint", "Paste an addon's manifest URL (from its configure page). Debrid addons such as Torrentio, Comet, MediaFusion or AIOStreams set up with Real-Debrid, AllDebrid, Premiumize or TorBox give direct links that play on this device.");
                AddonsPanel.Visibility = Visibility.Visible;
                RenderAddons();
                _firstFocusTarget = InstallAddonButton;
                // Section-chrome keys for Back-arrival restore (see TryFocusKey).
                InstallAddonButton.Tag = "addons|install";
                ResetAddonsButton.Tag = "addons|reset";
            }
            else if (_section == "debrid")
            {
                PageTitle.Text = S("SettingsDebridTitle", "Debrid");
                DebridHint.Text = S("SettingsDebridHint", "Addons that already include your debrid service (Torrentio, Comet, AIOStreams...) work without this. Connect a service here to also play plain torrent results: the service fetches the torrent and this device plays the file it serves. Only results already cached by the service play straight away.");
                DebridPanel.Visibility = Visibility.Visible;
                RenderDebrid();
                _firstFocusTarget = FirstButtonIn(DebridHost);
            }
            else
            {
                // Unknown section falls back to the root list.
                _section = string.Empty;
                PageTitle.Text = S("SettingsTitle", "Settings");
                RootPanel.Visibility = Visibility.Visible;
                RenderRootRows();
                _firstFocusTarget = FirstButtonIn(RootPanel);
            }
        }

        // ---------- Root list ----------

        private static readonly List<SettingsRootRow> RootRows = new List<SettingsRootRow>
        {
            new SettingsRootRow { Id = "account", LabelKey = "SettingsAccount", LabelFallback = "Account", DescriptionKey = "SettingsAccountDescription", DescriptionFallback = "Sign in to sync your addons, library and progress" },
            new SettingsRootRow { Id = "addons", LabelKey = "SettingsAddons", LabelFallback = "Addons", DescriptionKey = "SettingsAddonsDescription", DescriptionFallback = "Install, remove, refresh, and sort your content sources" },
            new SettingsRootRow { Id = "debrid", LabelKey = "SettingsDebrid", LabelFallback = "Debrid", DescriptionKey = "SettingsDebridDescription", DescriptionFallback = "Play torrent results through Real-Debrid, TorBox or Premiumize" },
            new SettingsRootRow { Id = "playback", LabelKey = "SettingsPlayback", LabelFallback = "Playback", DescriptionKey = "SettingsPlaybackDescription", DescriptionFallback = "Languages, resume, next episode, streams" },
            new SettingsRootRow { Id = "subtitles", LabelKey = "SettingsSubtitles", LabelFallback = "Subtitles", DescriptionKey = "SettingsSubtitlesDescription", DescriptionFallback = "Size, colour and position of addon subtitles" },
            new SettingsRootRow { Id = "appearance", LabelKey = "SettingsAppearance", LabelFallback = "Appearance", DescriptionKey = "SettingsAppearanceDescription", DescriptionFallback = "Home layout and theme" },
            new SettingsRootRow { Id = "integrations", LabelKey = "SettingsIntegrations", LabelFallback = "Integrations", DescriptionKey = "SettingsIntegrationsDescription", DescriptionFallback = "Trakt, Jellyfin and Emby servers, TMDB" },
            new SettingsRootRow { Id = "device", LabelKey = "SettingsDevice", LabelFallback = "Device", DescriptionKey = "SettingsDeviceDescription", DescriptionFallback = "Controller and app options" },
            new SettingsRootRow { Id = "about", LabelKey = "SettingsAbout", LabelFallback = "About", DescriptionKey = "SettingsAboutDescription", DescriptionFallback = "Versions and licences" },
        };

        private void RenderRootRows()
        {
            RootPanel.Children.Clear();
            foreach (var row in RootRows)
            {
                var label = S(row.LabelKey, row.LabelFallback);
                var description = S(row.DescriptionKey, row.DescriptionFallback);
                var button = BuildRowButton(label, description, "›");
                button.Tag = "root|" + row.Id;
                button.Click += RootRow_Click;
                RootPanel.Children.Add(button);
            }
        }

        private void RootRow_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null) return;
            var tag = button.Tag as string;
            if (string.IsNullOrEmpty(tag) || !tag.StartsWith("root|", StringComparison.Ordinal)) return;
            var id = tag.Substring("root|".Length);
            if (id == "addons" || id == "debrid")
            {
                _viewModel.Navigation.NavigateSettings(id);
                return;
            }
            var row = RootRows.FirstOrDefault(r => r.Id == id);
            var label = row != null ? S(row.LabelKey, row.LabelFallback) : id;
            // Temporary placeholder message, following the existing literal pattern.
            _viewModel.Navigation.NavigatePlaceholder(label, label + " settings land in Milestone 6.", "Milestone 6", "settings");
        }

        /// <summary>A full-width row button: label + description stacked, value/chevron at the end.</summary>
        private Button BuildRowButton(string label, string description, string endText)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnSpacing = 12;
            var text = new StackPanel { Spacing = 2 };
            Grid.SetColumn(text, 0);
            text.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 11,
                FontWeight = Windows.UI.Text.FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
            });
            if (!string.IsNullOrEmpty(description))
            {
                text.Children.Add(new TextBlock
                {
                    Text = description,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)),
                    FontSize = 8,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            grid.Children.Add(text);
            if (!string.IsNullOrEmpty(endText))
            {
                var end = new TextBlock
                {
                    Text = endText,
                    Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                    FontSize = 11,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(end, 1);
                grid.Children.Add(end);
            }
            return new Button
            {
                Style = (Style)Resources["SettingsRowButtonStyle"],
                Content = grid,
            };
        }

        // ---------- Addons ----------

        private void RenderAddons()
        {
            var focusedKey = FocusedAddonActionKey();
            AddonsHost.Children.Clear();
            var addons = _viewModel.Addons;
            if (addons.Count == 0)
            {
                AddonsEmptyState.Visibility = Visibility.Visible;
                AddonsEmptyTitle.Text = S("SettingsNoAddonsTitle", "No addons installed yet.");
                AddonsEmptyMessage.Text = S("SettingsNoAddonsMessage", "Add a manifest URL to start loading catalogs, metadata, streams or subtitles into Nuvio.");
            }
            else
            {
                AddonsEmptyState.Visibility = Visibility.Collapsed;
                for (var i = 0; i < addons.Count; i++)
                {
                    AddonsHost.Children.Add(BuildAddonCard(addons[i], i, addons.Count));
                }
            }
            if (!string.IsNullOrEmpty(focusedKey))
            {
                RestoreAddonActionFocus(focusedKey);
            }
        }

        private Border BuildAddonCard(Addon addon, int index, int count)
        {
            var manifest = addon.Manifest;
            var name = StremioService.AddonName(addon);
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x8C, 0x0D, 0x0E, 0x13)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 12),
                Opacity = addon.Enabled == false ? 0.6 : 1.0,
            };
            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            card.Child = layout;

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnSpacing = 12;
            Grid.SetRow(head, 0);
            layout.Children.Add(head);

            // Logo (48x48) with a plain initial-letter placeholder behind it;
            // a failed load leaves the placeholder visible.
            var logoHost = new Border
            {
                Width = 48,
                Height = 48,
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            };
            Grid.SetColumn(logoHost, 0);
            var logoGrid = new Grid();
            logoHost.Child = logoGrid;
            var initial = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
            logoGrid.Children.Add(new TextBlock
            {
                Text = initial,
                Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                FontSize = 16,
                FontWeight = Windows.UI.Text.FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var logoUrl = manifest != null ? manifest.Logo : null;
            if (!string.IsNullOrEmpty(logoUrl))
            {
                try
                {
                    var image = new Image
                    {
                        Stretch = Stretch.Uniform,
                        Margin = new Thickness(4),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    };
                    image.ImageFailed += (s, e) => { image.Visibility = Visibility.Collapsed; };
                    image.Source = new BitmapImage(new Uri(logoUrl));
                    logoGrid.Children.Add(image);
                }
                catch (Exception ex)
                {
                    Log.Error("Addon logo failed", ex);
                }
            }
            head.Children.Add(logoHost);

            var text = new StackPanel { Spacing = 4 };
            Grid.SetColumn(text, 1);
            head.Children.Add(text);
            var version = manifest != null && !string.IsNullOrEmpty(manifest.Version) ? " v" + manifest.Version : string.Empty;
            text.Children.Add(new TextBlock
            {
                Text = name + version,
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 11,
                FontWeight = Windows.UI.Text.FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
            });
            if (manifest != null && !string.IsNullOrEmpty(manifest.Description))
            {
                text.Children.Add(new TextBlock
                {
                    Text = manifest.Description,
                    Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)),
                    FontSize = 9,
                    TextWrapping = TextWrapping.Wrap,
                    MaxLines = 3,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
            }
            var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var badge in _viewModel.AddonBadges(addon))
            {
                badges.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(4, 1, 4, 1),
                    Child = new TextBlock
                    {
                        Text = badge,
                        Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)),
                        FontSize = 7,
                        FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    },
                });
            }
            text.Children.Add(badges);
            text.Children.Add(new TextBlock
            {
                Text = SettingsViewModel.AddonDisplayUrl(addon),
                Foreground = new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)),
                FontSize = 8,
                TextWrapping = TextWrapping.Wrap,
            });

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
            Grid.SetRow(actions, 1);
            layout.Children.Add(actions);
            actions.Children.Add(MakeAddonActionButton(addon, "up", S("SettingsMoveUp", "Move up"), "↑", index == 0));
            actions.Children.Add(MakeAddonActionButton(addon, "down", S("SettingsMoveDown", "Move down"), "↓", index == count - 1));
            actions.Children.Add(MakeAddonActionButton(addon, "toggle",
                addon.Enabled == false ? S("SettingsEnableAddon", "Enable") : S("SettingsDisableAddon", "Disable"),
                addon.Enabled == false ? S("SettingsEnableAddon", "Enable") : S("SettingsDisableAddon", "Disable"), false));
            actions.Children.Add(MakeAddonActionButton(addon, "refresh", S("SettingsRefreshAddon", "Refresh"), S("SettingsRefreshAddon", "Refresh"), false));
            if (manifest != null && manifest.BehaviorHintsConfigurable)
            {
                actions.Children.Add(MakeAddonActionButton(addon, "configure", S("SettingsConfigureAddon", "Configure"), S("SettingsConfigureAddon", "Configure"), false));
            }
            actions.Children.Add(MakeAddonActionButton(addon, "remove", S("Remove", "Remove"), S("Remove", "Remove"), false));
            return card;
        }

        private Button MakeAddonActionButton(Addon addon, string action, string automationName, string content, bool disabled)
        {
            var button = new Button
            {
                Content = content,
                Style = (Style)Resources["SettingsActionButtonStyle"],
                Tag = addon.TransportUrl + "|" + action,
                IsEnabled = !disabled,
            };
            Windows.UI.Xaml.Automation.AutomationProperties.SetName(button, automationName);
            button.Click += AddonAction_Click;
            return button;
        }

        private string FocusedAddonActionKey()
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused == null || !IsDescendantOf(focused, AddonsHost)) return null;
            for (var node = focused; node != null; node = VisualTreeHelper.GetParent(node))
            {
                var button = node as Button;
                if (button != null) return button.Tag as string;
            }
            return null;
        }

        private void RestoreAddonActionFocus(string key)
        {
            var button = FindButtonByTag(AddonsHost, key);
            if (button != null && button.IsEnabled)
            {
                button.Focus(FocusState.Programmatic);
            }
            else
            {
                InstallAddonButton.Focus(FocusState.Programmatic);
            }
        }

        private async void AddonAction_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null) return;
            var tag = button.Tag as string;
            if (string.IsNullOrEmpty(tag)) return;
            var separator = tag.LastIndexOf('|');
            if (separator < 0) return;
            var url = tag.Substring(0, separator);
            var action = tag.Substring(separator + 1);
            var addon = _viewModel.Addons.FirstOrDefault(a => a.TransportUrl == url);
            switch (action)
            {
                case "up":
                    _viewModel.MoveAddon(url, -1);
                    break;
                case "down":
                    _viewModel.MoveAddon(url, 1);
                    break;
                case "toggle":
                    if (addon != null) _viewModel.SetAddonEnabled(url, addon.Enabled == false);
                    break;
                case "refresh":
                    try
                    {
                        var message = await _viewModel.RefreshAddonAsync(url).ConfigureAwait(true);
                        MainPage.Current.ShowToast(message, "success");
                    }
                    catch (Exception ex)
                    {
                        MainPage.Current.ShowToast(string.Format(S("SettingsRefreshFailed", "Refresh failed: {0}"), ex.Message), "error");
                    }
                    break;
                case "configure":
                    await OpenAddonConfigureAsync(addon).ConfigureAwait(true);
                    break;
                case "remove":
                    if (addon != null && await ShowConfirmAsync(
                        S("SettingsRemoveAddonTitle", "Delete addon?"),
                        string.Format(S("SettingsRemoveAddonMessage", "Remove {0}?"), StremioService.AddonName(addon)),
                        S("Remove", "Remove")).ConfigureAwait(true))
                    {
                        _viewModel.RemoveAddon(url);
                    }
                    break;
            }
        }

        private async Task OpenAddonConfigureAsync(Addon addon)
        {
            var configureUrl = SettingsViewModel.AddonConfigureUrl(addon);
            Uri uri;
            if (string.IsNullOrEmpty(configureUrl) || !Uri.TryCreate(configureUrl, UriKind.Absolute, out uri)) return;
            try
            {
                // Verified against Microsoft Learn: Launcher.LaunchUriAsync(Uri)
                // exists in UWP (UniversalApiContract v1.0) and must run on the
                // UI thread; DetailsPage already uses the same call.
                await Launcher.LaunchUriAsync(uri).AsTask().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Error("Opening addon configure page failed", ex);
                MainPage.Current.ShowToast(S("SettingsConfigureFailed", "Couldn't open the addon settings page"), "error");
            }
        }

        private async void InstallAddonButton_Click(object sender, RoutedEventArgs e)
        {
            var result = await MainPage.Current.RequestTextAsync(
                new TextEntryRequest(S("SettingsAddonUrlLabel", "Addon URL"), "url", string.Empty)).ConfigureAwait(true);
            if (result == null || string.IsNullOrWhiteSpace(result.Text)) return;
            var url = result.Text.Trim();
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                MainPage.Current.ShowToast(S("SettingsInvalidAddonUrl", "Enter a valid addon URL starting with http:// or https://"), "error");
                return;
            }
            MainPage.Current.ShowToast(S("SettingsCheckingAddon", "Checking addon…"));
            try
            {
                var message = await _viewModel.InstallAddonAsync(url).ConfigureAwait(true);
                MainPage.Current.ShowToast(message, "success");
            }
            catch (Exception ex)
            {
                MainPage.Current.ShowToast(string.Format(S("SettingsInstallFailed", "Install failed: {0}"), ex.Message), "error", 5000);
            }
        }

        private async void ResetAddonsButton_Click(object sender, RoutedEventArgs e)
        {
            if (!await ShowConfirmAsync(
                S("SettingsResetAddonsTitle", "Reset addons?"),
                S("SettingsResetAddonsMessage", "This removes all your addons and installs Cinemeta and OpenSubtitles again."),
                S("SettingsReset", "Reset")).ConfigureAwait(true))
            {
                return;
            }
            try
            {
                var message = await _viewModel.ResetAddonsToDefaultsAsync().ConfigureAwait(true);
                MainPage.Current.ShowToast(message, "success");
            }
            catch (Exception ex)
            {
                MainPage.Current.ShowToast(string.Format(S("SettingsInstallFailed", "Install failed: {0}"), ex.Message), "error", 5000);
            }
        }

        // ---------- Debrid ----------

        private void RenderDebrid()
        {
            var focusedKey = FocusedDebridKey();
            DebridHost.Children.Clear();
            var account = _viewModel.DebridAccount;
            if (account != null)
            {
                DebridConnectHeader.Visibility = Visibility.Collapsed;
                var name = _viewModel.DebridProviderName;
                var shortTag = _viewModel.DebridProviderShort;
                var connectedRow = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x8C, 0x0D, 0x0E, 0x13)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(12, 8, 12, 8),
                };
                var connectedGrid = new Grid();
                connectedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                connectedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                connectedGrid.ColumnSpacing = 12;
                var connectedText = new StackPanel { Spacing = 2 };
                Grid.SetColumn(connectedText, 0);
                connectedText.Children.Add(new TextBlock
                {
                    Text = name,
                    Foreground = new SolidColorBrush(Colors.White),
                    FontSize = 11,
                    FontWeight = Windows.UI.Text.FontWeights.Bold,
                    TextWrapping = TextWrapping.Wrap,
                });
                connectedText.Children.Add(new TextBlock
                {
                    Text = string.Format(S("SettingsDebridConnectedDescription", "Connected. Torrent results show \"via {0}\" and play through it."), shortTag),
                    Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)),
                    FontSize = 8,
                    TextWrapping = TextWrapping.Wrap,
                });
                connectedGrid.Children.Add(connectedText);
                var connectedValue = new TextBlock
                {
                    Text = S("SettingsDebridConnectedValue", "Connected"),
                    Foreground = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)),
                    FontSize = 9,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(connectedValue, 1);
                connectedGrid.Children.Add(connectedValue);
                connectedRow.Child = connectedGrid;
                DebridHost.Children.Add(connectedRow);

                var disconnectButton = BuildRowButton(
                    string.Format(S("SettingsDisconnectDebridRow", "Disconnect {0}"), name), null, null);
                disconnectButton.Tag = "debrid|disconnect";
                disconnectButton.Click += DisconnectDebridButton_Click;
                DebridHost.Children.Add(disconnectButton);
            }
            else
            {
                DebridConnectHeader.Visibility = Visibility.Visible;
                DebridConnectHeader.Text = S("SettingsDebridConnectHeader", "CONNECT A SERVICE");
                foreach (var provider in _viewModel.DebridProviders)
                {
                    if (provider == null) continue;
                    var button = BuildRowButton(
                        provider.Name,
                        string.Format(S("SettingsDebridKeyFrom", "API key from {0}"), provider.KeyHelp),
                        S("SettingsDebridConnect", "Connect"));
                    button.Tag = "debrid|" + provider.Id;
                    button.Click += DebridProvider_Click;
                    DebridHost.Children.Add(button);
                }
            }
            if (!string.IsNullOrEmpty(focusedKey))
            {
                var button = FindButtonByTag(DebridHost, focusedKey);
                if (button != null) button.Focus(FocusState.Programmatic);
                else
                {
                    var first = FirstButtonIn(DebridHost);
                    if (first != null) first.Focus(FocusState.Programmatic);
                }
            }
        }

        private async void DebridProvider_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null) return;
            var tag = button.Tag as string;
            if (string.IsNullOrEmpty(tag) || !tag.StartsWith("debrid|", StringComparison.Ordinal)) return;
            var id = tag.Substring("debrid|".Length);
            var provider = _viewModel.DebridProviders.FirstOrDefault(p => p != null && p.Id == id);
            if (provider == null) return;
            // MainPage.ShowOnScreenKeyboard is private, so the key goes through
            // RequestTextAsync with kind "password" (which shows the on-screen
            // keyboard itself); the keyHelp location rides in the label.
            var label = string.Format(S("SettingsDebridKeyLabel", "{0} API key — find it at {1}"), provider.Name, provider.KeyHelp);
            var result = await MainPage.Current.RequestTextAsync(
                new TextEntryRequest(label, "password", string.Empty)).ConfigureAwait(true);
            if (result == null || string.IsNullOrWhiteSpace(result.Text)) return;
            MainPage.Current.ShowToast(S("SettingsCheckingKey", "Checking key…"));
            try
            {
                var accepted = await _viewModel.ConnectDebridAsync(id, result.Text.Trim()).ConfigureAwait(true);
                if (!accepted)
                {
                    MainPage.Current.ShowToast(string.Format(S("SettingsDebridKeyRejected", "{0} didn't accept that key."), provider.Name), "error");
                    return;
                }
                MainPage.Current.ShowToast(string.Format(S("SettingsDebridConnected", "{0} connected"), provider.Name), "success");
            }
            catch (HttpError ex)
            {
                Log.Error("Validating debrid key failed", ex);
                MainPage.Current.ShowToast(string.Format(S("SettingsDebridUnreachable", "Couldn't reach {0}: {1}"), provider.Name, ex.Message), "error");
            }
            catch (Exception ex)
            {
                Log.Error("Validating debrid key failed", ex);
                MainPage.Current.ShowToast(string.Format(S("SettingsDebridUnreachable", "Couldn't reach {0}: {1}"), provider.Name, ex.Message), "error");
            }
        }

        private async void DisconnectDebridButton_Click(object sender, RoutedEventArgs e)
        {
            var name = _viewModel.DebridProviderName;
            if (!await ShowConfirmAsync(
                string.Format(S("SettingsDisconnectDebridTitle", "Disconnect {0}?"), name),
                S("SettingsDisconnectDebridMessage", "Torrent results will be hidden again."),
                S("SettingsDisconnect", "Disconnect")).ConfigureAwait(true))
            {
                return;
            }
            _viewModel.DisconnectDebrid();
        }

        private string FocusedDebridKey()
        {
            var focused = FocusManager.GetFocusedElement() as DependencyObject;
            if (focused == null || !IsDescendantOf(focused, DebridHost)) return null;
            for (var node = focused; node != null; node = VisualTreeHelper.GetParent(node))
            {
                var button = node as Button;
                if (button != null) return button.Tag as string;
            }
            return null;
        }

        // ---------- Confirm dialog ----------

        /// <summary>Title + message confirm dialog; focuses the primary button on Opened (DetailsPage pattern).</summary>
        private async Task<bool> ShowConfirmAsync(string title, string message, string confirmLabel)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = confirmLabel,
                CloseButtonText = S("Cancel", "Cancel"),
                DefaultButton = ContentDialogButton.Primary,
            };
            dialog.Opened += (s, e) =>
            {
                var primary = FindDialogButton(dialog, confirmLabel);
                if (primary != null) primary.Focus(FocusState.Programmatic);
            };
            var shell = MainPage.Current;
            // Part C: remember the invoker so focus returns to it when the
            // dialog closes instead of being lost.
            var invoker = FocusManager.GetFocusedElement() as UIElement;
            shell.DialogOpen = true;
            ContentDialogResult result;
            try
            {
                result = await dialog.ShowAsync().AsTask().ConfigureAwait(true);
            }
            finally
            {
                shell.DialogOpen = false;
                RestoreDialogInvoker(invoker);
            }
            return result == ContentDialogResult.Primary;
        }

        private static Button FindDialogButton(DependencyObject root, string content)
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var button = child as Button;
                if (button != null && (button.Content as string) == content) return button;
                var found = FindDialogButton(child, content);
                if (found != null) return found;
            }
            return null;
        }

        // ---------- Helpers ----------

        private static Button FirstButtonIn(Panel panel)
        {
            if (panel == null) return null;
            foreach (var child in panel.Children)
            {
                var button = child as Button;
                if (button != null) return button;
            }
            return null;
        }

        private static Button FindButtonByTag(DependencyObject root, string tag)
        {
            if (root == null || string.IsNullOrEmpty(tag)) return null;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var button = child as Button;
                if (button != null && (button.Tag as string) == tag) return button;
                var found = FindButtonByTag(child, tag);
                if (found != null) return found;
            }
            return null;
        }

        private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
        {
            for (var node = element; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (node == ancestor) return true;
            }
            return false;
        }
    }
}
