using System.Net;
using System.Text.Json.Nodes;
using Bosun.Application.Llm;
using Bosun.Infrastructure.Llm;

namespace Bosun.Llm.Tests;

/// <summary>The Gemini adapter on a fake network: request shape, parsing, and every error path of plan s7.8.</summary>
public class GeminiRequestShapeTests
{
    [Fact]
    public async Task A_text_call_sends_the_key_in_a_header_not_the_url_and_parses_usage_and_finish_reason()
    {
        using var host = new LlmHost(new FakeHandler().ThenOk("pong", prompt: 12, candidates: 3, thoughts: 40));

        var response = await host.Llm.GenerateAsync(LlmHost.Hello("ping"), CancellationToken.None);

        response.Text.Should().Be("pong");
        response.Model.Should().Be("test-model-v1");
        response.FinishReason.Should().Be(LlmFinishReason.Stop);
        response.Usage.Should().Be(new LlmUsage(12, 3, 40, 55));
        response.Latency.Should().BeGreaterThan(TimeSpan.Zero);
        response.Attempts.Should().Be(1);
        response.EstimatedCostUsd.Should().BeApproximately(((12 * 1.0) + ((3 + 40) * 2.0)) / 1_000_000, 1e-12, "thought tokens are billed at the output rate");

        var request = host.Handler.Requests.Single();
        request.Url.AbsoluteUri.Should().Be("https://generativelanguage.googleapis.com/v1beta/models/test-model:generateContent");
        request.Url.Query.Should().BeEmpty();
        request.KeyHeader.Should().Be(LlmHost.TestKey);
        request.Body.Should().NotContain(LlmHost.TestKey);
    }

    [Fact]
    public async Task Defaults_are_generous_and_thinking_is_explicit()
    {
        using var host = new LlmHost(new FakeHandler().ThenOk("x"));
        await host.Llm.GenerateAsync(LlmHost.Hello(), CancellationToken.None);

        var generation = host.Handler.Requests.Single().Json["generationConfig"]!;
        generation["maxOutputTokens"]!.GetValue<int>().Should().Be(8192);
        generation["thinkingConfig"]!["thinkingLevel"]!.GetValue<string>().Should().Be("minimal");
        generation["thinkingConfig"]!.AsObject().ContainsKey("thinkingBudget").Should().BeFalse("the API rejects level and budget together");
    }

    [Fact]
    public async Task A_thinking_budget_wins_over_the_level_and_always_leaves_room_for_the_answer()
    {
        using var host = new LlmHost(new FakeHandler().ThenOk("x"));
        await host.Llm.GenerateAsync(LlmHost.Hello() with { Thinking = LlmThinking.WithBudget(4000), MaxOutputTokens = 500 }, CancellationToken.None);

        var generation = host.Handler.Requests.Single().Json["generationConfig"]!;
        generation["thinkingConfig"]!["thinkingBudget"]!.GetValue<int>().Should().Be(4000);
        generation["thinkingConfig"]!.AsObject().ContainsKey("thinkingLevel").Should().BeFalse();
        generation["maxOutputTokens"]!.GetValue<int>().Should().BeGreaterThan(4000, "reasoning tokens count against maxOutputTokens");
    }

    [Fact]
    public async Task A_configured_budget_replaces_the_default_level()
    {
        using var host = new LlmHost(new FakeHandler().ThenOk("x"), new() { ["Llm:ThinkingBudget"] = "2048" });
        await host.Llm.GenerateAsync(LlmHost.Hello(), CancellationToken.None);

        host.Handler.Requests.Single().Json["generationConfig"]!["thinkingConfig"]!["thinkingBudget"]!.GetValue<int>().Should().Be(2048);
    }

