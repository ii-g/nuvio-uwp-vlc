using System;
using Windows.Data.Json;

namespace Nuvio.Core
{
    /// <summary>Emitted when a setting changes (replaces the JS event bus 'settings' event for this cross-cutting case).</summary>
    internal sealed class SettingsChangedEventArgs : EventArgs
    {
        public SettingsChangedEventArgs(string name, JsonValue value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; }
        public JsonValue Value { get; }
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
    /// Tiny typed event aggregator for the two cross-cutting events. Everything else is a
    /// per-service event (e.g. LibraryService.LibraryChanged).
    /// </summary>
    internal static class AppEvents
    {
        public static event EventHandler<SettingsChangedEventArgs> SettingsChanged;
        public static event EventHandler<RouteChangedEventArgs> RouteChanged;

        public static void RaiseSettingsChanged(string name, JsonValue value)
        {
            var handler = SettingsChanged;
            if (handler != null) handler(null, new SettingsChangedEventArgs(name, value));
        }

        public static void RaiseRouteChanged(string routeName)
        {
            var handler = RouteChanged;
            if (handler != null) handler(null, new RouteChangedEventArgs(routeName));
        }
    }
}
