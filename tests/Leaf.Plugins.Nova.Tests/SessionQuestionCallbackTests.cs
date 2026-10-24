using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Leaf.Plugins.Nova.Endpoints;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Leaf.Plugins.Nova.Tests;

// Incident eb0d256c663d: a delegated session parked on AskUserQuestion and the delegating
// discussion was never told. RedCompute now posts each parked question once; the discussion must
// receive it with everything needed to answer, exactly once, and only from a loopback caller.
public sealed class SessionQuestionCallbackTests
{
    [Fact]
    public async Task Non_loopback_callers_are_rejected_and_nothing_is_injected()
    {
        using var f = new CallbackDeliveryTests.Fixture();

        Assert.Equal(403, await Ask(f, "req-1", remote: IPAddress.Parse("192.168.1.20")));

        Assert.Empty(f.TargetMessages);
        Assert.Empty(f.LiveMessages);
        Assert.Empty(f.Gateway.Admitted);
    }

    [Fact]
    public async Task Question_is_injected_with_its_options_deadline_and_the_exact_answer_contract()
    {
        using var f = new CallbackDeliveryTests.Fixture();

        Assert.Equal(200, await Ask(f, "req-1"));

        var message = Assert.Single(f.TargetMessages);
        var content = message.Content;
        Assert.Contains("<nova-event source=\"callback:session-question\" type=\"session-question\"", content);
        Assert.Contains("sessionId=\"delegated-session\"", content);
        Assert.Contains("requestId=\"req-1\"", content);
        Assert.Contains("expiresAt=\"2026-10-24T10:58:52Z\"", content);
        Assert.Contains("Leaf Commons relay", content);
        Assert.Contains("Question 1 [Database]: Which database?", content);
        Assert.Contains("- Postgres: Shared server", content);
        Assert.Contains("- SQLite: Local file", content);
        Assert.Contains("Question 2 [Scope] (multi-select): Which surfaces?", content);
        Assert.Contains("POST http://127.0.0.1:18800/ai-session/sessions/delegated-session/question", content);
        Assert.Contains("\"requestId\": \"req-1\"", content);
        Assert.Contains("\"answers\"", content);
        Assert.Contains("\"response\"", content);
        Assert.Contains("response outranks answers", content);
        Assert.Contains("\"decline\"", content);

        using var parts = JsonDocument.Parse(message.Metadata["parts_json"]!.GetValue<string>());
        var data = parts.RootElement.EnumerateArray().Single(p => p.GetProperty("type").GetString() == "event_data").GetProperty("data");
        Assert.Equal("req-1", data.GetProperty("requestId").GetString());
        Assert.Equal(2, data.GetProperty("questions").GetArrayLength());
        Assert.Equal("http://127.0.0.1:18800/ai-session/sessions/delegated-session/question",
            data.GetProperty("answer").GetProperty("url").GetString());

        // The discussion is woken to act on it, and LIVE hears about it like a completion.
        Assert.Single(f.Gateway.Admitted);
        Assert.Contains("waiting on a question: Leaf Commons relay", Assert.Single(f.LiveMessages).Content);
    }

    [Fact]
    public async Task Redelivery_of_the_same_request_is_deduplicated_and_a_new_request_is_not()
    {
        using var f = new CallbackDeliveryTests.Fixture();

        Assert.Equal(200, await Ask(f, "req-1"));
        Assert.Equal(200, await Ask(f, "req-1"));

        Assert.Single(f.TargetMessages);
        Assert.Single(f.Gateway.Admitted);
        Assert.Single(f.LiveMessages);

        Assert.Equal(200, await Ask(f, "req-2"));
        Assert.Equal(2, f.TargetMessages.Count());
        Assert.Equal(2, f.Gateway.Admitted.Count);
        Assert.Equal(2, f.LiveMessages.Count());
    }

