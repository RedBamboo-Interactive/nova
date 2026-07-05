using System.Net.Http.Json;
using System.Text.Json;

namespace Leaf.Plugins.Nova;

/// <summary>
/// Ambient LIVE-timeline enrichment, ported from the standalone app: polls Spotify,
/// Sonos, and Hue (through the smart-home plugin's kernel API), weather (open-meteo),
/// and Steam presence, and posts state CHANGES as events on the LIVE discussion.
/// Baseline-first: the initial reading of each source is swallowed so a boot never
/// spams the timeline. Runs until ApplicationStopping (plugins have no shutdown hook).
/// </summary>
public sealed class LivePoller(LiveEvents live, NovaConfigStore config)
{
    // Same-process kernel API. Local requests pass auth (LocalDefault); the plugin
    // has no way to mint suite JWTs — SDK gap, but loopback doesn't need one.
    private const string KernelBase = "http://127.0.0.1:18804";
    private const string SonosBase = "http://localhost:5005";

    private SpotifyState? _lastSpotify;
    private Dictionary<string, SonosRoomState> _lastSonos = [];
    private Dictionary<string, HueGroupState> _lastHueGroups = [];
    private WeatherState? _lastWeather;
    private SteamState? _lastSteam;
    private DateTime? _steamSessionStart;
    private List<string>? _steamFriendIds;

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
            PollLoopAsync(TimeSpan.FromMinutes(10), PollWeatherAsync, ct),
            PollLoopAsync(TimeSpan.FromMinutes(2), PollSteamAsync, ct),
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

    // ── Weather (open-meteo, Zurich) ───────────────────────────────────

    private async Task PollWeatherAsync()
    {
        JsonElement data;
        try
        {
            var resp = await _http.GetAsync(
                "https://api.open-meteo.com/v1/forecast?latitude=47.3769&longitude=8.5417" +
                "&current=temperature_2m,weather_code,wind_speed_10m,precipitation" +
                "&timezone=Europe/Zurich");
            if (!resp.IsSuccessStatusCode) return;
            data = await resp.Content.ReadFromJsonAsync<JsonElement>();
        }
        catch { return; }

        if (!data.TryGetProperty("current", out var current)) return;

        var temp = current.TryGetProperty("temperature_2m", out var t) ? t.GetDouble() : 0;
        var code = current.TryGetProperty("weather_code", out var wc) ? wc.GetInt32() : 0;
        var wind = current.TryGetProperty("wind_speed_10m", out var w) ? w.GetDouble() : 0;
        var precip = current.TryGetProperty("precipitation", out var p) ? p.GetDouble() : 0;

        var state = new WeatherState(temp, code, ClassifyWeather(code), wind, precip);

        if (_lastWeather == null)
        {
            _lastWeather = state;
            return;
        }

        var events = new List<string>();

        var tempDelta = Math.Abs(state.Temp - _lastWeather.Temp);
        if (tempDelta >= 3)
        {
            var dir = state.Temp > _lastWeather.Temp ? "up" : "down";
            events.Add($"Temperature {dir} to {state.Temp:F0}°C (was {_lastWeather.Temp:F0}°C)");
        }

        if (state.Condition != _lastWeather.Condition)
            events.Add($"Weather changed: {_lastWeather.Condition} → {state.Condition}");

        if (ClassifyWind(_lastWeather.Wind) != ClassifyWind(state.Wind))
            events.Add($"Wind now {ClassifyWind(state.Wind)} ({state.Wind:F0} km/h)");

        if (state.Precip > 0 && _lastWeather.Precip == 0)
            events.Add($"Precipitation started ({state.Precip:F1} mm)");
        else if (state.Precip == 0 && _lastWeather.Precip > 0)
            events.Add("Precipitation stopped");

        if (events.Count > 0)
        {
            await live.PostAsync("weather", string.Join(". ", events),
                new { temp = state.Temp, condition = state.Condition, wind = state.Wind, precip = state.Precip, code = state.Code });
        }

        _lastWeather = state;
    }

    private static string ClassifyWeather(int code) => code switch
    {
        0 => "Clear",
        1 or 2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 or 63 or 65 or 80 or 81 or 82 => "Rain",
        66 or 67 => "Freezing rain",
        71 or 73 or 75 or 77 or 85 or 86 => "Snow",
        95 or 96 or 99 => "Thunderstorm",
        _ => "Unknown",
    };

    private static string ClassifyWind(double kmh) => kmh switch
    {
        < 10 => "calm",
        < 30 => "breezy",
        < 50 => "windy",
        _ => "strong",
    };

    // ── Steam presence ─────────────────────────────────────────────────

