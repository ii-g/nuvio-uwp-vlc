using System.Collections.Generic;
using Nuvio.Core;
using Nuvio.Models;
using Nuvio.Views;
using Windows.UI.Xaml.Controls;

namespace Nuvio.Services
{
    /// <summary>Navigation parameter for a not-yet-built page (M1: everything but Home).</summary>
    internal sealed class PlaceholderNavigation
    {
        public string Title { get; set; }
        public string Message { get; set; }
        /// <summary>Which milestone builds this screen, e.g. "Milestone 2".</summary>
        public string Milestone { get; set; }
        /// <summary>Sidebar root to highlight while the placeholder is showing, if any.</summary>
        public string SidebarRoot { get; set; }
    }

    /// <summary>Navigation parameter for the Details page (M3).</summary>
    internal sealed class DetailsNavigation
    {
        public string Type { get; set; }
        public string Id { get; set; }
        /// <summary>The catalog meta object, shown while the full meta loads.</summary>
        public Meta PreviewMeta { get; set; }
    }

    /// <summary>Navigation parameter for the Streams page (M4).</summary>
    internal sealed class StreamsNavigation
    {
        public string Type { get; set; }
        public string Id { get; set; }
        public string VideoId { get; set; }
        public Meta Meta { get; set; }
        public Video Video { get; set; }
        public bool Restart { get; set; }
    }

    /// <summary>Navigation parameter for the Player page (M4). A plain in-process object, never a URI.</summary>
    internal sealed class PlayerNavigation
    {
        public Meta Meta { get; set; }
        public Video Video { get; set; }
        public StreamItem Stream { get; set; }
        public List<StreamItem> Streams { get; set; }
        public bool Restart { get; set; }
        public bool FallbackToStreams { get; set; }
    }

    /// <summary>Navigation parameter for the Catalog page (M2).</summary>
    internal sealed class CatalogNavigation
    {
        public string TransportUrl { get; set; }
        public string Type { get; set; }
        public string Id { get; set; }
    }

    /// <summary>
    /// The only cross-page navigation API (architecture D2): OpenTitle / PlayVideo /
    /// ResumeEntry, ported from services/navigation.js. In M1 every destination but Home
    /// is a PlaceholderPage — deliberate, so navigation is proven end-to-end without
    /// building Details early.
    /// </summary>
    internal sealed class NavigationService
    {
        private readonly Frame _frame;
        private readonly AppSettings _settings;

        public NavigationService(Frame frame, AppSettings settings)
        {
            _frame = frame;
            _settings = settings;
        }

        public bool CanGoBack { get { return _frame.CanGoBack; } }

        public void NavigateHome()
        {
            _frame.BackStack.Clear();
            _frame.Navigate(typeof(HomePage));
            AppEvents.RaiseRouteChanged("home");
        }

        /// <summary>Sidebar roots navigate with root semantics: clear the stack, navigate.</summary>
        public void NavigateRoot(string root)
        {
            switch (root)
            {
                case "home":
                    NavigateHome();
                    break;
                case "search":
                    NavigatePlaceholder("Search", "Search lands in Milestone 2.", "Milestone 2", "search");
                    break;
                case "library":
                    NavigatePlaceholder("Library", "Library lands in Milestone 5.", "Milestone 5", "library");
                    break;
                case "settings":
                    NavigatePlaceholder("Settings", "Settings lands in Milestone 6.", "Milestone 6", "settings");
                    break;
                default:
                    NavigateHome();
                    break;
            }
        }

        public void NavigatePlaceholder(string title, string message, string milestone, string sidebarRoot = null)
        {
            _frame.BackStack.Clear();
            _frame.Navigate(typeof(PlaceholderPage), new PlaceholderNavigation
            {
                Title = title,
                Message = message,
                Milestone = milestone,
                SidebarRoot = sidebarRoot,
            });
            AppEvents.RaiseRouteChanged("placeholder");
        }

        public void OpenTitle(Meta meta)
        {
            _frame.Navigate(typeof(PlaceholderPage), new PlaceholderNavigation
            {
                Title = meta != null && !string.IsNullOrEmpty(meta.Name) ? meta.Name : "Details",
                Message = "Details lands in Milestone 3.",
                Milestone = "Milestone 3",
            });
            AppEvents.RaiseRouteChanged("details");
        }

        /// <summary>Opens the stream list for a movie (video null) or an episode.</summary>
        public void PlayVideo(Meta meta, Video video, bool restart = false)
        {
            _frame.Navigate(typeof(PlaceholderPage), new PlaceholderNavigation
            {
                Title = meta != null && !string.IsNullOrEmpty(meta.Name) ? meta.Name : "Streams",
                Message = "Streams land in Milestone 4.",
                Milestone = "Milestone 4",
            });
            AppEvents.RaiseRouteChanged("streams");
        }

        /// <summary>Continue Watching: play the next episode, or resume with the last stream if we have it.</summary>
        public void ResumeEntry(ProgressEntry entry, bool restart = false)
        {
            var meta = new Meta
            {
                Id = entry.ContentId,
                Type = entry.ContentType,
                Name = entry.Title,
                Poster = entry.Poster,
                Background = entry.Background,
                Logo = entry.Logo,
            };
            if (entry.NextUp && entry.Next != null)
            {
                PlayVideo(meta, entry.Next);
                return;
            }
            var video = entry.Season.HasValue
                ? new Video { Id = entry.VideoId, Season = entry.Season.Value, Episode = entry.Episode ?? 0, Name = entry.EpisodeTitle, Thumbnail = entry.Thumbnail }
                : null;
            if (!restart && _settings.RememberLastStream && entry.Stream != null && !string.IsNullOrEmpty(entry.Stream.Url))
            {
                _frame.Navigate(typeof(PlaceholderPage), new PlaceholderNavigation
                {
                    Title = meta.Name ?? "Player",
                    Message = "Playback lands in Milestone 4.",
                    Milestone = "Milestone 4",
                });
                AppEvents.RaiseRouteChanged("player");
                return;
            }
            PlayVideo(meta, video, restart);
        }

        /// <summary>Back with an empty stack on a non-home root navigates home.</summary>
        public void GoBackOrHome()
        {
            if (_frame.CanGoBack) _frame.GoBack();
            else NavigateHome();
        }
    }
}