    [Fact]
    public async Task Structured_requests_send_a_json_schema_and_json_mime_type()
    {
        using var host = new LlmHost(new FakeHandler().ThenOk("{}"));
        var schema = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["ok"] = new JsonObject { ["type"] = "boolean" } } };
        await host.Llm.GenerateAsync(LlmHost.Hello() with { ResponseSchema = new LlmJsonSchema("S", schema), SystemInstruction = "be brief", Temperature = 0 }, CancellationToken.None);

        var body = host.Handler.Requests.Single().Json;
        body["generationConfig"]!["responseMimeType"]!.GetValue<string>().Should().Be("application/json");
        body["generationConfig"]!["responseJsonSchema"]!["properties"]!["ok"]!["type"]!.GetValue<string>().Should().Be("boolean");
        body["generationConfig"]!["temperature"]!.GetValue<double>().Should().Be(0);
        body["systemInstruction"]!["parts"]![0]!["text"]!.GetValue<string>().Should().Be("be brief");
    }

    [Fact]
    public async Task Vision_input_is_sent_inline_as_base64()
    {
        using var host = new LlmHost(new FakeHandler().ThenOk("a cat"));
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];
        await host.Llm.GenerateAsync(new LlmRequest
        {
            Feature = "vision",
            Messages = [LlmMessage.User(new TextPart("what is this?"), new ImagePart(png, "image/png"))],
        }, CancellationToken.None);

        var parts = host.Handler.Requests.Single().Json["contents"]![0]!["parts"]!.AsArray();
        parts[0]!["text"]!.GetValue<string>().Should().Be("what is this?");
        parts[1]!["inlineData"]!["mimeType"]!.GetValue<string>().Should().Be("image/png");
        Convert.FromBase64String(parts[1]!["inlineData"]!["data"]!.GetValue<string>()).Should().Equal(png);
    }

    [Fact]
    public async Task Tool_declarations_and_the_function_call_round_trip_keep_ids_and_thought_signatures()
    {
        var call = new JsonObject
        {
            ["candidates"] = new JsonArray(new JsonObject
            {
                ["content"] = new JsonObject
                {
                    ["role"] = "model",
                    ["parts"] = new JsonArray(new JsonObject
                    {
                        ["functionCall"] = new JsonObject { ["name"] = "get_weather", ["args"] = new JsonObject { ["city"] = "Hanoi" }, ["id"] = "call_1" },
                        ["thoughtSignature"] = "SIG-ABC",
                    }),
                },
                ["finishReason"] = "STOP",
            }),
            ["usageMetadata"] = FakeHandler.Usage(30, 10, 0),
        }.ToJsonString();
        using var host = new LlmHost(new FakeHandler().Then(HttpStatusCode.OK, call).ThenOk("It is sunny in Hanoi."));

        var tool = new LlmToolDeclaration(
            "get_weather",
            "Weather for a city",
            new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["city"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("city") });
        var first = await host.Llm.GenerateAsync(LlmHost.Hello() with { Tools = [tool], ToolMode = LlmToolMode.Any, AllowedTools = ["get_weather"] }, CancellationToken.None);

        var sent = host.Handler.Requests[0].Json;
        sent["tools"]![0]!["functionDeclarations"]![0]!["name"]!.GetValue<string>().Should().Be("get_weather");
        sent["tools"]![0]!["functionDeclarations"]![0]!["parametersJsonSchema"]!["required"]![0]!.GetValue<string>().Should().Be("city");
        sent["toolConfig"]!["functionCallingConfig"]!["mode"]!.GetValue<string>().Should().Be("ANY");
        sent["toolConfig"]!["functionCallingConfig"]!["allowedFunctionNames"]![0]!.GetValue<string>().Should().Be("get_weather");

        var functionCall = first.FunctionCalls.Should().ContainSingle().Subject;
        functionCall.Should().Match<FunctionCallPart>(c => c.Name == "get_weather" && c.Id == "call_1" && c.ThoughtSignature == "SIG-ABC");
        functionCall.Args["city"]!.GetValue<string>().Should().Be("Hanoi");

        // the next turn: the model's own message first (signature intact), then the tool result
        var history = new List<LlmMessage>(LlmHost.Hello().Messages) { first.ToMessage(), LlmMessage.ToolResults([new FunctionResponsePart("get_weather", new JsonObject { ["tempC"] = 31 }, "call_1")]) };
        var second = await host.Llm.GenerateAsync(LlmHost.Hello() with { Messages = history, Tools = [tool] }, CancellationToken.None);
        second.Text.Should().Be("It is sunny in Hanoi.");

        var contents = host.Handler.Requests[1].Json["contents"]!.AsArray();
        contents.Count.Should().Be(3);
        contents[1]!["role"]!.GetValue<string>().Should().Be("model");
        contents[1]!["parts"]![0]!["thoughtSignature"]!.GetValue<string>().Should().Be("SIG-ABC");
        contents[1]!["parts"]![0]!["functionCall"]!["id"]!.GetValue<string>().Should().Be("call_1");
        contents[2]!["role"]!.GetValue<string>().Should().Be("user");
        contents[2]!["parts"]![0]!["functionResponse"]!["response"]!["tempC"]!.GetValue<int>().Should().Be(31);
    }

    [Fact]
    public async Task A_non_object_tool_result_is_wrapped_because_the_api_wants_an_object()
    {
        using var host = new LlmHost(new FakeHandler().ThenOk("ok"));
        await host.Llm.GenerateAsync(
            LlmHost.Hello() with { Messages = [LlmMessage.ToolResults([new FunctionResponsePart("count", JsonValue.Create(7))])] },
            CancellationToken.None);

        host.Handler.Requests.Single().Json["contents"]![0]!["parts"]![0]!["functionResponse"]!["response"]!["result"]!.GetValue<int>().Should().Be(7);
    }

    [Fact]
    public async Task A_request_model_overrides_the_configured_one_but_must_be_a_valid_id()
    {
        using var host = new LlmHost(new FakeHandler().ThenOk("x"));
        await host.Llm.GenerateAsync(LlmHost.Hello() with { Model = "models/other-model.2" }, CancellationToken.None);
        host.Handler.Requests.Single().Url.AbsolutePath.Should().EndWith("/models/other-model.2:generateContent");

        var bad = async () => await host.Llm.GenerateAsync(LlmHost.Hello() with { Model = "../../evil?x=1" }, CancellationToken.None);
        (await bad.Should().ThrowAsync<LlmException>()).Which.Code.Should().Be(LlmErrorCodes.BadRequest);
        host.Handler.Calls.Should().Be(1, "the invalid id never reaches the network");
    }
}