    private async Task PollSteamAsync()
    {
        var (apiKey, steamId) = await GetSteamConfigAsync();
        if (apiKey == null || steamId == null) return;

        JsonElement playerData;
        try
        {
            var resp = await _http.GetAsync(
                $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={apiKey}&steamids={steamId}");
            if (!resp.IsSuccessStatusCode) return;
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            if (!json.TryGetProperty("response", out var r) || !r.TryGetProperty("players", out var players)
                || players.GetArrayLength() == 0) return;
            playerData = players[0];
        }
        catch { return; }

        var personaState = playerData.TryGetProperty("personastate", out var ps) ? ps.GetInt32() : 0;
        var game = playerData.TryGetProperty("gameextrainfo", out var gi) ? gi.GetString() : null;
        var gameId = playerData.TryGetProperty("gameid", out var gid) ? gid.GetString() : null;

        var current = new SteamState(personaState, game, gameId);

        if (_lastSteam == null)
        {
            _lastSteam = current;
            return;
        }

        if (current.Game != null && _lastSteam.Game == null)
        {
            _steamSessionStart = DateTime.UtcNow;
            var friends = await GetFriendsInGameAsync(apiKey, steamId, current.GameId);
            var withStr = friends.Count > 0 ? $" with {string.Join(", ", friends)}" : "";
            await live.PostAsync("steam", $"Started playing {current.Game}{withStr}",
                new { game = current.Game, gameId = current.GameId, friends, status = "playing" });
        }
        else if (current.Game != null && current.GameId != _lastSteam.GameId)
        {
            var duration = FormatDuration(_steamSessionStart);
            await live.PostAsync("steam", $"Stopped playing {_lastSteam.Game}{(duration != null ? $" ({duration})" : "")}",
                new { game = _lastSteam.Game, status = "stopped", duration });

            _steamSessionStart = DateTime.UtcNow;
            var friends = await GetFriendsInGameAsync(apiKey, steamId, current.GameId);
            var withStr = friends.Count > 0 ? $" with {string.Join(", ", friends)}" : "";
            await live.PostAsync("steam", $"Started playing {current.Game}{withStr}",
                new { game = current.Game, gameId = current.GameId, friends, status = "playing" });
        }
        else if (current.Game == null && _lastSteam.Game != null)
        {
            var duration = FormatDuration(_steamSessionStart);
            _steamSessionStart = null;
            await live.PostAsync("steam", $"Stopped playing {_lastSteam.Game}{(duration != null ? $" ({duration})" : "")}",
                new { game = _lastSteam.Game, status = "stopped", duration });
        }

        _lastSteam = current;
    }

    private (string? ApiKey, string? SteamId)? _steamConfigCache;
    private DateTime _steamConfigFetched = DateTime.MinValue;

    private async Task<(string? ApiKey, string? SteamId)> GetSteamConfigAsync()
    {
        if (_steamConfigCache is { } cached && DateTime.UtcNow - _steamConfigFetched < TimeSpan.FromMinutes(5))
            return cached;
        try
        {
            var c = await config.GetRawAsync();
            var result = (c["steam_api_key"]?.GetValue<string>(), c["steam_id"]?.GetValue<string>());
            _steamConfigCache = result;
            _steamConfigFetched = DateTime.UtcNow;
            return result;
        }
        catch
        {
            return (null, null);
        }
    }

    private async Task<List<string>> GetFriendsInGameAsync(string apiKey, string steamId, string? gameId)
    {
        if (gameId == null) return [];

        try
        {
            if (_steamFriendIds == null)
            {
                var resp = await _http.GetAsync(
                    $"https://api.steampowered.com/ISteamUser/GetFriendList/v1/?key={apiKey}&steamid={steamId}&relationship=friend");
                if (!resp.IsSuccessStatusCode) return [];
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
                if (!json.TryGetProperty("friendslist", out var fl) || !fl.TryGetProperty("friends", out var friends)) return [];

                _steamFriendIds = [];
                foreach (var f in friends.EnumerateArray())
                    if (f.TryGetProperty("steamid", out var sid))
                        _steamFriendIds.Add(sid.GetString()!);
            }

            // GetPlayerSummaries accepts up to 100 steamids
            var ids = string.Join(",", _steamFriendIds);
            var summResp = await _http.GetAsync(
                $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={apiKey}&steamids={ids}");
            if (!summResp.IsSuccessStatusCode) return [];
            var summJson = await summResp.Content.ReadFromJsonAsync<JsonElement>();
            if (!summJson.TryGetProperty("response", out var r) || !r.TryGetProperty("players", out var players)) return [];

            var names = new List<string>();
            foreach (var p in players.EnumerateArray())
            {
                var fGameId = p.TryGetProperty("gameid", out var fgid) ? fgid.GetString() : null;
                if (fGameId == gameId)
                {
                    var name = p.TryGetProperty("personaname", out var pn) ? pn.GetString() : null;
                    if (name != null) names.Add(name);
                }
            }
            return names;
        }
        catch { return []; }
    }

    private static string? FormatDuration(DateTime? start)
    {
        if (start == null) return null;
        var span = DateTime.UtcNow - start.Value;
        if (span.TotalHours >= 1)
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        return span.Minutes < 1 ? "< 1m" : $"{span.Minutes}m";
    }

    private record SpotifyState(bool Playing, string? TrackUri, string? Track, string? Artist);
    private record SonosRoomState(string Room, bool Playing, string? Track, string? Artist, string TrackKey, string? AlbumArt);
    private record HueGroupState(string Name, bool On, int Brightness);
    private record WeatherState(double Temp, int Code, string Condition, double Wind, double Precip);
    private record SteamState(int PersonaState, string? Game, string? GameId);
}
