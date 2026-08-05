using System.Net.Http.Json;
using System.Text.Json;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Ambient LIVE-timeline enrichment for Smart Home state: polls Spotify, Sonos,
/// and Hue. Other installed extensions may contribute their own events through
/// Nova's generic live-event slot.
/// Baseline-first: the initial reading of each source is swallowed so a boot never
/// spams the timeline. Runs until ApplicationStopping (plugins have no shutdown hook).
/// </summary>
public sealed class LivePoller(LiveEvents live)
{
    // Same-process kernel API. Local requests pass auth (LocalDefault); the plugin
    // has no way to mint suite JWTs — SDK gap, but loopback doesn't need one.
    private const string KernelBase = "http://127.0.0.1:18804";
    private const string SonosBase = "http://localhost:5005";

    private SpotifyState? _lastSpotify;
    private Dictionary<string, SonosRoomState> _lastSonos = [];
    private Dictionary<string, HueGroupState> _lastHueGroups = [];
    private readonly HttpClient _kernel = new() { BaseAddress = new Uri(KernelBase), Timeout = TimeSpan.FromSeconds(10) };
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task RunAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(10), ct);

        var tasks = new List<Task>
        {
            PollLoopAsync(TimeSpan.FromSeconds(5), PollSonosAsync, ct),
            PollLoopAsync(TimeSpan.FromSeconds(5), PollSpotifyAsync, ct),
            PollLoopAsync(TimeSpan.FromSeconds(15), PollHueAsync, ct),
        };
        await Task.WhenAll(tasks);
    }

    private static async Task PollLoopAsync(TimeSpan interval, Func<Task> poll, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await poll(); }
            catch (Exception) { /* per-source failures never kill the loop */ }

            try { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // ── Spotify (via smart-home plugin) ────────────────────────────────

    private async Task PollSpotifyAsync()
    {
        JsonElement data;
        try
        {
            var resp = await _kernel.GetAsync("/api/apps/smart-home/spotify/playback");
            if (!resp.IsSuccessStatusCode) return;
            data = await resp.Content.ReadFromJsonAsync<JsonElement>();
        }
        catch { return; }

        var playing = data.TryGetProperty("playing", out var p) && p.GetBoolean();
        var trackUri = data.TryGetProperty("track_uri", out var tu) ? tu.GetString() : null;
        var track = data.TryGetProperty("track", out var t) ? t.GetString() : null;
        var artist = data.TryGetProperty("artist", out var a) ? a.GetString() : null;
        var album = data.TryGetProperty("album", out var al) ? al.GetString() : null;
        var albumArt = data.TryGetProperty("album_art", out var art) ? art.GetString() : null;
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
                new { track, artist, album, albumArt, device });
        }
        else if (current.Playing != _lastSpotify.Playing)
        {
            if (!current.Playing)
                await live.PostAsync("spotify", "Paused playback",
                    new { track = current.Track ?? _lastSpotify.Track, artist = current.Artist ?? _lastSpotify.Artist, albumArt, device, status = "paused" });
            else if (current.Track != null)
                await live.PostAsync("spotify", $"Resumed: {current.Track} — {current.Artist}",
                    new { track, artist, album, albumArt, device, status = "resumed" });
        }

        _lastSpotify = current;
    }

    // ── Hue (via smart-home plugin) ────────────────────────────────────

    private async Task PollHueAsync()
    {
        JsonElement data;
        try
        {
            var resp = await _kernel.GetAsync("/api/apps/smart-home/hue/groups");
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
            if (_lastHueGroups.TryGetValue(id, out var prev) && state.On != prev.On)
            {
                var action = state.On ? "turned on" : "turned off";
                await live.PostAsync("hue", $"{state.Name} lights {action}",
                    new { room = state.Name, id, on = state.On, brightness = state.Brightness });
            }
        }

        _lastHueGroups = current;
    }

    // ── Sonos (node-sonos-http-api bridge, smart-home managed process) ─

    private async Task PollSonosAsync()
    {
        JsonElement data;
        try
        {
            var resp = await _http.GetAsync($"{SonosBase}/zones");
            if (!resp.IsSuccessStatusCode) return;
            data = await resp.Content.ReadFromJsonAsync<JsonElement>();
        }
        catch { return; }

        var current = new Dictionary<string, SonosRoomState>();

        foreach (var zone in data.EnumerateArray())
        {
            if (!zone.TryGetProperty("coordinator", out var coord)) continue;
            var room = coord.TryGetProperty("roomName", out var rn) ? rn.GetString() : null;
            if (room == null) continue;

            if (!coord.TryGetProperty("state", out var state)) continue;
            var playback = state.TryGetProperty("playbackState", out var ps) ? ps.GetString() : null;
            var playing = playback == "PLAYING";

            string? track = null, artist = null, albumArt = null;
            if (state.TryGetProperty("currentTrack", out var trackEl))
            {
                track = trackEl.TryGetProperty("title", out var tt) ? tt.GetString() : null;
                artist = trackEl.TryGetProperty("artist", out var at) ? at.GetString() : null;
                // May be a LAN-only URL — the renderer hides album art that fails to load.
                albumArt = trackEl.TryGetProperty("absoluteAlbumArtUri", out var aa) ? aa.GetString() : null;
            }

            current[room] = new SonosRoomState(room, playing, track, artist, $"{track}:{artist}", albumArt);
        }

        if (_lastSonos.Count == 0)
        {
            _lastSonos = current;
            return;
        }

        foreach (var (room, state) in current)
        {
            if (_lastSonos.TryGetValue(room, out var prev))
            {
                if (state.Playing && state.TrackKey != prev.TrackKey && state.Track != null)
                {
                    await live.PostAsync("sonos", $"Now playing in {room}: {state.Track} — {state.Artist}",
                        new { room, track = state.Track, artist = state.Artist, albumArt = state.AlbumArt });
                }
                else if (state.Playing != prev.Playing)
                {
                    if (state.Playing && state.Track != null)
                        await live.PostAsync("sonos", $"{room}: Resumed {state.Track} — {state.Artist}",
                            new { room, track = state.Track, artist = state.Artist, albumArt = state.AlbumArt, status = "resumed" });
                    else
                        await live.PostAsync("sonos", $"{room}: Paused",
                            new { room, track = state.Track, artist = state.Artist, albumArt = state.AlbumArt, status = "paused" });
                }
            }
            else if (state.Playing && state.Track != null)
            {
                await live.PostAsync("sonos", $"Now playing in {room}: {state.Track} — {state.Artist}",
                    new { room, track = state.Track, artist = state.Artist, albumArt = state.AlbumArt });
            }
        }

        _lastSonos = current;
    }


    private record SpotifyState(bool Playing, string? TrackUri, string? Track, string? Artist);
    private record SonosRoomState(string Room, bool Playing, string? Track, string? Artist, string TrackKey, string? AlbumArt);
    private record HueGroupState(string Name, bool On, int Brightness);
}
