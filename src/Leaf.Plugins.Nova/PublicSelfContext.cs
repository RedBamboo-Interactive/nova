using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Nova;

/// <summary>The short lines a dreaming cycle writes at the top of <c>memory/dreaming/mood.md</c>.</summary>
internal sealed record MoodLines(string? Energy, string? Vibe, string? Color)
{
    /// <summary>The first <c>Energy:</c>, <c>Vibe:</c> and <c>Color:</c> lines, trimmed, label included.</summary>
    public static MoodLines Parse(IEnumerable<string> lines)
    {
        string? energy = null, vibe = null, color = null;
        foreach (var line in lines)
        {
            if (energy is null && line.StartsWith("Energy:", StringComparison.Ordinal)) energy = line.Trim();
            else if (vibe is null && line.StartsWith("Vibe:", StringComparison.Ordinal)) vibe = line.Trim();
            else if (color is null && line.StartsWith("Color:", StringComparison.Ordinal)) color = line.Trim();
        }
        return new(energy, vibe, color);
    }

    /// <summary>What follows the label, or null when there is nothing.</summary>
    public static string? Value(string? line)
    {
        if (line is null) return null;
        var value = line[(line.IndexOf(':') + 1)..].Trim();
        return value.Length == 0 ? null : value;
    }
}

/// <summary>The allowlisted Agent state a public session may know: the outfit and the mood line.</summary>
internal sealed record PublicSelfState(PublicSelfState.OutfitState? Outfit, PublicSelfState.MoodState? Mood)
{
    internal sealed record OutfitState(string Name, string? AssetId);

    internal sealed record MoodState(string? Energy, string? Vibe, string? Color);
}

/// <summary>
/// One turn's planned block. <see cref="Block"/> is sent with the outfit image attached,
/// <see cref="BlockWithoutImage"/> when there is none or the session refused it.
/// </summary>
internal sealed record PublicSelfContextTurn(
    string Block, string BlockWithoutImage, string? ImageAssetId, string Hash, bool Full, long TurnsSinceFull,
    string? ShownAssetId, string TurnKey)
{
    public string Render(bool imageAttached) => imageAttached ? Block : BlockWithoutImage;
}

