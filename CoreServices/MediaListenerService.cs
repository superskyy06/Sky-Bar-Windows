using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace SkyBar.CoreServices;

public record LyricLine(TimeSpan Time, string Text);

public record MediaTrackInfo(
    string Title,
    string Artist,
    bool IsPlaying,
    BitmapImage? CoverArt,
    TimeSpan Position = default,
    TimeSpan Duration = default,
    string SourceAppName = "Media Player");

public sealed class MediaListenerService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private string _lastTrackId = string.Empty;
    private string _lastLyricsTrackId = string.Empty;

    private TimeSpan _basePosition = TimeSpan.Zero;
    private TimeSpan _duration = TimeSpan.Zero;
    private DateTimeOffset _lastPositionTimestamp = DateTimeOffset.UtcNow;
    private bool _isPlaying;

    private CancellationTokenSource? _lyricsCts;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly Regex LrcLineRegex = new(@"\[(\d{1,2}):(\d{2})(?:[\.\:](\d{1,3}))?\]\s*(.*)", RegexOptions.Compiled);
    private static readonly Regex NoiseTitleRegex = new(@"\s*[\(\[\|]\s*(official\s*(music\s*)?video|lyric\s*video|lyrics?|audio|visualizer|hd|4k|mv|remastered.*?)[\)\]\|\-].*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public IReadOnlyList<LyricLine> CurrentLyrics { get; private set; } = Array.Empty<LyricLine>();
    public bool HasSyncedLyrics { get; private set; }
    public string LyricsStatusText { get; private set; } = "Putar musik untuk menampilkan lirik tersinkronisasi";

    public event Action<MediaTrackInfo?, bool>? MediaStateUpdated;
    public event Action<IReadOnlyList<LyricLine>, bool, string>? LyricsUpdated;

    public MediaListenerService()
    {
        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "SkyBarWindows/1.0.0");
    }

    public async Task InitializeAsync()
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;
            AttachSession(_sessionManager.GetCurrentSession());
        }
        catch { }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        => AttachSession(sender.GetCurrentSession());

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
        _currentSession = session;
        if (_currentSession != null)
        {
            _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _currentSession.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            _ = RefreshMediaInfoAsync();
        }
        else
        {
            _isPlaying = false;
            _basePosition = TimeSpan.Zero;
            _duration = TimeSpan.Zero;
            CurrentLyrics = Array.Empty<LyricLine>();
            LyricsStatusText = "Tidak ada media yang diputar";
            MediaStateUpdated?.Invoke(null, false);
            LyricsUpdated?.Invoke(CurrentLyrics, false, LyricsStatusText);
        }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args) => _ = RefreshMediaInfoAsync();
    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, object args) => _ = RefreshMediaInfoAsync();
    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, object args) => _ = RefreshMediaInfoAsync();

    private async Task RefreshMediaInfoAsync()
    {
        if (_currentSession == null) return;
        try
        {
            var props = await _currentSession.TryGetMediaPropertiesAsync();
            var playback = _currentSession.GetPlaybackInfo();
            var timeline = _currentSession.GetTimelineProperties();
            if (props == null) return;

            bool isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            BitmapImage? coverBitmap = await ReadThumbnailAsync(props.Thumbnail);

            TimeSpan pos = timeline?.Position ?? TimeSpan.Zero;
            TimeSpan dur = timeline?.EndTime ?? TimeSpan.Zero;
            if (dur < pos) dur = pos;

            _basePosition = pos;
            _duration = dur;
            _isPlaying = isPlaying;
            _lastPositionTimestamp = timeline != null && timeline.LastUpdatedTime != default
                ? timeline.LastUpdatedTime
                : DateTimeOffset.UtcNow;

            string title = string.IsNullOrWhiteSpace(props.Title) ? "Media Aktif" : props.Title.Trim();
            string artist = string.IsNullOrWhiteSpace(props.Artist) ? "Artis Tidak Diketahui" : props.Artist.Trim();
            string sourceApp = FormatSourceAppName(_currentSession.SourceAppUserModelId);

            string currentTrackId = $"{title}|{artist}";
            bool isTrackChanged = !string.IsNullOrEmpty(props.Title) && currentTrackId != _lastTrackId;
            _lastTrackId = currentTrackId;

            TimeSpan livePos = GetLivePosition();

            MediaStateUpdated?.Invoke(new MediaTrackInfo(
                title,
                artist,
                isPlaying,
                coverBitmap,
                livePos,
                dur,
                sourceApp), isTrackChanged);

            if (currentTrackId != _lastLyricsTrackId && !string.IsNullOrWhiteSpace(props.Title))
            {
                _lastLyricsTrackId = currentTrackId;
                _ = FetchSyncedLyricsAsync(title, artist, dur);
            }
        }
        catch { }
    }

    /// <summary>
    /// Menghitung posisi waktu lagu secara real-time (interpolasi dari LastUpdatedTime sumber)
    /// sehingga slider musik & lirik tersinkronisasi presisi ke milidetik.
    /// </summary>
    public TimeSpan GetLivePosition()
    {
        TimeSpan pos = _basePosition;
        if (_isPlaying)
        {
            TimeSpan elapsed = DateTimeOffset.UtcNow - _lastPositionTimestamp;
            if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromHours(2))
                pos += elapsed;
        }
        if (_duration > TimeSpan.Zero && pos > _duration)
            pos = _duration;
        if (pos < TimeSpan.Zero)
            pos = TimeSpan.Zero;
        return pos;
    }

    public TimeSpan Duration => _duration;
    public bool IsPlaying => _isPlaying;

    /// <summary>
    /// Menggeser posisi lagu langsung ke sumber (Spotify, Browser, Media Player) saat user menggeser slider.
    /// </summary>
    public async Task SeekToPositionAsync(TimeSpan targetPosition)
    {
        if (_currentSession == null) return;
        try
        {
            _basePosition = targetPosition;
            _lastPositionTimestamp = DateTimeOffset.UtcNow;
            await _currentSession.TryChangePlaybackPositionAsync(targetPosition.Ticks);
        }
        catch { }
    }

    // ================================================================
    //  SINKRONISASI LIRIK DARI LRCLIB (AUTO-SYNC DENGAN LAGU AKTIF)
    // ================================================================

    private async Task FetchSyncedLyricsAsync(string rawTitle, string rawArtist, TimeSpan duration)
    {
        _lyricsCts?.Cancel();
        var cts = new CancellationTokenSource();
        _lyricsCts = cts;

        CurrentLyrics = Array.Empty<LyricLine>();
        HasSyncedLyrics = false;
        LyricsStatusText = "Menyinkronkan lirik...";
        LyricsUpdated?.Invoke(CurrentLyrics, false, LyricsStatusText);

        try
        {
            (string cleanTitle, string cleanArtist) = CleanTrackMetadata(rawTitle, rawArtist);
            string query = Uri.EscapeDataString($"{cleanTitle} {cleanArtist}".Trim());
            string url = $"https://lrclib.net/api/search?q={query}";

            string json = await Http.GetStringAsync(url, cts.Token);
            if (cts.Token.IsCancellationRequested) return;

            using JsonDocument doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            {
                // Coba pencarian kedua hanya dengan judul bersih
                string fallbackUrl = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(cleanTitle)}";
                json = await Http.GetStringAsync(fallbackUrl, cts.Token);
                if (cts.Token.IsCancellationRequested) return;
                using JsonDocument doc2 = JsonDocument.Parse(json);
                if (doc2.RootElement.ValueKind == JsonValueKind.Array && doc2.RootElement.GetArrayLength() > 0)
                {
                    ParseBestLyricMatch(doc2.RootElement, duration);
                    return;
                }

                LyricsStatusText = "Lirik belum tersedia untuk lagu ini";
                LyricsUpdated?.Invoke(CurrentLyrics, false, LyricsStatusText);
                return;
            }

            ParseBestLyricMatch(arr, duration);
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!cts.Token.IsCancellationRequested)
            {
                LyricsStatusText = "Tidak dapat memuat lirik saat ini";
                LyricsUpdated?.Invoke(CurrentLyrics, false, LyricsStatusText);
            }
        }
    }

    private void ParseBestLyricMatch(JsonElement arrayElement, TimeSpan targetDuration)
    {
        string? bestSynced = null;
        string? bestPlain = null;
        double bestDiff = double.MaxValue;

        foreach (JsonElement item in arrayElement.EnumerateArray())
        {
            string? synced = item.TryGetProperty("syncedLyrics", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            string? plain = item.TryGetProperty("plainLyrics", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            double itemDur = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : 0;

            double diff = targetDuration.TotalSeconds > 5 && itemDur > 5
                ? Math.Abs(targetDuration.TotalSeconds - itemDur)
                : 0;

            if (!string.IsNullOrWhiteSpace(synced) && diff < bestDiff)
            {
                bestDiff = diff;
                bestSynced = synced;
            }
            if (bestPlain == null && !string.IsNullOrWhiteSpace(plain))
            {
                bestPlain = plain;
            }
        }

        if (!string.IsNullOrWhiteSpace(bestSynced))
        {
            var parsed = ParseLrcLines(bestSynced);
            if (parsed.Count > 0)
            {
                CurrentLyrics = parsed;
                HasSyncedLyrics = true;
                LyricsStatusText = "Lirik tersinkronisasi aktif";
                LyricsUpdated?.Invoke(CurrentLyrics, true, LyricsStatusText);
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(bestPlain))
        {
            var lines = new List<LyricLine>();
            string[] split = bestPlain.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            double stepSec = Math.Max(3.0, (targetDuration.TotalSeconds > 20 ? targetDuration.TotalSeconds : 180.0) / Math.Max(1, split.Length));
            for (int i = 0; i < split.Length; i++)
            {
                string txt = split[i].Trim();
                if (!string.IsNullOrEmpty(txt))
                    lines.Add(new LyricLine(TimeSpan.FromSeconds(i * stepSec), txt));
            }

            if (lines.Count > 0)
            {
                CurrentLyrics = lines;
                HasSyncedLyrics = false;
                LyricsStatusText = "Lirik standar";
                LyricsUpdated?.Invoke(CurrentLyrics, false, LyricsStatusText);
                return;
            }
        }

        LyricsStatusText = "Lirik belum tersedia untuk lagu ini";
        LyricsUpdated?.Invoke(CurrentLyrics, false, LyricsStatusText);
    }

    private static List<LyricLine> ParseLrcLines(string lrcContent)
    {
        var list = new List<LyricLine>();
        string[] rawLines = lrcContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

        foreach (string raw in rawLines)
        {
            Match m = LrcLineRegex.Match(raw);
            if (!m.Success) continue;

            int min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            int ms = 0;
            if (m.Groups[3].Success)
            {
                string frac = m.Groups[3].Value.PadRight(3, '0')[..3];
                int.TryParse(frac, NumberStyles.Integer, CultureInfo.InvariantCulture, out ms);
            }

            string text = m.Groups[4].Value.Trim();
            if (string.IsNullOrWhiteSpace(text)) text = "♪ • • •";

            list.Add(new LyricLine(new TimeSpan(0, 0, min, sec, ms), text));
        }

        list.Sort((a, b) => a.Time.CompareTo(b.Time));
        return list;
    }

    private static (string Title, string Artist) CleanTrackMetadata(string rawTitle, string rawArtist)
    {
        string title = NoiseTitleRegex.Replace(rawTitle, "").Trim();
        string artist = rawArtist
            .Replace("- Topic", "", StringComparison.OrdinalIgnoreCase)
            .Replace("VEVO", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

        // Jika judul YouTube mengandung "Artis - Judul Lagu"
        if (title.Contains(" - "))
        {
            string[] parts = title.Split(" - ", 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
            {
                return (parts[1], parts[0]);
            }
        }

        return (title, artist);
    }

    private static string FormatSourceAppName(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return "System Media";
        if (appId.Contains("Spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (appId.Contains("Chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome / Web";
        if (appId.Contains("msedge", StringComparison.OrdinalIgnoreCase)) return "Edge / Web";
        if (appId.Contains("Firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        if (appId.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase) || appId.Contains("iTunes", StringComparison.OrdinalIgnoreCase)) return "Apple Music";
        if (appId.Contains("ZuneMusic", StringComparison.OrdinalIgnoreCase) || appId.Contains("MediaPlayer", StringComparison.OrdinalIgnoreCase)) return "Windows Media";
        if (appId.Contains("VLC", StringComparison.OrdinalIgnoreCase)) return "VLC Player";

        string name = Path.GetFileNameWithoutExtension(appId);
        return string.IsNullOrWhiteSpace(name) ? "Media Player" : name;
    }

    private static async Task<BitmapImage?> ReadThumbnailAsync(IRandomAccessStreamReference? thumbnailRef)
    {
        if (thumbnailRef == null) return null;
        using IRandomAccessStreamWithContentType stream = await thumbnailRef.OpenReadAsync();
        using var netStream = stream.AsStreamForRead();
        using var memStream = new MemoryStream();
        await netStream.CopyToAsync(memStream);
        memStream.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = 140;
        bitmap.StreamSource = memStream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public async Task TogglePlayPauseAsync() { if (_currentSession != null) await _currentSession.TryTogglePlayPauseAsync(); }
    public async Task SkipNextAsync() { if (_currentSession != null) await _currentSession.TrySkipNextAsync(); }
    public async Task SkipPreviousAsync() { if (_currentSession != null) await _currentSession.TrySkipPreviousAsync(); }

    public void Dispose()
    {
        _lyricsCts?.Cancel();
        if (_sessionManager != null) _sessionManager.CurrentSessionChanged -= OnCurrentSessionChanged;
        AttachSession(null);
    }
}
