using System.Collections.Generic;

namespace Nuvio.Models
{
    /// <summary>One parsed subtitle cue: { start (s), end (s), text (HTML-safe subset: &lt;i&gt;/&lt;b&gt;/&lt;u&gt;, &lt;br&gt;) }.</summary>
    internal sealed class SubtitleCue
    {
        public double Start { get; set; }
        public double End { get; set; }
        public string Text { get; set; }
    }

    /// <summary>An addon subtitle: { url, lang, addonName, key: '&lt;index&gt;:&lt;url&gt;' } plus its parsed cues once loaded.</summary>
    internal sealed class SubtitleItem
    {
        public string Url { get; set; }
        public string Lang { get; set; }
        public string Id { get; set; }
        public string AddonName { get; set; }
        public string Key { get; set; }
        public List<SubtitleCue> Cues { get; set; }
        public bool CuesLoaded { get; set; }
    }
}
