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
    private WeatherState? _lastWeather;
    private SteamState? _lastSteam;
    private DateTime? _steamSessionStart;
    private List<string>? _steamFriendIds;

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
        var weatherTask = PollLoopAsync("weather", TimeSpan.FromMinutes(10), PollWeatherAsync, ct);

        var tasks = new List<Task> { sonosTask, spotifyTask, hueTask, weatherTask };

        var steamConfig = App.Config.Steam;
        if (!string.IsNullOrEmpty(steamConfig.ApiKey))
        {
            tasks.Add(PollLoopAsync("steam", TimeSpan.FromMinutes(2), () => PollSteamAsync(steamConfig), ct));
            _log.Info("live-poller", "Steam polling enabled");
        }
        else
        {
            _log.Info("live-poller", "Steam polling disabled (no API key configured)");
        }

        await Task.WhenAll(tasks);
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

    private async Task PollWeatherAsync()
    {
        var live = LiveEventService.Instance;
        if (live == null) return;

        JsonElement data;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var resp = await http.GetAsync(
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
        var condition = ClassifyWeather(code);

        var state = new WeatherState(temp, code, condition, wind, precip);

        if (_lastWeather == null)
        {
            _lastWeather = state;
            _log.Info("live-poller", $"Weather baseline: {temp:F0}°C, {condition}, wind {wind:F0} km/h");
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
        {
            events.Add($"Weather changed: {_lastWeather.Condition} → {state.Condition}");
        }

        var prevWindCat = ClassifyWind(_lastWeather.Wind);
        var currWindCat = ClassifyWind(state.Wind);
        if (prevWindCat != currWindCat)
        {
            events.Add($"Wind now {currWindCat} ({state.Wind:F0} km/h)");
        }

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

    private async Task PollSteamAsync(Configuration.SteamSettings config)
    {
        var live = LiveEventService.Instance;
        if (live == null) return;

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // Fetch player summary
        JsonElement playerData;
        try
        {
            var resp = await http.GetAsync(
                $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={config.ApiKey}&steamids={config.SteamId}");
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
            if (game != null)
                _log.Info("live-poller", $"Steam baseline: playing {game}");
            else
                _log.Info("live-poller", $"Steam baseline: not in game");
            return;
        }

        // Game started
        if (current.Game != null && _lastSteam.Game == null)
        {
            _steamSessionStart = DateTime.UtcNow;
            var friends = await GetFriendsInGame(http, config, current.GameId);
            var withStr = friends.Count > 0 ? $" with {string.Join(", ", friends)}" : "";
            await live.PostAsync("steam", $"Started playing {current.Game}{withStr}",
                new { game = current.Game, gameId = current.GameId, friends, status = "playing" });
        }
        // Game changed
        else if (current.Game != null && current.GameId != _lastSteam.GameId)
        {
            var duration = FormatDuration(_steamSessionStart);
            var durationStr = duration != null ? $" ({duration})" : "";
            await live.PostAsync("steam", $"Stopped playing {_lastSteam.Game}{durationStr}",
                new { game = _lastSteam.Game, status = "stopped", duration });

            _steamSessionStart = DateTime.UtcNow;
            var friends = await GetFriendsInGame(http, config, current.GameId);
            var withStr = friends.Count > 0 ? $" with {string.Join(", ", friends)}" : "";
            await live.PostAsync("steam", $"Started playing {current.Game}{withStr}",
                new { game = current.Game, gameId = current.GameId, friends, status = "playing" });
        }
        // Game stopped
        else if (current.Game == null && _lastSteam.Game != null)
        {
            var duration = FormatDuration(_steamSessionStart);
            var durationStr = duration != null ? $" ({duration})" : "";
            _steamSessionStart = null;
            await live.PostAsync("steam", $"Stopped playing {_lastSteam.Game}{durationStr}",
                new { game = _lastSteam.Game, status = "stopped", duration });
        }

        _lastSteam = current;
    }

    private async Task<List<string>> GetFriendsInGame(HttpClient http, Configuration.SteamSettings config, string? gameId)
    {
        if (gameId == null) return [];

        try
        {
            if (_steamFriendIds == null)
            {
                var resp = await http.GetAsync(
                    $"https://api.steampowered.com/ISteamUser/GetFriendList/v1/?key={config.ApiKey}&steamid={config.SteamId}&relationship=friend");
                if (!resp.IsSuccessStatusCode) return [];
                var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
                if (!json.TryGetProperty("friendslist", out var fl) || !fl.TryGetProperty("friends", out var friends)) return [];

                _steamFriendIds = [];
                foreach (var f in friends.EnumerateArray())
                    if (f.TryGetProperty("steamid", out var sid))
                        _steamFriendIds.Add(sid.GetString()!);

                _log.Info("live-poller", $"Steam friend list cached: {_steamFriendIds.Count} friends");
            }

            // GetPlayerSummaries accepts up to 100 steamids
            var ids = string.Join(",", _steamFriendIds);
            var summResp = await http.GetAsync(
                $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={config.ApiKey}&steamids={ids}");
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
    private record SonosRoomState(string Room, bool Playing, string? Track, string? Artist, string TrackKey);
    private record HueGroupState(string Name, bool On, int Brightness);
    private record WeatherState(double Temp, int Code, string Condition, double Wind, double Precip);
    private record SteamState(int PersonaState, string? Game, string? GameId);
}