public class GeminiErrorMappingTests
{
    private static async Task<LlmException> FailureAsync(LlmHost host, LlmRequest? request = null)
    {
        var act = async () => await host.Llm.GenerateAsync(request ?? LlmHost.Hello(), CancellationToken.None);
        return (await act.Should().ThrowAsync<LlmException>()).Which;
    }

    [Fact]
    public async Task A_429_is_retried_and_the_second_attempt_succeeds()
    {
        var handler = new FakeHandler()
            .Then(HttpStatusCode.TooManyRequests, FakeHandler.Error(429, "RESOURCE_EXHAUSTED", "Quota exceeded for metric"), r => r.Headers.RetryAfter = new(TimeSpan.Zero))
            .ThenOk("recovered");
        using var host = new LlmHost(handler);

        var response = await host.Llm.GenerateAsync(LlmHost.Hello(), CancellationToken.None);

        response.Text.Should().Be("recovered");
        response.Attempts.Should().Be(2);
        handler.Calls.Should().Be(2);
        host.Log.Calls.Should().ContainSingle().Which.Should().Match<Bosun.Domain.Entities.LlmCall>(c => c.Succeeded && c.Attempts == 2);
    }

    [Fact]
    public async Task The_provider_retryDelay_is_honoured_in_the_error_body()
    {
        var handler = new FakeHandler()
            .Then(HttpStatusCode.TooManyRequests, FakeHandler.Error(429, "RESOURCE_EXHAUSTED", "slow down", ("RATE_LIMIT_EXCEEDED", null), ("", "0.05s")))
            .ThenOk("ok");
        using var host = new LlmHost(handler);

        (await host.Llm.GenerateAsync(LlmHost.Hello(), CancellationToken.None)).Attempts.Should().Be(2);
    }