    [Fact]
    public async Task Question_does_not_consume_the_later_completion()
    {
        using var f = new CallbackDeliveryTests.Fixture();

        Assert.Equal(200, await Ask(f, "req-1"));
        Assert.Equal(200, await f.Complete("prompt-one"));

        Assert.Equal(2, f.TargetMessages.Count());
        Assert.Contains(f.TargetMessages, m => m.Content.Contains("type=\"session-complete\""));
        Assert.Contains(f.LiveMessages, m => m.Content.Contains("Delegated session completed"));
    }

    [Fact]
    public async Task Codex_question_without_a_deadline_says_so()
    {
        using var f = new CallbackDeliveryTests.Fixture();

        Assert.Equal(200, await Ask(f, "item-1", expiresAt: null));

        var content = Assert.Single(f.TargetMessages).Content;
        Assert.Contains("expiresAt=\"none\"", content);
        Assert.Contains("no deadline", content);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Missing_request_identity_is_rejected(string? requestId)
    {
        using var f = new CallbackDeliveryTests.Fixture();

        Assert.Equal(400, await Ask(f, requestId));
        Assert.Empty(f.TargetMessages);
    }

    [Fact]
    public async Task Delegation_opts_in_to_question_notifications_for_its_discussion()
    {
        var (completion, question) = DelegateEndpoints.CallbackUrls("855ddaca");
        Assert.Equal("http://127.0.0.1:18804/api/apps/nova/callbacks/session-complete?discussionId=855ddaca", completion);
        Assert.Equal("http://127.0.0.1:18804/api/apps/nova/callbacks/session-question?discussionId=855ddaca", question);

        var requests = new List<JsonElement>();
        var gateway = CallbackDeliveryTests.Proxy.Create<IComputeGateway>((_, args) => {
            var request = (HttpRequestMessage)args![0]!;
            requests.Add(JsonSerializer.Deserialize<JsonElement>(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        var client = new RedComputeClient(gateway);
        Assert.True(await client.RegisterCallbackAsync("session", completion, force: true, callbackId: "prompt", promptMessageUid: "prompt",
            questionUrl: question));
        Assert.True(await client.RegisterCallbackAsync("session", completion, callbackId: "prompt", promptMessageUid: "prompt"));

        Assert.Equal(question, requests[0].GetProperty("questionUrl").GetString());
        Assert.Equal("prompt", requests[0].GetProperty("promptMessageUid").GetString());
        Assert.False(requests[1].TryGetProperty("questionUrl", out _));
    }

    private static async Task<int> Ask(CallbackDeliveryTests.Fixture f, string? requestId,
        IPAddress? remote = null, string? expiresAt = "2026-10-24T10:58:52Z")
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = remote ?? IPAddress.Loopback;
        ctx.Request.QueryString = new("?discussionId=target");
        ctx.Request.ContentType = "application/json";
        var body = new JsonObject
        {
            ["event"] = "session.question",
            ["sessionId"] = "delegated-session",
            ["callbackId"] = "prompt-one",
            ["title"] = "Leaf Commons relay",
            ["toolName"] = "AskUserQuestion",
            ["toolUseId"] = "toolu_ask",
            ["askedAt"] = "2026-10-24T10:28:52Z",
            ["expiresAt"] = expiresAt,
            ["questions"] = new JsonArray
            {
                new JsonObject
                {
                    ["question"] = "Which database?", ["header"] = "Database", ["multiSelect"] = false,
                    ["options"] = new JsonArray
                    {
                        new JsonObject { ["label"] = "Postgres", ["description"] = "Shared server" },
                        new JsonObject { ["label"] = "SQLite", ["description"] = "Local file" },
                    },
                },
                new JsonObject
                {
                    ["question"] = "Which surfaces?", ["header"] = "Scope", ["multiSelect"] = true,
                    ["options"] = new JsonArray { new JsonObject { ["label"] = "Nova" }, new JsonObject { ["label"] = "Code" } },
                },
            },
        };
        if (requestId is not null) body["requestId"] = requestId;
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body.ToJsonString()));
        var result = await CallbackEndpoints.HandleSessionQuestionAsync(ctx, f.Store, f.Injector, f.Live);
        return ((IStatusCodeHttpResult)result).StatusCode ?? 200;
    }
}
