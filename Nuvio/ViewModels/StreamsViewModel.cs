using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nuvio.Core;
using Nuvio.Diagnostics;
using Nuvio.Models;
using Nuvio.Services;
using Windows.ApplicationModel.Resources;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace Nuvio.ViewModels
{
    /// <summary>One filter chip: "All" or one stream addon (key = "all" or the transport URL).</summary>
    internal sealed class StreamFilterChip
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public bool Selected { get; set; }
    }

    /// <summary>Per-addon load state, in StreamAddons order.</summary>
    internal sealed class AddonStreamState
    {
        public Addon Addon { get; set; }
        /// <summary>"loading" | "done" | "error".</summary>
        public string Status { get; set; }
    }

    /// <summary>One status line under the filters: optional spinner, text, error colour.</summary>
    internal sealed class StreamStatusLine
    {
        public string Text { get; set; }
        public bool ShowSpinner { get; set; }
        public bool IsError { get; set; }
    }

    /// <summary>One visible stream row, with its display strings already resolved.</summary>
    internal sealed class StreamRowItem
    {
        public StreamEntry Entry { get; set; }
        public string Resolution { get; set; }
        public string AddonName { get; set; }
        public string Name { get; set; }
        public string Detail { get; set; }
        public List<string> Badges { get; set; }
        /// <summary>e.g. "RD+" when cached with a debrid tag, "Cached" when cached without one, else null.</summary>
        public string CachedLabel { get; set; }
        /// <summary>Debrid tag when not cached, e.g. "RD", else null.</summary>
        public string DebridLabel { get; set; }
        public string SizeText { get; set; }
        /// <summary>"Torrent" / "Not playable" when the row cannot be played, else null.</summary>
        public string UnsupportedTag { get; set; }
        public bool IsPlayable { get; set; }
    }

    /// <summary>
    /// Streams: the stream list for one title/episode. Ported from views/streams.js.
    /// No UI types here: the page renders chips/list/status from EntriesChanged and
    /// the computed properties below. No debrid in M4, so only http streams play;
    /// torrent rows show as "Torrent" and toast on click. Never logs stream URLs.
    /// </summary>
    internal sealed class StreamsViewModel : ViewModelBase
    {
        private readonly StremioService _stremio;
        private readonly AppSettings _settings;
        private readonly NavigationService _navigation;
        private readonly CoreDispatcher _dispatcher;
        private readonly ResourceLoader _strings = ResourceLoader.GetForViewIndependentUse("Resources");

        /// <summary>Raised on the UI thread whenever entries, addon states or the filter change.</summary>
        public event EventHandler EntriesChanged;

        private string _type;
        private string _id;
        private string _videoId;
        private bool _closed;
        private bool _initDone;
        private string _filter = "all";
        private readonly List<StreamEntry> _entries = new List<StreamEntry>();
        private List<AddonStreamState> _addonStates = new List<AddonStreamState>();

        public StreamsViewModel(StremioService stremio, AppSettings settings, NavigationService navigation)
        {
            _stremio = stremio;
            _settings = settings;
            _navigation = navigation;
            _dispatcher = Window.Current.Dispatcher;
        }

        private string S(string key, string fallback)
        {
            var text = _strings.GetString(key);
            return string.IsNullOrEmpty(text) ? fallback : text;
        }

        // ---------- Info panel ----------

        private Meta _meta;
        private Video _video;
        private bool _restart;

        public Meta Meta
        {
            get { return _meta; }
            private set { SetProperty(ref _meta, value); }
        }

        public Video Video
        {
            get { return _video; }
            private set { SetProperty(ref _video, value); }
        }

        public bool Restart { get { return _restart; } }

        public string Title { get { return Meta != null ? Meta.Name ?? string.Empty : string.Empty; } }
        public string LogoUrl { get { return Meta != null ? Meta.Logo : null; } }
        public string BackdropUrl { get { return Meta != null ? Meta.Background : null; } }
        public string EpisodeCode { get { return Video != null ? Formatting.FormatEpisodeCode(Video.Season, Video.Episode) : string.Empty; } }
        public string EpisodeName { get { return Video != null ? (!string.IsNullOrEmpty(Video.Name) ? Video.Name : Video.Title ?? string.Empty) : string.Empty; } }
        public string ThumbnailUrl { get { return Video != null ? Video.Thumbnail : null; } }
        public string Description
        {
            get
            {
                if (Video != null && !string.IsNullOrEmpty(Video.Overview)) return Video.Overview;
                if (Video != null && !string.IsNullOrEmpty(Video.Description)) return Video.Description;
                return Meta != null ? Meta.Description ?? string.Empty : string.Empty;
            }
        }

        public bool HasVideo { get { return Video != null; } }

        // ---------- Derived state ----------

        /// <summary>True while any addon is still loading.</summary>
        public bool IsLoadingStreams { get { return _addonStates.Any(s => s.Status == "loading"); } }

        /// <summary>Entries the list shows: playable-only unless showUnsupportedStreams, then the filter, then streamSort.</summary>
        public List<StreamEntry> VisibleEntries
        {
            get { return ComputeVisibleEntries(); }
        }

        public bool HasVisibleEntries { get { return ComputeVisibleEntries().Count > 0; } }

        /// <summary>Centered spinner: no visible rows yet and addons still loading.</summary>
        public bool ShowEmptyLoading { get { return !HasVisibleEntries && IsLoadingStreams; } }

        /// <summary>Empty state once loading finished with no visible rows.</summary>
        public bool ShowEmptyState { get { return !HasVisibleEntries && !IsLoadingStreams; } }

        /// <summary>"No streams found" when addons exist, "No stream addons" when none do.</summary>
        public string EmptyTitle
        {
            get
            {
                if (!ShowEmptyState) return string.Empty;
                return _addonStates.Count > 0
                    ? S("StreamsNoStreamsFound", "No streams found")
                    : S("StreamsNoStreamAddons", "No stream addons");
            }
        }

        public string EmptyMessage
        {
            get
            {
                if (!ShowEmptyState) return string.Empty;
                return _addonStates.Count > 0
                    ? S("StreamsNoStreamsMessage", "None of your addons returned a playable link for this. Debrid addons (Torrentio, Comet, AIOStreams with Real-Debrid, TorBox, AllDebrid...) give direct links that play here.")
                    : S("StreamsNoAddonsMessage", "Install an addon that provides streams, such as Torrentio or AIOStreams configured with your debrid service.");
            }
        }

        public List<StreamFilterChip> FilterChips
        {
            get { return ComputeFilterChips(); }
        }

        public List<StreamStatusLine> StatusLines
        {
            get { return ComputeStatusLines(); }
        }

        /// <summary>Rows for the page's ListView, built from VisibleEntries.</summary>
        public List<StreamRowItem> VisibleRows
        {
            get
            {
                var rows = new List<StreamRowItem>();
                foreach (var entry in ComputeVisibleEntries())
                {
                    rows.Add(BuildRow(entry));
                }
                return rows;
            }
        }

        private StreamRowItem BuildRow(StreamEntry entry)
        {
            var info = entry.Info;
            var playable = CanPlay(entry);
            string cachedLabel = null;
            string debridLabel = null;
            if (info.Cached)
            {
                cachedLabel = !string.IsNullOrEmpty(info.Debrid) ? info.Debrid + "+" : S("StreamsCached", "Cached");
            }
            else if (!string.IsNullOrEmpty(info.Debrid))
            {
                debridLabel = info.Debrid;
            }
            return new StreamRowItem
            {
                Entry = entry,
                Resolution = info.Resolution,
                AddonName = info.AddonName,
                Name = info.Name,
                Detail = info.Detail,
                Badges = info.Badges ?? new List<string>(),
                CachedLabel = cachedLabel,
                DebridLabel = debridLabel,
                SizeText = info.SizeText,
                UnsupportedTag = playable
                    ? null
                    : (info.Kind == StreamKind.Torrent
                        ? S("StreamsTorrent", "Torrent")
                        : S("StreamsNotPlayable", "Not playable")),
                IsPlayable = playable,
            };
        }

        /// <summary>
        /// M4: only http streams play. There is no debrid account yet (M6), so a
        /// torrent entry is never playable here, even if an addon already embeds one.
        /// </summary>
        public static bool CanPlay(StreamEntry entry)
        {
            return entry != null && entry.Info != null && entry.Info.Kind == StreamKind.Http;
        }

        private bool ShowUnsupported { get { return _settings.GetBool("showUnsupportedStreams", false); } }

        private List<StreamEntry> ComputeVisibleEntries()
        {
            var showAll = ShowUnsupported;
            var shown = _entries.Where(e => showAll || CanPlay(e)).ToList();
            if (_filter != "all")
            {
                shown = shown.Where(e => e.Addon != null && e.Addon.TransportUrl == _filter).ToList();
            }
            return StreamAnalysis.SortStreams(shown, _settings.GetString("streamSort", "addon"));
        }

        private List<StreamFilterChip> ComputeFilterChips()
        {
            var showAll = ShowUnsupported;
            var counts = new Dictionary<string, int>();
            foreach (var entry in _entries)
            {
                if (!showAll && !CanPlay(entry)) continue;
                var url = entry.Addon != null ? entry.Addon.TransportUrl : string.Empty;
                int count;
                counts.TryGetValue(url, out count);
                counts[url] = count + 1;
            }
            var chips = new List<StreamFilterChip>
            {
                new StreamFilterChip { Key = "all", Label = S("All", "All"), Selected = _filter == "all" },
            };
            foreach (var state in _addonStates)
            {
                var url = state.Addon != null ? state.Addon.TransportUrl : string.Empty;
                int count;
                counts.TryGetValue(url, out count);
                if (state.Status == "done" && count == 0) continue;
                var label = StremioService.AddonName(state.Addon)
                    + (state.Status == "loading" ? " …" : " (" + count + ")");
                chips.Add(new StreamFilterChip { Key = url, Label = label, Selected = _filter == url });
            }
            return chips;
        }

        private List<StreamStatusLine> ComputeStatusLines()
        {
            var lines = new List<StreamStatusLine>();
            var loading = _addonStates.Where(s => s.Status == "loading").ToList();
            if (loading.Count > 0)
            {
                var names = string.Join(", ", loading.Select(s => StremioService.AddonName(s.Addon)));
                lines.Add(new StreamStatusLine
                {
                    Text = string.Format(S("StreamsLoadingFrom", "Loading from {0}"), names),
                    ShowSpinner = true,
                });
            }
            var hidden = _entries.Count(e => !CanPlay(e));
            if (hidden > 0 && !ShowUnsupported)
            {
                var text = hidden == 1
                    ? S("StreamsHiddenTorrentOne", "1 torrent result hidden: connect a debrid service in Settings, or set one up in your addon, to play them here.")
                    : string.Format(S("StreamsHiddenTorrents", "{0} torrent results hidden: connect a debrid service in Settings, or set one up in your addon, to play them here."), hidden);
                lines.Add(new StreamStatusLine { Text = text });
            }
            var failed = _addonStates.Where(s => s.Status == "error").ToList();
            if (failed.Count > 0)
            {
                var names = string.Join(", ", failed.Select(s => StremioService.AddonName(s.Addon)));
                lines.Add(new StreamStatusLine
                {
                    Text = string.Format(S("StreamsFailedToRespond", "{0} failed to respond."), names),
                    IsError = true,
                });
            }
            return lines;
        }

        public void SetFilter(string key)
        {
            if (string.IsNullOrEmpty(key)) key = "all";
            if (_filter == key) return;
            _filter = key;
            RaiseDerivedChanged();
        }

        // ---------- Init / load ----------

        /// <summary>
        /// Resolves meta (fetched + merged when the navigation param has none or no
        /// name) and the episode video (found in the meta, else synthesised from the
        /// parsed video id). Mirrors streams.js init().
        /// </summary>
        public async Task Init(string type, string id, string videoId, Meta meta, Video video, bool restart)
        {
            _type = type;
            _id = id;
            _videoId = string.IsNullOrEmpty(videoId) ? id : videoId;
            _restart = restart;
            Meta = meta;
            Video = video;
            if (Meta == null || string.IsNullOrEmpty(Meta.Name))
            {
                try
                {
                    var fetched = await _stremio.FetchMetaAsync(type, id).ConfigureAwait(false);
                    var passed = Meta;
                    if (passed != null)
                    {
                        if (string.IsNullOrEmpty(fetched.Logo)) fetched.Logo = passed.Logo;
                        if (string.IsNullOrEmpty(fetched.Background)) fetched.Background = passed.Background;
                        if (string.IsNullOrEmpty(fetched.Poster)) fetched.Poster = passed.Poster;
                    }
                    await RunOnUi(() => { Meta = fetched; }).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Error("Loading streams meta failed", ex);
                    await RunOnUi(() =>
                    {
                        if (Meta == null) Meta = new Meta { Id = id, Type = type, Name = string.Empty };
                    }).ConfigureAwait(false);
                }
            }
            if (Video == null && type != "movie" && _videoId != id)
            {
                var parsed = ProgressService.ParseVideoId(_videoId, type);
                var found = MetaHelpers.SortedVideos(Meta).FirstOrDefault(v => v.Id == _videoId);
                var resolved = found ?? new Video
                {
                    Id = _videoId,
                    Season = parsed.Season ?? 0,
                    Episode = parsed.Episode ?? 0,
                };
                await RunOnUi(() => { Video = resolved; }).ConfigureAwait(false);
            }
            await RunOnUi(() => RaiseInfoChanged()).ConfigureAwait(false);
            _initDone = true;
        }

        /// <summary>
        /// Asks every stream addon in parallel; each addon's results land as they
        /// arrive, kept in addon order (then each addon's own order) no matter who
        /// answers first. Per-addon failures only mark that addon failed.
        /// </summary>
        public async Task LoadStreamsAsync()
        {
            if (!_initDone) return;
            var addons = _stremio.StreamAddons(_type, _videoId);
            await RunOnUi(() =>
            {
                _addonStates = addons.Select(a => new AddonStreamState { Addon = a, Status = "loading" }).ToList();
                RaiseDerivedChanged();
            }).ConfigureAwait(false);
            var tasks = new List<Task>();
            for (var i = 0; i < addons.Count; i++)
            {
                tasks.Add(LoadAddonAsync(addons[i], i));
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        private async Task LoadAddonAsync(Addon addon, int order)
        {
            try
            {
                var streams = await _stremio.FetchStreamsAsync(addon, _type, _videoId).ConfigureAwait(false);
                if (_closed) return;
                var name = StremioService.AddonName(addon);
                var made = new List<StreamEntry>();
                for (var index = 0; index < streams.Count; index++)
                {
                    var stream = streams[index];
                    made.Add(new StreamEntry
                    {
                        Stream = stream,
                        Info = StreamAnalysis.DescribeStream(stream, name),
                        Addon = addon,
                        Order = order * 1000 + index,
                    });
                }
                await RunOnUi(() =>
                {
                    _entries.AddRange(made);
                    _entries.Sort((a, b) => a.Order.CompareTo(b.Order));
                    SetAddonStatus(addon, "done");
                    RaiseDerivedChanged();
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("Fetching streams failed: " + Log.DescribeUrl(addon.TransportUrl), ex);
                if (_closed) return;
                await RunOnUi(() =>
                {
                    SetAddonStatus(addon, "error");
                    RaiseDerivedChanged();
                }).ConfigureAwait(false);
            }
        }

        private void SetAddonStatus(Addon addon, string status)
        {
            foreach (var state in _addonStates)
            {
                if (state.Addon == addon) state.Status = status;
            }
        }

        /// <summary>
        /// Plays an entry, or returns the toast message the page must show when the
        /// entry cannot be played here (null when the player was opened). The caller
        /// shows the toast; this class owns no UI. Torrents are never resolved in M4.
        /// </summary>
        public string Play(StreamEntry entry)
        {
            if (entry == null || entry.Stream == null) return null;
            if (!StreamAnalysis.IsPlayable(entry.Stream))
            {
                if (entry.Info != null && entry.Info.Kind == StreamKind.Torrent)
                {
                    return S("StreamsTorrentToast", "Torrents can't be played on this device. Connect a debrid service in Settings, or use one in your addon.");
                }
                return S("StreamsUnplayableToast", "This link can't be played here.");
            }
            var chosen = CopyWithAddon(entry);
            var others = StreamAnalysis.SortForPlayback(_entries.Where(e => e.Info != null && e.Info.Kind == StreamKind.Http).ToList())
                .Select(e => CopyWithAddon(e))
                .Where(s => s.Url != chosen.Url)
                .ToList();
            var streams = new List<StreamItem> { chosen };
            streams.AddRange(others);
            _navigation.OpenPlayer(Meta, Video, chosen, streams, _restart, fallbackToStreams: false);
            return null;
        }

        private static StreamItem CopyWithAddon(StreamEntry entry)
        {
            var source = entry.Stream;
            return new StreamItem
            {
                Url = source.Url,
                Name = source.Name,
                Title = source.Title,
                Description = source.Description,
                InfoHash = source.InfoHash,
                FileIdx = source.FileIdx,
                Sources = source.Sources,
                YtId = source.YtId,
                ExternalUrl = source.ExternalUrl,
                Subtitles = source.Subtitles,
                BehaviorHints = source.BehaviorHints,
                Jellyfin = source.Jellyfin,
                AddonUrl = entry.Addon != null ? entry.Addon.TransportUrl : null,
                AddonName = entry.Info != null ? entry.Info.AddonName : null,
            };
        }

        /// <summary>Stops pending loads from applying results after the page is gone.</summary>
        public void Close()
        {
            _closed = true;
        }

        private void RaiseInfoChanged()
        {
            RaisePropertyChanged(nameof(Meta));
            RaisePropertyChanged(nameof(Video));
            RaisePropertyChanged(nameof(Title));
            RaisePropertyChanged(nameof(LogoUrl));
            RaisePropertyChanged(nameof(BackdropUrl));
            RaisePropertyChanged(nameof(EpisodeCode));
            RaisePropertyChanged(nameof(EpisodeName));
            RaisePropertyChanged(nameof(ThumbnailUrl));
            RaisePropertyChanged(nameof(Description));
            RaisePropertyChanged(nameof(HasVideo));
        }

        private void RaiseDerivedChanged()
        {
            RaisePropertyChanged(nameof(IsLoadingStreams));
            RaisePropertyChanged(nameof(VisibleEntries));
            RaisePropertyChanged(nameof(HasVisibleEntries));
            RaisePropertyChanged(nameof(ShowEmptyLoading));
            RaisePropertyChanged(nameof(ShowEmptyState));
            RaisePropertyChanged(nameof(EmptyTitle));
            RaisePropertyChanged(nameof(EmptyMessage));
            RaisePropertyChanged(nameof(FilterChips));
            RaisePropertyChanged(nameof(StatusLines));
            RaisePropertyChanged(nameof(VisibleRows));
            var handler = EntriesChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private async Task RunOnUi(Action action)
        {
            if (_dispatcher.HasThreadAccess) action();
            else await _dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action()).AsTask().ConfigureAwait(false);
        }
    }
}