    [Fact]
    public async Task A_429_that_asks_for_a_long_wait_is_not_retried_and_says_how_long()
    {
        var handler = new FakeHandler().Then(HttpStatusCode.TooManyRequests, FakeHandler.Error(429, "RESOURCE_EXHAUSTED", "You exceeded your current quota", ("", "3600s")));
        using var host = new LlmHost(handler);

        var failure = await FailureAsync(host);

        failure.Code.Should().Be(LlmErrorCodes.RateLimited);
        failure.RetryAfter.Should().Be(TimeSpan.FromHours(1));
        failure.ProviderStatus.Should().Be(429);
        failure.Message.Should().Contain("3600").And.Contain("quota");
        handler.Calls.Should().Be(1, "waiting an hour inside a request is pointless");
        host.Log.Calls.Single().Should().Match<Bosun.Domain.Entities.LlmCall>(c => !c.Succeeded && c.ErrorCode == "llm_rate_limited");
    }

    [Fact]
    public async Task A_daily_quota_429_is_retried_only_when_quota_retries_are_switched_on_and_the_flap_can_succeed()
    {
        var quota = FakeHandler.Error(429, "RESOURCE_EXHAUSTED", "You exceeded your current quota", ("", "58000s"));
        var handler = new FakeHandler()
            .Then(HttpStatusCode.TooManyRequests, quota)
            .Then(HttpStatusCode.TooManyRequests, quota)
            .ThenOk("recovered");
        using var host = new LlmHost(handler, new() { ["Llm:Resilience:QuotaRetryAttempts"] = "3", ["Llm:Resilience:QuotaRetryDelaySeconds"] = "0.05" });

        var response = await host.Llm.GenerateAsync(LlmHost.Hello(), CancellationToken.None);

        response.Text.Should().Be("recovered");
        response.Attempts.Should().Be(3);
        handler.Calls.Should().Be(3);
    }

    [Fact]
    public async Task A_spent_daily_quota_still_surfaces_with_the_providers_text_when_the_quota_retries_run_out()
    {
        var quota = FakeHandler.Error(429, "RESOURCE_EXHAUSTED", "You exceeded your current quota", ("", "58000s"));
        var handler = new FakeHandler();
        for (var i = 0; i < 4; i++)
        {
            handler.Then(HttpStatusCode.TooManyRequests, quota);
        }

        using var host = new LlmHost(handler, new() { ["Llm:Resilience:QuotaRetryAttempts"] = "3", ["Llm:Resilience:QuotaRetryDelaySeconds"] = "0.05" });

        var failure = await FailureAsync(host);

        failure.Code.Should().Be(LlmErrorCodes.RateLimited);
        failure.Message.Should().Contain("quota");
        failure.Attempts.Should().Be(4);
        handler.Calls.Should().Be(4, "one try plus the three quota retries, then it gives up");
    }

    [Fact]
    public async Task A_persistent_429_gives_up_after_the_configured_retries()
    {
        var handler = new FakeHandler();
        for (var i = 0; i < 5; i++)
        {
            handler.Then(HttpStatusCode.TooManyRequests, FakeHandler.Error(429, "RESOURCE_EXHAUSTED", "busy"), r => r.Headers.RetryAfter = new(TimeSpan.Zero));
        }

        using var host = new LlmHost(handler, new() { ["Llm:Resilience:MaxRetryAttempts"] = "2" });

        var failure = await FailureAsync(host);

        failure.Code.Should().Be(LlmErrorCodes.RateLimited);
        failure.Attempts.Should().Be(3);
        handler.Calls.Should().Be(3, "1 try + 2 retries");
    }

