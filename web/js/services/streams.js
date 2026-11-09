// Reading addon stream results: what kind of link it is, quality badges, and sorting.

const RESOLUTIONS = [
  { label: '4K', rank: 4, re: /\b(2160p|4k|uhd)\b/i },
  { label: '1440p', rank: 3.5, re: /\b1440p\b/i },
  { label: '1080p', rank: 3, re: /\b(1080p|fhd)\b/i },
  { label: '720p', rank: 2, re: /\b(720p|hd)\b/i },
  { label: '576p', rank: 1.5, re: /\b576p\b/i },
  { label: '480p', rank: 1, re: /\b(480p|sd)\b/i },
];
const DEBRID_TAGS = /\[(RD|AD|PM|TB|DL|ED|OC|PKP|EN|TRB|DLR|EASY|PIO|SEEDR)(\+|⚡| download)?\]/i;

/** "http" (playable here), "torrent", "external", "youtube" or "unsupported". */
export function streamKind(stream) {
  if (stream.url && /^https?:\/\//i.test(stream.url)) return 'http';
  if (stream.url && /^(rtsp|rtmp|mms|ftp|smb):\/\//i.test(stream.url)) return 'http';
  if (stream.infoHash) return 'torrent';
  if (stream.ytId) return 'youtube';
  if (stream.externalUrl) return 'external';
  return 'unsupported';
}

export function isPlayable(stream) {
  return streamKind(stream) === 'http';
}

export function streamText(stream) {
  const hints = stream.behaviorHints || {};
  return [stream.name, stream.title, stream.description, hints.filename].filter(Boolean).join('\n');
}

function parseSize(text, hints) {
  if (hints && Number(hints.videoSize) > 0) return Number(hints.videoSize);
  const match = text.match(/(\d+(?:[.,]\d+)?)\s?(TB|TiB|GB|GiB|MB|MiB)\b/i);
  if (!match) return 0;
  const value = parseFloat(match[1].replace(',', '.'));
  const unit = match[2].toUpperCase();
  const factor = unit.startsWith('T') ? 1024 ** 4 : unit.startsWith('G') ? 1024 ** 3 : 1024 ** 2;
  return value * factor;
}

export function formatSize(bytes) {
  if (!bytes) return '';
  if (bytes >= 1024 ** 3) return (bytes / 1024 ** 3).toFixed(bytes >= 10 * 1024 ** 3 ? 0 : 1) + ' GB';
  return Math.round(bytes / 1024 ** 2) + ' MB';
}

/** Everything the stream list shows about a result. */
export function describeStream(stream, addonName) {
  const text = streamText(stream);
  const hints = stream.behaviorHints || {};
  const resolution = RESOLUTIONS.find((r) => r.re.test(text)) || null;
  const badges = [];
  if (/\b(DV|DoVi|dolby[ .]?vision)\b/i.test(text)) badges.push('DV');
  if (/\bHDR10\+/i.test(text)) badges.push('HDR10+');
  else if (/\bHDR(10)?\b/i.test(text)) badges.push('HDR');
  if (/\b(x265|h\.?265|hevc)\b/i.test(text)) badges.push('HEVC');
  else if (/\bav1\b/i.test(text)) badges.push('AV1');
  if (/\batmos\b/i.test(text)) badges.push('Atmos');
  if (/\btrue-?hd\b/i.test(text)) badges.push('TrueHD');
  else if (/\bdts[- ]?(hd|x|ma)\b/i.test(text)) badges.push('DTS-HD');
  else if (/\bdts\b/i.test(text)) badges.push('DTS');
  else if (/\b(ddp|dd\+|e-?ac-?3)\b/i.test(text)) badges.push('DD+');
  if (/\bremux\b/i.test(text)) badges.push('REMUX');
  const debridTag = text.match(DEBRID_TAGS);
  const cached = /⚡|\+\]|\bcached\b/i.test(text) && !/\bnot cached\b|\buncached\b/i.test(text);
  const size = parseSize(text, hints);
  const seeders = (text.match(/👤\s*(\d+)/) || [])[1];

  const nameLines = String(stream.name || '').split('\n').map((s) => s.trim()).filter(Boolean);
  const detail = String(stream.title || stream.description || '').trim();
  return {
    addonName,
    // Addons put their own name plus a short quality label in "name".
    name: nameLines.join(' ') || addonName,
    detail,
    resolution: resolution ? resolution.label : null,
    rank: resolution ? resolution.rank : 0,
    badges,
    size,
    sizeText: formatSize(size),
    seeders: seeders ? Number(seeders) : null,
    debrid: debridTag ? debridTag[1].toUpperCase() : null,
    cached,
    kind: streamKind(stream),
    bingeGroup: hints.bingeGroup || null,
  };
}

/** Request headers the stream asks the player to send (behaviorHints.proxyHeaders). */
export function streamHeaders(stream) {
  const request = stream.behaviorHints && stream.behaviorHints.proxyHeaders && stream.behaviorHints.proxyHeaders.request;
  if (!request || typeof request !== 'object') return {};
  const headers = {};
  for (const [key, value] of Object.entries(request)) if (typeof value === 'string') headers[key] = value;
  return headers;
}

export function sortStreams(entries, mode) {
  if (mode === 'quality') {
    return [...entries].sort((a, b) =>
      (b.info.rank - a.info.rank) || (Number(b.info.cached) - Number(a.info.cached)) || (b.info.size - a.info.size));
  }
  if (mode === 'size') return [...entries].sort((a, b) => b.info.size - a.info.size);
  return entries;
}

// How likely the primary (FFmpegInteropX) player is to open this stream first try. Lower is
// tried earlier. mp4/H.264 and adaptive HLS/DASH go first; mkv and streams whose
// titles promise HEVC/AV1/DTS/TrueHD/REMUX go last: FFmpeg can play those codecs,
// but they are heavier to decode, so the lighter streams are still tried first.
// Deferred sources are never removed, only tried later.
const RISKY_BADGES = { HEVC: 3, AV1: 3, 'DTS-HD': 2, DTS: 2, TrueHD: 2, DV: 2, 'HDR10+': 1, REMUX: 2 };
export function playbackScore(stream, info) {
  let score = 0;
  const path = String(stream.url || '').split('?')[0].toLowerCase();
  if (/\.m3u8$|\.mpd$/.test(path)) score -= 1;
  else if (/\.mp4$|\.m4v$/.test(path)) score += 0;
  else if (/\.mkv$/.test(path)) score += 3;
  else if (/\.webm$/.test(path)) score += 2;
  else score += 2; // No recognizable extension (e.g. /redirect): the container cannot be guessed from the URL.
  for (const badge of (info && info.badges) || []) score += RISKY_BADGES[badge] || 0;
  if (info && info.cached) score -= 1;
  return score;
}

/** Entries ({ stream, info }) ordered for playback attempts, best first. */
export function sortForPlayback(entries) {
  return [...entries].sort((a, b) =>
    (playbackScore(a.stream, a.info) - playbackScore(b.stream, b.info)) || (b.info.rank - a.info.rank));
}
