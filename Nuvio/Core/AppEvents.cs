using System;
using Nuvio.Models;
using Windows.Data.Json;

namespace Nuvio.Core
{
    /// <summary>Emitted when a setting changes (replaces the JS event bus 'settings' event for this cross-cutting case).</summary>
    internal sealed class SettingsChangedEventArgs : EventArgs
    {
        public SettingsChangedEventArgs(string name, IJsonValue value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; }
        public IJsonValue Value { get; }
    }

    /// <summary>Emitted when the shell navigates to another root (replaces the JS event bus 'route' event).</summary>
    internal sealed class RouteChangedEventArgs : EventArgs
    {
        public RouteChangedEventArgs(string routeName)
        {
            RouteName = routeName;
        }

        public string RouteName { get; }
    }

    /// <summary>
    /// Emitted by the player (via PlayerService.ReportPlayback) for Trakt/Jellyfin
    /// consumers (architecture §10.3). Phase is start|progress|pause|resume|stop.
    /// </summary>
    internal sealed class PlaybackEventArgs : EventArgs
    {
        public PlaybackEventArgs(string phase, string contentId, string contentType, string videoId,
            int? season, int? episode, string title, double position, double duration, bool paused, StreamItem stream)
        {
            Phase = phase;
            ContentId = contentId;
            ContentType = contentType;
            VideoId = videoId;
            Season = season;
            Episode = episode;
            Title = title;
            Position = position;
            Duration = duration;
            Paused = paused;
            Stream = stream;
        }

        public string Phase { get; }
        public string ContentId { get; }
        public string ContentType { get; }
        public string VideoId { get; }
        public int? Season { get; }
        public int? Episode { get; }
        public string Title { get; }
        /// <summary>Position in seconds (the duration when ended).</summary>
        public double Position { get; }
        /// <summary>Duration in seconds.</summary>
        public double Duration { get; }
        public bool Paused { get; }
        public StreamItem Stream { get; }
    }

    /// <summary>
    /// Tiny typed event aggregator for the three cross-cutting events. Everything else is a
    /// per-service event (e.g. LibraryService.LibraryChanged).
    /// </summary>
    internal static class AppEvents
    {
        public static event EventHandler<SettingsChangedEventArgs> SettingsChanged;
        public static event EventHandler<RouteChangedEventArgs> RouteChanged;
        public static event EventHandler<PlaybackEventArgs> Playback;

        public static void RaiseSettingsChanged(string name, IJsonValue value)
        {
            var handler = SettingsChanged;
            if (handler != null) handler(null, new SettingsChangedEventArgs(name, value));
        }

        public static void RaiseRouteChanged(string routeName)
        {
            var handler = RouteChanged;
            if (handler != null) handler(null, new RouteChangedEventArgs(routeName));
        }

        public static void RaisePlayback(PlaybackEventArgs args)
        {
            var handler = Playback;
            if (handler != null) handler(null, args);
        }
    }
}