    public static TheoryData<int, string, string, string> AuthFailures => new()
    {
        // real responses recorded against generativelanguage.googleapis.com with deliberately wrong keys
        { 400, "INVALID_ARGUMENT", "API_KEY_INVALID", "API key not valid. Please pass a valid API key." },
        { 401, "UNAUTHENTICATED", "ACCESS_TOKEN_TYPE_UNSUPPORTED", "API key not valid. Please pass a valid API key." },
        { 403, "PERMISSION_DENIED", "", "Method doesn't allow unregistered callers (callers without established identity)." },
        { 400, "INVALID_ARGUMENT", "API_KEY_SERVICE_BLOCKED", "Requests to this API are blocked." },
    };

    [Theory]
    [MemberData(nameof(AuthFailures))]
    public async Task A_rejected_key_is_llm_auth_failed_whatever_the_status_and_is_never_retried(int status, string statusName, string reason, string message)
    {
        var handler = new FakeHandler().Then((HttpStatusCode)status, FakeHandler.Error(status, statusName, message, (reason, null)));
        using var host = new LlmHost(handler);

        var failure = await FailureAsync(host);

        failure.Code.Should().Be(LlmErrorCodes.AuthFailed);
        failure.Message.Should().Contain("[rejected key: GEMINI_LLM_API_KEY]");
        failure.Message.Should().Contain("GEMINI_LLM_API_KEY");
        handler.Calls.Should().Be(1, "a wrong key does not get better with retries");
    }

    [Fact]
    public async Task Other_400s_are_a_bad_request_not_an_auth_failure()
    {
        var handler = new FakeHandler().Then(HttpStatusCode.BadRequest, FakeHandler.Error(400, "INVALID_ARGUMENT", "Request contains an invalid argument."));
        using var host = new LlmHost(handler);

        var failure = await FailureAsync(host);

        failure.Code.Should().Be(LlmErrorCodes.BadRequest);
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task A_404_unknown_model_is_a_bad_request_that_names_the_problem()
    {
        var handler = new FakeHandler().Then(HttpStatusCode.NotFound, FakeHandler.Error(404, "NOT_FOUND", "models/test-model is not found for API version v1beta"));
        using var host = new LlmHost(handler);

        var failure = await FailureAsync(host);

        failure.Code.Should().Be(LlmErrorCodes.BadRequest);
        failure.Message.Should().Contain("not found");
    }

    [Fact]
    public async Task A_503_is_retried_and_a_persistent_one_is_llm_unavailable()
    {
        var transient = new FakeHandler().Then(HttpStatusCode.ServiceUnavailable, FakeHandler.Error(503, "UNAVAILABLE", "The model is overloaded.")).ThenOk("fine");
        using (var host = new LlmHost(transient))
        {
            (await host.Llm.GenerateAsync(LlmHost.Hello(), CancellationToken.None)).Attempts.Should().Be(2);
        }

        var persistent = new FakeHandler();
        for (var i = 0; i < 3; i++)
        {
            persistent.Then(HttpStatusCode.ServiceUnavailable, FakeHandler.Error(503, "UNAVAILABLE", "The model is overloaded."));
        }

        using var failing = new LlmHost(persistent, new() { ["Llm:Resilience:MaxRetryAttempts"] = "2" });
        var failure = await FailureAsync(failing);
        failure.Code.Should().Be(LlmErrorCodes.Unavailable);
        failure.Message.Should().Contain("overloaded");
        persistent.Calls.Should().Be(3);
    }

    [Fact]
    public async Task A_network_failure_is_retried_then_reported_as_unavailable()
    {
        var handler = new FakeHandler().ThenThrow(new HttpRequestException("connection refused")).ThenOk("back");
        using var host = new LlmHost(handler);
        (await host.Llm.GenerateAsync(LlmHost.Hello(), CancellationToken.None)).Attempts.Should().Be(2);

        var dead = new FakeHandler();
        for (var i = 0; i < 4; i++)
        {
            dead.ThenThrow(new HttpRequestException("connection refused"));
        }

        using var down = new LlmHost(dead);
        (await FailureAsync(down)).Code.Should().Be(LlmErrorCodes.Unavailable);
    }

    [Fact]
    public async Task A_504_from_the_provider_is_llm_timeout()
    {
        using var host = new LlmHost(new FakeHandler().Then(HttpStatusCode.GatewayTimeout, FakeHandler.Error(504, "DEADLINE_EXCEEDED", "Deadline expired")), new() { ["Llm:Resilience:MaxRetryAttempts"] = "0" });
        (await FailureAsync(host)).Code.Should().Be(LlmErrorCodes.Timeout);
    }

    [Fact]
    public async Task A_slow_provider_hits_the_attempt_timeout_and_is_llm_timeout()
    {
        var handler = new FakeHandler();
        for (var i = 0; i < 2; i++)
        {
            handler.Then(async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return FakeHandler.Respond(HttpStatusCode.OK, "{}");
            });
        }

        using var host = new LlmHost(handler, new() { ["Llm:Resilience:AttemptTimeoutSeconds"] = "0.2", ["Llm:Resilience:MaxRetryAttempts"] = "1" });

        var failure = await FailureAsync(host);

        failure.Code.Should().Be(LlmErrorCodes.Timeout);
        failure.Message.Should().Contain("in time");
        handler.Calls.Should().Be(2, "the timed-out attempt was retried once");
    }