/// <summary>
/// The trusted block that tells a public Agent session (Discord, Leaf Community) who it is right now:
/// the owner's local date and time, its current outfit with the image, and its short mood line.
/// Built server-side through an allowlist; never the outfit's <c>prompt</c> or <c>reasoning</c>, never an
/// nsfw outfit, never anything else from the private context.
/// </summary>
/// <remarks>
/// The full block goes out on a session's first turn, whenever the outfit or mood changes, while the
/// current outfit image has not been shown, and every <see cref="RefreshTurns"/> turns (the guild
/// server_map rule); other turns carry a compact reference with the current time. A turn's block is
/// stored under its admission key before it is sent, so a retry sends the same bytes.
/// </remarks>
public sealed class PublicSelfContext(
    IEntityStore entities,
    IRecordStreams records,
    ILogger<PublicSelfContext> logger,
    IAssets? assets = null,
    AgentWorkspaces? workspaces = null,
    TimeProvider? time = null)
{
    internal const string Stream = "public-self-context";
    internal const string Schema = "leaf-public-self-context/v1";
    internal const string ReferenceSchema = "leaf-public-self-context-ref/v1";
    internal const string Tag = "public-self-context-json";
    internal const int RefreshTurns = 25;
    internal const int HashLength = 16;
    internal const int MaxOutfitNameLength = 120;
    internal const int MaxMoodLength = 200;

    internal const string Heading =
        "Public self-context JSON (trusted, generated locally from your own Agent state; no participant wrote it):";

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);

    internal static bool AppliesTo(string transport)
        => string.Equals(transport, "discord", StringComparison.OrdinalIgnoreCase)
           || string.Equals(transport, "leaf-commons", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The block for one turn: the stored one when this admission key was planned before, otherwise a
    /// new one, stored before it is returned. <paramref name="imageSlots"/> is how many more images the
    /// turn can carry.
    /// </summary>
    internal async Task<PublicSelfContextTurn> PlanAsync(
        string admissionKey, string sessionId, string agentId, string ownerId, int imageSlots,
        CancellationToken ct = default)
    {
        var turnKey = TurnKey(admissionKey);
        var gate = gates.GetOrAdd(sessionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (await FindTurnAsync(turnKey, ct) is { } stored) return stored;

            var prior = await LatestAdmittedAsync(sessionId, ct);
            var state = await ReadStateAsync(agentId, ct);
            var planned = await PlanCoreAsync(state, prior, Now(), imageSlots, turnKey, ct);
            var data = TurnJson(planned, sessionId);
            if (!await records.AppendOnceAsync(Stream, turnKey, data, userId: ownerId, ct: ct))
                return await FindTurnAsync(turnKey, ct)
                    ?? throw new InvalidOperationException("The planned public self-context could not be read back");
            return planned;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Records that the session received this turn, so later turns compare against it.</summary>
    internal async Task MarkAdmittedAsync(
        PublicSelfContextTurn turn, string sessionId, string ownerId, CancellationToken ct = default)
    {
        await records.AppendOnceAsync(Stream, "admitted:" + turn.TurnKey, new JsonObject
        {
            ["kind"] = "admitted",
            ["session_id"] = sessionId,
            ["turn"] = turn.TurnKey,
            ["hash"] = turn.Hash,
            ["turns_since_full"] = turn.TurnsSinceFull,
            ["shown_asset_id"] = turn.ShownAssetId,
        }, SessionKey(sessionId), ownerId, ct);
    }

    /// <summary>The outfit image to attach, read from its Asset, or null when it is not a showable image.</summary>
    internal async Task<(string MediaType, byte[] Bytes)?> ReadImageAsync(string assetId, CancellationToken ct)
    {
        if (assets is null) return null;
        try
        {
            var file = await assets.ReadAsync(assetId, ct);
            var mediaType = file is null ? null : ExternalConversationImages.Displayable(file.Bytes);
            return mediaType is null ? null : (mediaType, file!.Bytes);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Outfit image {AssetId} could not be read for a public session", assetId);
            return null;
        }
    }

    private DateTimeOffset Now()
        => TimeZoneInfo.ConvertTime(clock.GetUtcNow(), clock.LocalTimeZone);

    private async Task<PublicSelfContextTurn> PlanCoreAsync(
        PublicSelfState state, AdmittedState? prior, DateTimeOffset now, int imageSlots, string turnKey,
        CancellationToken ct)
    {
        var hash = Hash(state);
        var assetId = state.Outfit?.AssetId;
        var wantImage = assetId is not null && !string.Equals(prior?.ShownAssetId, assetId, StringComparison.Ordinal);
        var turnsSinceFull = prior is null ? 0 : prior.TurnsSinceFull + 1;
        var full = prior is null
                   || !string.Equals(prior.Hash, hash, StringComparison.Ordinal)
                   || turnsSinceFull >= RefreshTurns
                   || wantImage;
        if (full) turnsSinceFull = 0;
        // With no image slot left the outfit image waits for a later turn. An image that cannot be
        // shown at all counts as handled, so it does not make every later turn a full block.
        string? attach = null;
        var waiting = wantImage && imageSlots <= 0;
        if (wantImage && !waiting && await ReadImageAsync(assetId!, ct) is not null)
            attach = assetId;
        var shown = assetId is null ? null : waiting ? prior?.ShownAssetId : assetId;
        var zone = clock.LocalTimeZone;
        return full
            ? new(Wrap(Full(state, hash, now, zone, imageAttached: attach is not null)),
                Wrap(Full(state, hash, now, zone, imageAttached: false)),
                attach, hash, true, turnsSinceFull, shown, turnKey)
            : new(Wrap(Reference(hash, turnsSinceFull, now, zone)), Wrap(Reference(hash, turnsSinceFull, now, zone)),
                null, hash, false, turnsSinceFull, shown, turnKey);
    }

    internal static string Wrap(JsonObject block)
        => $"{Heading}\n<{Tag}>{block.ToJsonString()}</{Tag}>";

    internal static JsonObject Full(
        PublicSelfState state, string hash, DateTimeOffset now, TimeZoneInfo zone, bool imageAttached)
        => new()
        {
            ["schema"] = Schema,
            ["hash"] = hash,
            ["now"] = NowJson(now, zone),
            ["outfit"] = state.Outfit is { } outfit
                ? new JsonObject
                {
                    ["name"] = outfit.Name,
                    ["assetId"] = outfit.AssetId,
                    ["markdown"] = outfit.AssetId is null ? null : $"![{MarkdownAlt(outfit.Name)}](/api/assets/{outfit.AssetId})",
                    ["imageAttached"] = imageAttached,
                }
                : null,
            ["mood"] = state.Mood is { } mood
                ? new JsonObject { ["energy"] = mood.Energy, ["vibe"] = mood.Vibe, ["color"] = mood.Color }
                : null,
        };

    internal static JsonObject Reference(string hash, long turnsSinceFull, DateTimeOffset now, TimeZoneInfo zone)
        => new()
        {
            ["schema"] = ReferenceSchema,
            ["unchanged"] = true,
            ["hash"] = hash,
            ["turnsSinceFull"] = turnsSinceFull,
            ["now"] = NowJson(now, zone),
        };

    // The offset is a number: the default encoder, which keeps '<' and '>' out of the frame, writes '+' as +.
    internal static JsonObject NowJson(DateTimeOffset now, TimeZoneInfo zone) => new()
    {
        ["date"] = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["weekday"] = now.ToString("dddd", CultureInfo.InvariantCulture),
        ["localTime"] = now.ToString("HH:mm", CultureInfo.InvariantCulture),
        ["timeZone"] = IanaId(zone),
        ["utcOffsetMinutes"] = (int)now.Offset.TotalMinutes,
    };

    private static string IanaId(TimeZoneInfo zone)
        => zone.HasIanaId ? zone.Id
            : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : zone.Id;

    // Square brackets would end the alt text early; the name is otherwise kept as written.
    private static string MarkdownAlt(string name) => name.Replace('[', '(').Replace(']', ')');

    /// <summary>The disclosed state's identity: what the session would see change, and nothing else.</summary>
    internal static string Hash(PublicSelfState state)
    {
        var canonical = new JsonObject
        {
            ["outfit"] = state.Outfit is { } outfit
                ? new JsonObject { ["name"] = outfit.Name, ["assetId"] = outfit.AssetId }
                : null,
            ["mood"] = state.Mood is { } mood
                ? new JsonObject { ["energy"] = mood.Energy, ["vibe"] = mood.Vibe, ["color"] = mood.Color }
                : null,
        }.ToJsonString();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant()[..HashLength];
    }

    /// <summary>
    /// The Agent's current outfit and mood, read through the allowlist. A field that cannot be read
    /// is left out rather than failing the turn.
    /// </summary>
    internal async Task<PublicSelfState> ReadStateAsync(string agentId, CancellationToken ct)
        => new(await ReadOutfitAsync(agentId, ct), await ReadMoodAsync(agentId, ct));

    /// <summary>
    /// The Agent's selected outfit, matching the Outfits read contract: the Agent's <c>outfit</c> field names an
    /// <c>outfit</c> entity of that same Agent. Only its name and local Asset are read; an outfit flagged nsfw
    /// (as Outfits reads the flag) is absent here.
    /// </summary>
    private async Task<PublicSelfState.OutfitState?> ReadOutfitAsync(string agentId, CancellationToken ct)
    {
        try
        {
            if (!Guid.TryParse(agentId, out var agentGuid)) return null;
            var agent = await entities.GetAsync(agentGuid, ct);
            if (agent?.TypeSlug != "agent"
                || agent.Data["outfit"] is not JsonValue outfitValue
                || !outfitValue.TryGetValue<string>(out var outfitRef)
                || !Guid.TryParse(outfitRef, out var outfitId))
                return null;
            var outfit = await entities.GetAsync(outfitId, ct);
            if (outfit?.TypeSlug != "outfit"
                || !string.Equals(String(outfit.Data, "agent"), agent.Id.ToString(), StringComparison.OrdinalIgnoreCase)
                || IsNsfw(outfit.Data))
                return null;
            var name = Bounded(outfit.Name, MaxOutfitNameLength);
            return name is null ? null : new(name, LocalAssetId(String(outfit.Data, "asset")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The outfit of Agent {AgentId} could not be read for a public session", agentId);
            return null;
        }
    }

    private async Task<PublicSelfState.MoodState?> ReadMoodAsync(string agentId, CancellationToken ct)
    {
        if (workspaces is null) return null;
        try
        {
            var workspace = await workspaces.TryGetAsync(agentId, ct);
            if (workspace is null) return null;
            var path = Path.Combine(workspace.MemoryPath, "dreaming", "mood.md");
            if (!File.Exists(path)) return null;
            var lines = MoodLines.Parse(await File.ReadAllLinesAsync(path, ct));
            var mood = new PublicSelfState.MoodState(
                Bounded(MoodLines.Value(lines.Energy), MaxMoodLength),
                Bounded(MoodLines.Value(lines.Vibe), MaxMoodLength),
                Bounded(MoodLines.Value(lines.Color), MaxMoodLength));
            return mood is { Energy: null, Vibe: null, Color: null } ? null : mood;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The mood of Agent {AgentId} could not be read for a public session", agentId);
            return null;
        }
    }

    /// <summary>The Outfits plugin's own reading of its flag: true, or the string "true".</summary>
    internal static bool IsNsfw(JsonObject data)
        => data["nsfw"] is JsonValue value
           && (value.TryGetValue<bool>(out var flag) ? flag
               : value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed) && parsed);

    /// <summary>The Asset id of a local <c>/api/assets/{id}</c> reference, absolute loopback URLs included.</summary>
    internal static string? LocalAssetId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var absolute))
        {
            if (!absolute.IsLoopback || absolute.Scheme is not ("http" or "https")) return null;
            candidate = absolute.AbsolutePath;
        }
        return ExternalAgentConversationProvider.AssetIdFromReference(candidate);
    }

    private static string? Bounded(string? value, int maximumLength)
    {
        if (value is null) return null;
        var clean = new string(value.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (clean.Length == 0) return null;
        return clean.Length <= maximumLength ? clean : clean[..maximumLength].TrimEnd() + "…";
    }

    private static string? String(JsonObject data, string key)
        => data[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    internal sealed record AdmittedState(string Hash, long TurnsSinceFull, string? ShownAssetId);

    private async Task<AdmittedState?> LatestAdmittedAsync(string sessionId, CancellationToken ct)
    {
        var latest = (await records.QueryAsync(Stream, new RecordQuery
        {
            EntityId = SessionKey(sessionId),
            Limit = 1,
        }, ct)).FirstOrDefault();
        if (latest is null
            || !string.Equals(String(latest.Data, "session_id"), sessionId, StringComparison.Ordinal)
            || String(latest.Data, "hash") is not { } hash)
            return null;
        var turns = latest.Data["turns_since_full"] is JsonValue value && value.TryGetValue<long>(out var count)
            ? Math.Max(0, count) : 0;
        return new(hash, turns, String(latest.Data, "shown_asset_id"));
    }

    private async Task<PublicSelfContextTurn?> FindTurnAsync(string turnKey, CancellationToken ct)
    {
        var stored = (await records.QueryAsync(Stream, new RecordQuery { ExternalId = turnKey, Limit = 1 }, ct))
            .FirstOrDefault();
        if (stored is null) return null;
        var data = stored.Data;
        if (String(data, "block") is not { } block
            || String(data, "block_without_image") is not { } withoutImage
            || String(data, "hash") is not { } hash)
            throw new InvalidOperationException($"Stored public self-context {turnKey} is malformed");
        return new(block, withoutImage, String(data, "image_asset_id"), hash,
            data["full"] is JsonValue full && full.TryGetValue<bool>(out var isFull) && isFull,
            data["turns_since_full"] is JsonValue turns && turns.TryGetValue<long>(out var count) ? count : 0,
            String(data, "shown_asset_id"), turnKey);
    }

    private static JsonObject TurnJson(PublicSelfContextTurn turn, string sessionId) => new()
    {
        ["kind"] = "turn",
        ["session_id"] = sessionId,
        ["hash"] = turn.Hash,
        ["full"] = turn.Full,
        ["turns_since_full"] = turn.TurnsSinceFull,
        ["image_asset_id"] = turn.ImageAssetId,
        ["shown_asset_id"] = turn.ShownAssetId,
        ["block"] = turn.Block,
        ["block_without_image"] = turn.BlockWithoutImage,
    };

    /// <summary>The record key of one admission: its RedCompute idempotency key, hashed to a bounded length.</summary>
    internal static string TurnKey(string admissionKey)
        => "turn:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(admissionKey))).ToLowerInvariant();

    /// <summary>
    /// The record parent key of one session's admitted turns. Records are queried by parent, and a session has no
    /// entity of its own, so this is a name-derived identifier, not an entity reference.
    /// </summary>
    internal static Guid SessionKey(string sessionId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("nova:public-self-context:session\n" + sessionId));
        return new Guid(bytes.AsSpan(0, 16));
    }
}
