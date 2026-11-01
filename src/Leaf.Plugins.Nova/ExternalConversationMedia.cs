using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Leaf.Sdk.Services;

namespace Leaf.Plugins.Nova;

/// <summary>
/// What kind of file an inbound attachment is for the review: a picture the model may be shown as
/// it is, or media (video, an animation, a picture too large for the image bounds) that the
/// Sentinel samples or shrinks and the model sees only through the frames the review covered.
/// Decided from the bytes, never from the name or the declared type.
/// </summary>
internal static class ExternalConversationMedia
{
    /// <summary>The ceiling for one attachment, the Sentinel's.</summary>
    public const int MaxMediaBytes = UntrustedContentReviewRequest.MaxMediaBytes;

    private const int MaxStillPixels = 100_000_000;

    private static readonly HashSet<string> Mp4Brands = new(StringComparer.Ordinal)
    {
        "isom", "iso2", "iso3", "iso4", "iso5", "iso6", "mp41", "mp42", "mp71", "avc1", "M4V ", "M4VH", "M4VP",
        "dash", "msdh", "msix", "f4v ", "3gp4", "3gp5", "3gp6", "3gp7", "3g2a", "3g2b", "3g2c",
    };

    /// <summary>The Sentinel's video types, from the bytes; null for anything else.</summary>
    public static string? SniffVideo(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 12 && bytes.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = Encoding.ASCII.GetString(bytes.Slice(8, 4));
            if (brand == "qt  ") return "video/quicktime";
            return Mp4Brands.Contains(brand) ? "video/mp4" : null;
        }
        if (bytes.Length >= 4 && bytes[0] == 0x1A && bytes[1] == 0x45 && bytes[2] == 0xDF && bytes[3] == 0xA3)
        {
            var head = Encoding.Latin1.GetString(bytes[..Math.Min(bytes.Length, 64)]);
            if (head.Contains("webm", StringComparison.Ordinal)) return "video/webm";
            if (head.Contains("matroska", StringComparison.Ordinal)) return "video/x-matroska";
        }
        return null;
    }

    /// <summary>
    /// The media type to send the Sentinel when the bytes are media it can review but the model must
    /// not be shown as they are: a video, an animated GIF, or a PNG, JPEG, GIF or still WebP over the
    /// image bounds (5 MiB, 4096 px) and inside the media ceiling. Null for everything else, including
    /// an animated WebP (the Sentinel cannot sample it) and unreadable bytes.
    /// </summary>
    public static string? Reviewable(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxMediaBytes) return null;
        if (SniffVideo(bytes) is { } video) return video;
        var mediaType = ExternalConversationImages.Sniff(bytes);
        if (mediaType is null) return null;
        if (mediaType == "image/gif" && IsAnimatedGif(bytes)) return mediaType;
        if (ExternalConversationImages.Displayable(bytes) is not null) return null; // an ordinary showable picture
        var size = ExternalConversationImages.Dimensions(bytes, mediaType);
        if (size is not { Width: >= 1, Height: >= 1 }) return null;
        return (long)size.Value.Width * size.Value.Height <= MaxStillPixels
               && size.Value.Width <= 16_384 && size.Value.Height <= 16_384
            ? mediaType
            : null;
    }

    /// <summary>Bytes that are a GIF with more than one frame.</summary>
    public static bool IsAnimatedGifFile(byte[] bytes)
        => ExternalConversationImages.Sniff(bytes) == "image/gif" && IsAnimatedGif(bytes);

    /// <summary>A GIF with more than one image block. Bounded: it only walks block headers.</summary>
    public static bool IsAnimatedGif(ReadOnlySpan<byte> b)
    {
        if (b.Length < 13) return false;
        var i = 13;
        if ((b[10] & 0x80) != 0) i += 3 * (1 << ((b[10] & 0x07) + 1));
        var frames = 0;
        while (i < b.Length)
        {
            switch (b[i])
            {
                case 0x2C:
                    if (++frames > 1) return true;
                    if (i + 10 > b.Length) return false;
                    var flags = b[i + 9];
                    i += 10;
                    if ((flags & 0x80) != 0) i += 3 * (1 << ((flags & 0x07) + 1));
                    i = SkipSubBlocks(b, i + 1);
                    break;
                case 0x21:
                    i = SkipSubBlocks(b, i + 2);
                    break;
                default:
                    return false;
            }
            if (i < 0) return false;
        }
        return false;
    }

    private static int SkipSubBlocks(ReadOnlySpan<byte> b, int i)
    {
        while (i < b.Length)
        {
            var size = b[i];
            i += 1 + size;
            if (size == 0) return i;
        }
        return -1;
    }

    // ---- what the model is shown ----

    /// <summary>
    /// Spreads the per-turn image budget over the attachments: a picture shown as it is takes one,
    /// and each media attachment shares what is left, at least one frame each. The frames come from
    /// the review only, evenly across the frames it covered and always including the first and last.
    /// </summary>
    /// <returns>The shown images in attachment order, or null when a media attachment has no reviewed
    /// frame, which means the review did not cover what the model would be shown.</returns>
    public static IReadOnlyList<ShownImage>? Allocate(
        IReadOnlyList<InboundAttachmentImage> attachments, IReadOnlyList<UntrustedContentReviewedFrame> reviewed,
        int budget)
    {
        var stills = attachments.Count(attachment => !attachment.Media);
        var media = attachments.Count - stills;
        var perMedia = media == 0 ? 0 : Math.Max(1, (budget - stills) / media);
        var shown = new List<ShownImage>();
        for (var index = 0; index < attachments.Count; index++)
        {
            var attachment = attachments[index];
            if (!attachment.Media)
            {
                shown.Add(new ShownImage(index, attachment.MediaType, attachment.Bytes, 0, 0, 0));
                continue;
            }
            var frames = reviewed.Where(frame => frame.AttachmentIndex == index)
                .OrderBy(frame => frame.TimestampSeconds).ToList();
            if (frames.Count == 0 || frames.Any(frame => frame.Bytes.Length == 0)) return null;
            foreach (var frame in Spread(frames, perMedia))
                shown.Add(new ShownImage(index, frame.MediaType, frame.Bytes, frame.TimestampSeconds,
                    frames.Count, frame.DurationSeconds));
        }
        return shown;
    }

    private static IEnumerable<UntrustedContentReviewedFrame> Spread(
        IReadOnlyList<UntrustedContentReviewedFrame> frames, int count)
    {
        if (count >= frames.Count) return frames;
        if (count == 1) return [frames[0]];
        return Enumerable.Range(0, count)
            .Select(k => frames[(int)Math.Round(k * (frames.Count - 1) / (double)(count - 1))])
            .Distinct();
    }

    // ---- what the Agent is told ----

    /// <summary>
    /// Lines the Agent reads beside the envelope, written here from the review's own numbers and the
    /// attachment ids the bridge assigned. A participant's file name never appears in them.
    /// </summary>
    public static IReadOnlyList<string> Notes(
        IReadOnlyList<InboundAttachmentImage> attachments, IReadOnlyList<ShownImage> shown,
        IReadOnlyList<SkippedAttachment> skipped, bool framesAttached)
    {
        var notes = new List<string>();
        for (var index = 0; index < attachments.Count; index++)
        {
            var attachment = attachments[index];
            if (!attachment.Media) continue;
            var mine = shown.Where(item => item.AttachmentIndex == index).ToList();
            var covered = mine.FirstOrDefault()?.ReviewedFrames ?? 0;
            var duration = mine.FirstOrDefault()?.DurationSeconds ?? 0;
            var shownCount = framesAttached ? mine.Count : 0;
            var seeing = framesAttached
                ? $"You are seeing {shownCount} of the {covered} frames the Sentinel reviewed"
                : $"You are not being shown any of the {covered} frames the Sentinel reviewed (the session could not take images)";
            notes.Add(attachment.MediaType.StartsWith("video/", StringComparison.Ordinal)
                ? $"Attachment {attachment.AttachmentId} is a video {Seconds(duration)} s long. {seeing}, sampled across the clip, " +
                  "not every moment. Audio was not processed."
                : attachment.MediaType == "image/gif" || duration > 0
                    ? $"Attachment {attachment.AttachmentId} is an animated image {Seconds(duration)} s long. {seeing}, sampled across the animation."
                    : $"Attachment {attachment.AttachmentId} is a picture larger than the image limits, so it was reduced in size to be " +
                      (framesAttached ? "reviewed and shown: you see the reduced version." : "reviewed; you are not being shown it."));
        }
        foreach (var item in skipped)
            notes.Add(item.Reason switch
            {
                SkipReason.BeyondLimit =>
                    $"Attachment {item.AttachmentId} ({item.MediaType}) was NOT reviewed and is NOT shown to you: only the first " +
                    $"{ExternalConversationImages.MaxImages} images and videos of a message are reviewed.",
                SkipReason.ReviewUnavailable =>
                    $"Attachment {item.AttachmentId} ({item.MediaType}) could not be reviewed, so it is NOT shown to you.",
                _ =>
                    $"Attachment {item.AttachmentId} ({item.MediaType}) could not be read as an image or video within the limits, " +
                    "so it was NOT reviewed and is NOT shown to you.",
            });
        return notes;
    }

    private static string Seconds(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}

/// <summary>An inbound picture or media file in the review list. A still is shown as its own bytes;
/// media is shown only as the frames the review covered.</summary>
internal sealed record InboundAttachmentImage(
    string AttachmentId, UntrustedContentImage Review, string MediaType, byte[] Bytes, bool Media);

/// <param name="ReviewedFrames">How many frames of the attachment the review covered (0 for a still).</param>
internal sealed record ShownImage(
    int AttachmentIndex, string MediaType, byte[] Bytes, double TimestampSeconds, int ReviewedFrames,
    double DurationSeconds);

internal enum SkipReason
{
    BeyondLimit,
    Unreadable,
    /// <summary>Media whose review was unavailable, so the message went through without it.</summary>
    ReviewUnavailable,
}

internal sealed record SkippedAttachment(string AttachmentId, string MediaType, SkipReason Reason);