    [Fact]
    public async Task The_caller_cancelling_is_not_an_llm_error()
    {
        var handler = new FakeHandler().Then(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return FakeHandler.Respond(HttpStatusCode.OK, "{}");
        });
        using var host = new LlmHost(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var act = async () => await host.Llm.GenerateAsync(LlmHost.Hello(), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Repeated_server_errors_open_the_circuit_and_later_calls_fail_fast_without_touching_the_network()
    {
        var handler = new FakeHandler();
        for (var i = 0; i < 3; i++)
        {
            handler.Then(HttpStatusCode.InternalServerError, FakeHandler.Error(500, "INTERNAL", "boom"));
        }

        using var host = new LlmHost(handler, new()
        {
            ["Llm:Resilience:MaxRetryAttempts"] = "0",
            ["Llm:Resilience:BreakerMinimumThroughput"] = "3",
            ["Llm:Resilience:BreakerFailureRatio"] = "0.5",
            ["Llm:Resilience:BreakerSamplingSeconds"] = "30",
            ["Llm:Resilience:BreakerBreakSeconds"] = "30",
        });

        for (var i = 0; i < 3; i++)
        {
            (await FailureAsync(host)).Code.Should().Be(LlmErrorCodes.Unavailable);
        }

        var fast = await FailureAsync(host);

        fast.Code.Should().Be(LlmErrorCodes.Unavailable);
        fast.Message.Should().Contain("circuit breaker");
        handler.Calls.Should().Be(3, "the fourth call never left the process");
    }

    [Fact]
    public async Task A_429_does_not_count_towards_the_circuit_breaker()
    {
        var handler = new FakeHandler();
        for (var i = 0; i < 6; i++)
        {
            handler.Then(HttpStatusCode.TooManyRequests, FakeHandler.Error(429, "RESOURCE_EXHAUSTED", "busy", ("", "3600s")));
        }

        using var host = new LlmHost(handler, new() { ["Llm:Resilience:BreakerMinimumThroughput"] = "3" });
        for (var i = 0; i < 6; i++)
        {
            (await FailureAsync(host)).Code.Should().Be(LlmErrorCodes.RateLimited);
        }

        handler.Calls.Should().Be(6);
    }

    [Fact]
    public async Task A_safety_block_before_or_during_the_answer_is_llm_safety_blocked_and_not_retried()
    {
        var blockedPrompt = new JsonObject { ["promptFeedback"] = new JsonObject { ["blockReason"] = "PROHIBITED_CONTENT" } }.ToJsonString();
        using (var host = new LlmHost(new FakeHandler().Then(HttpStatusCode.OK, blockedPrompt)))
        {
            var failure = await FailureAsync(host);
            failure.Code.Should().Be(LlmErrorCodes.SafetyBlocked);
            failure.Message.Should().Contain("PROHIBITED_CONTENT");
            host.Handler.Calls.Should().Be(1);
        }

        using var blockedAnswer = new LlmHost(new FakeHandler().Then(HttpStatusCode.OK, FakeHandler.Ok("", finish: "SAFETY")));
        (await FailureAsync(blockedAnswer)).Code.Should().Be(LlmErrorCodes.SafetyBlocked);
    }

    [Fact]
    public async Task A_malformed_tool_call_and_an_empty_answer_are_llm_malformed_output()
    {
        using var malformedCall = new LlmHost(new FakeHandler().Then(HttpStatusCode.OK, FakeHandler.Ok("", finish: "MALFORMED_FUNCTION_CALL")));
        (await FailureAsync(malformedCall)).Code.Should().Be(LlmErrorCodes.MalformedOutput);

        using var noCandidates = new LlmHost(new FakeHandler().Then(HttpStatusCode.OK, "{\"usageMetadata\":{}}"));
        (await FailureAsync(noCandidates)).Code.Should().Be(LlmErrorCodes.MalformedOutput);
    }

    [Fact]
    public async Task A_non_json_200_body_is_unavailable()
    {
        using var host = new LlmHost(new FakeHandler().Then(HttpStatusCode.OK, "<html>captive portal</html>"));
        (await FailureAsync(host)).Code.Should().Be(LlmErrorCodes.Unavailable);
    }

    [Fact]
    public async Task MaxTokens_is_returned_as_a_finish_reason_for_the_caller_to_act_on()
    {
        using var host = new LlmHost(new FakeHandler().ThenOk("{\"ok\": tr", finish: "MAX_TOKENS", thoughts: 900));
        var response = await host.Llm.GenerateAsync(LlmHost.Hello(), CancellationToken.None);
        response.FinishReason.Should().Be(LlmFinishReason.MaxTokens);
        response.Usage.ThoughtTokens.Should().Be(900);
    }

    [Fact]
    public async Task Missing_key_provider_or_model_fail_before_any_request_with_llm_not_configured()
    {
        foreach (var config in new Dictionary<string, string?>[]
                 {
                     new() { ["Llm:ApiKey"] = "" },
                     new() { ["Llm:Provider"] = "openai-compatible" },
                     new() { ["Llm:Model"] = "" },
                 })
        {
            using var host = new LlmHost(new FakeHandler(), config);
            var failure = await FailureAsync(host);
            failure.Code.Should().Be(LlmErrorCodes.NotConfigured);
            host.Handler.Calls.Should().Be(0);
            host.Log.Calls.Should().BeEmpty("nothing was sent, so nothing is recorded as a call");
        }
    }

    [Fact]
    public async Task Provider_error_text_that_echoes_the_key_is_redacted()
    {
        var handler = new FakeHandler().Then(HttpStatusCode.BadRequest, FakeHandler.Error(400, "INVALID_ARGUMENT", $"bad request for key {LlmHost.TestKey} sorry"));
        using var host = new LlmHost(handler);

        var failure = await FailureAsync(host);

        failure.Message.Should().NotContain(LlmHost.TestKey).And.Contain("***");
    }

    [Theory]
    [InlineData("33s", 33.0)]
    [InlineData("0.5s", 0.5)]
    [InlineData("33.123456789s", 33.123456789)]
    public void Protobuf_durations_parse(string text, double seconds)
    {
        GeminiErrors.TryParseDuration(text, out var parsed).Should().BeTrue();
        parsed.TotalSeconds.Should().BeApproximately(seconds, 1e-6);
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("-3s")]
    [InlineData("12")]
    public void Garbage_durations_do_not_parse(string text) => GeminiErrors.TryParseDuration(text, out _).Should().BeFalse();
}
