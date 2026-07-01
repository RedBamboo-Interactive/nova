using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using RedBamboo.AppHost.Auth;
using RedBamboo.AppHost.Logging;

namespace Nova.App.Services;

public class LivePollerService
{
    private readonly LogService _log;
    private readonly string _redLeafBase;

    private SpotifyState? _lastSpotify;
    private Dictionary<string, SonosRoomState> _lastSonos = [];
    private Dictionary<string, HueGroupState> _lastHueGroups = [];

    public LivePollerService(LogService log)
    {
        _log = log;
        _redLeafBase = App.Config.Suite.RedLeaf.TrimEnd('/');
    }

    private HttpClient BuildRedLeafClient()
    {
        var redSuiteDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RedSuite");
        var signingKey = SigningKeyPersistence.EnsureSigningKey(redSuiteDir);
        var jwt = new JwtService(new JwtOptions { SigningKey = signingKey });
        var token = jwt.GenerateAccessToken("local-user", "local@nova", "Nova System", ["admin"]);
        var http = new HttpClient
        {
            BaseAddress = new Uri(_redLeafBase + "/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        return http;
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _log.Info("live-poller", "Poller waiting 10s before first poll...");
        await Task.Delay(TimeSpan.FromSeconds(10), ct);
        _log.Info("live-poller", "Starting poll loops");

        var sonosTask = PollLoopAsync("sonos", TimeSpan.FromSeconds(5), PollSonosAsync, ct);
        var spotifyTask = PollLoopAsync("spotify", TimeSpan.FromSeconds(5), PollSpotifyAsync, ct);
        var hueTask = PollLoopAsync("hue", TimeSpan.FromSeconds(15), PollHueAsync, ct);

        await Task.WhenAll(sonosTask, spotifyTask, hueTask);
    }

    private async Task PollLoopAsync(string name, TimeSpan interval, Func<Task> poll, CancellationToken ct)
    {
        _log.Info("live-poller", $"[{name}] Poll loop starting (interval: {interval.TotalSeconds}s)");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await poll();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warn("live-poller", $"[{name}] Poll failed: {ex.Message}");
            }

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task PollSpotifyAsync()
    {
        var live = LiveEventService.Instance;
        if (live == null) return;

        JsonElement data;
        try
        {
            using var http = BuildRedLeafClient();
            var resp = await http.GetAsync("api/spotify/playback");
            if (!resp.IsSuccessStatusCode) return;
            data = await resp.Content.ReadFromJsonAsync<JsonElement>();
        }
        catch { return; }

        var playing = data.TryGetProperty("playing", out var p) && p.GetBoolean();
        var trackUri = data.TryGetProperty("track_uri", out var tu) ? tu.GetString() : null;
        var track = data.TryGetProperty("track", out var t) ? t.GetString() : null;
        var artist = data.TryGetProperty("artist", out var a) ? a.GetString() : null;
        var album = data.TryGetProperty("album", out var al) ? al.GetString() : null;
        var device = data.TryGetProperty("device", out var dev) && dev.TryGetProperty("name", out var dn) ? dn.GetString() : null;

        var current = new SpotifyState(playing, trackUri, track, artist);

        if (_lastSpotify == null)
        {
            _lastSpotify = current;
            return;
        }

        if (current.TrackUri != _lastSpotify.TrackUri && current.Playing && current.Track != null)
        {
            await live.PostAsync("spotify", $"Now playing: {current.Track} — {current.Artist}",
                new { track, artist, album, device });
        }
        else if (current.Playing != _lastSpotify.Playing)
        {
            if (!current.Playing)
                await live.PostAsync("spotify", "Paused playback");
            else if (current.Track != null)
                await live.PostAsync("spotify", $"Resumed: {current.Track} — {current.Artist}");
        }

        _lastSpotify = current;
    }

    private async Task PollHueAsync()
    {
        var live = LiveEventService.Instance;
        if (live == null) return;

        JsonElement data;
        try
        {
            using var http = BuildRedLeafClient();
            var resp = await http.GetAsync("api/hue/groups");
            if (!resp.IsSuccessStatusCode) return;
            data = await resp.Content.ReadFromJsonAsync<JsonElement>();
        }
        catch { return; }

        var current = new Dictionary<string, HueGroupState>();

        foreach (var prop in data.EnumerateObject())
        {
            var id = prop.Name;
            var group = prop.Value;
            var name = group.TryGetProperty("name", out var n) ? n.GetString() ?? id : id;
            var on = group.TryGetProperty("action", out var action)
                     && action.TryGetProperty("on", out var onEl) && onEl.GetBoolean();
            var bri = action.TryGetProperty("bri", out var briEl) ? briEl.GetInt32() : 0;
            var type = group.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

            if (type is not "Room" and not "Zone") continue;

            current[id] = new HueGroupState(name, on, bri);
        }

        if (_lastHueGroups.Count == 0)
        {
            _lastHueGroups = current;
            return;
        }

        foreach (var (id, state) in current)
        {
            if (_lastHueGroups.TryGetValue(id, out var prev))
            {
                if (state.On != prev.On)
                {
                    var action = state.On ? "turned on" : "turned off";
                    await live.PostAsync("hue", $"{state.Name} lights {action}");
                }
            }
        }

        _lastHueGroups = current;
    }

    private async Task PollSonosAsync()
    {
        var live = LiveEventService.Instance;
        if (live == null) { _log.Warn("live-poller", "Sonos: LiveEventService.Instance is null"); return; }

        JsonElement data;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var resp = await http.GetAsync("http://localhost:5005/zones");
            if (!resp.IsSuccessStatusCode) { _log.Warn("live-poller", $"Sonos poll: HTTP {(int)resp.StatusCode}"); return; }
            data = await resp.Content.ReadFromJsonAsync<JsonElement>();
        }
        catch (Exception ex) { _log.Warn("live-poller", $"Sonos poll failed: {ex.Message}"); return; }

        var current = new Dictionary<string, SonosRoomState>();

        foreach (var zone in data.EnumerateArray())
        {
            if (!zone.TryGetProperty("coordinator", out var coord)) continue;
            var room = coord.TryGetProperty("roomName", out var rn) ? rn.GetString() : null;
            if (room == null) continue;

            if (!coord.TryGetProperty("state", out var state)) continue;
            var playback = state.TryGetProperty("playbackState", out var ps) ? ps.GetString() : null;
            var playing = playback == "PLAYING";

            string? track = null, artist = null;
            if (state.TryGetProperty("currentTrack", out var ct))
            {
                track = ct.TryGetProperty("title", out var tt) ? tt.GetString() : null;
                artist = ct.TryGetProperty("artist", out var at) ? at.GetString() : null;
            }

            var trackKey = $"{track}:{artist}";
            current[room] = new SonosRoomState(room, playing, track, artist, trackKey);
        }

        if (_lastSonos.Count == 0)
        {
            _log.Info("live-poller", $"Sonos baseline: {current.Count} rooms ({string.Join(", ", current.Values.Where(v => v.Playing).Select(v => $"{v.Room}: {v.Track}"))})");
            _lastSonos = current;
            return;
        }

        foreach (var (room, state) in current)
        {
            if (_lastSonos.TryGetValue(room, out var prev))
            {
                if (state.Playing && state.TrackKey != prev.TrackKey && state.Track != null)
                {
                    var msg = $"Now playing in {room}: {state.Track} — {state.Artist}";
                    _log.Info("live-poller", $"Sonos track change: {room} → {state.Track}");
                    try
                    {
                        await live.PostAsync("sonos", msg, new { room, track = state.Track, artist = state.Artist });
                        _log.Info("live-poller", $"Posted to LIVE: {msg}");
                    }
                    catch (Exception ex)
                    {
                        _log.Error("live-poller", $"PostAsync threw: {ex}");
                    }
                }
                else if (state.Playing != prev.Playing)
                {
                    if (state.Playing && state.Track != null)
                        await live.PostAsync("sonos", $"{room}: Resumed {state.Track} — {state.Artist}");
                    else
                        await live.PostAsync("sonos", $"{room}: Paused");
                }
            }
            else if (state.Playing && state.Track != null)
            {
                await live.PostAsync("sonos", $"Now playing in {room}: {state.Track} — {state.Artist}",
                    new { room, track = state.Track, artist = state.Artist });
            }
        }

        _lastSonos = current;
    }

    private record SpotifyState(bool Playing, string? TrackUri, string? Track, string? Artist);
    private record SonosRoomState(string Room, bool Playing, string? Track, string? Artist, string TrackKey);
    private record HueGroupState(string Name, bool On, int Brightness);
}
