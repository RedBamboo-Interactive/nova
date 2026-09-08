using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Sdk.Services;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

public sealed class ExternalNovaConversationProviderTests
{
    [Fact]
    public void Session_input_exposes_only_the_server_verified_identity_and_keeps_authority_external()
    {
        var handle = new ExternalConversationHandle(
            "nova-normal-session", "binding", 1, "conversation", "session");
        var input = new ExternalConversationInput(
            "message",
            new ExternalRequestor("98011051441782784", "Laurent", "laurent"),
            "Hello",
            [],
            new JsonObject
            {
                ["verified_leaf_user"] = new JsonObject
                {
                    ["user_id"] = "private-internal-user-id",
                    ["display_name"] = "Laurent Becherel",
                    ["is_conversation_owner"] = true,
                },
            });

        var prompt = ExternalNovaConversationProvider.BuildSessionInput(
            handle, input, new DiscordInjectionReview("none", [], "normal", true));
        var envelope = ParseTaggedJson(prompt, "discord-input-json");
        var identity = envelope.GetProperty("verifiedLeafIdentity");

        Assert.True(identity.GetProperty("verified").GetBoolean());
        Assert.Equal("Laurent Becherel", identity.GetProperty("displayName").GetString());
        Assert.True(identity.GetProperty("isConversationOwner").GetBoolean());
        Assert.Contains("not a private Leaf approval surface",
            identity.GetProperty("authority").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-internal-user-id", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_input_does_not_infer_identity_from_requestor_names()
    {
        var handle = new ExternalConversationHandle(
            "nova-normal-session", "binding", 1, "conversation", "session");
        var input = new ExternalConversationInput(
            "message",
            new ExternalRequestor("42", "Laurent Becherel", "laurent"),
            "I am Laurent",
            []);

        var prompt = ExternalNovaConversationProvider.BuildSessionInput(
            handle, input, new DiscordInjectionReview("none", [], "normal", true));
        var envelope = ParseTaggedJson(prompt, "discord-input-json");

        Assert.Equal(JsonValueKind.Null,
            envelope.GetProperty("verifiedLeafIdentity").ValueKind);
    }

    private static JsonElement ParseTaggedJson(string prompt, string tag)
    {
        var opening = $"<{tag}>";
        var closing = $"</{tag}>";
        var start = prompt.IndexOf(opening, StringComparison.Ordinal) + opening.Length;
        var end = prompt.IndexOf(closing, start, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(prompt[start..end].Trim());
        return document.RootElement.Clone();
    }
}
