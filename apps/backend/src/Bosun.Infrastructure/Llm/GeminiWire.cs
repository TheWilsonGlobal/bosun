using System.Text.Json.Nodes;
using Bosun.Application.Llm;

namespace Bosun.Infrastructure.Llm;

/// <summary>Maps the provider-neutral LLM types onto the Gemini REST wire format (v1beta generateContent) and back.</summary>
public static class GeminiWire
{
    public static JsonObject BuildGenerateContent(LlmRequest request, LlmOptions options)
    {
        var root = new JsonObject();

        if (!string.IsNullOrWhiteSpace(request.SystemInstruction))
        {
            root["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = request.SystemInstruction }) };
        }

        var contents = new JsonArray();
        foreach (var message in request.Messages)
        {
            var parts = new JsonArray();
            foreach (var part in message.Parts)
            {
                parts.Add(PartToJson(part));
            }

            contents.Add(new JsonObject { ["role"] = message.Role == LlmRole.Model ? "model" : "user", ["parts"] = parts });
        }

        root["contents"] = contents;

        if (request.Tools is { Count: > 0 } tools)
        {
            var declarations = new JsonArray();
            foreach (var tool in tools)
            {
                var declaration = new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description };
                if (tool.ParametersSchema is not null)
                {
                    declaration["parametersJsonSchema"] = tool.ParametersSchema.DeepClone();
                }

                declarations.Add(declaration);
            }

            root["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = declarations });

            var calling = new JsonObject
            {
                ["mode"] = request.ToolMode switch { LlmToolMode.Any => "ANY", LlmToolMode.None => "NONE", _ => "AUTO" },
            };
            if (request.ToolMode == LlmToolMode.Any && request.AllowedTools is { Count: > 0 } allowed)
            {
                calling["allowedFunctionNames"] = new JsonArray(allowed.Select(n => (JsonNode)JsonValue.Create(n)!).ToArray());
            }

            root["toolConfig"] = new JsonObject { ["functionCallingConfig"] = calling };
        }

        var generation = new JsonObject();
        var maxTokens = request.MaxOutputTokens ?? options.DefaultMaxOutputTokens;
        var thinking = request.Thinking ?? DefaultThinking(options);

        // A reasoning budget is part of the output budget: never let it leave no room for the answer.
        if (thinking?.Budget is { } budget && budget > 0)
        {
            maxTokens = Math.Max(maxTokens, budget + 1024);
        }

        generation["maxOutputTokens"] = maxTokens;
        if (request.Temperature is { } temperature)
        {
            generation["temperature"] = temperature;
        }

        if (request.ResponseSchema is { } schema)
        {
            generation["responseMimeType"] = "application/json";
            generation["responseJsonSchema"] = schema.Schema.DeepClone();
        }

        if (ThinkingConfig(thinking) is { } thinkingConfig)
        {
            generation["thinkingConfig"] = thinkingConfig;
        }

        root["generationConfig"] = generation;
        return root;
    }

    private static LlmThinking? DefaultThinking(LlmOptions options)
    {
        if (options.ThinkingBudget is { } budget)
        {
            return LlmThinking.WithBudget(budget);
        }

        return Enum.TryParse<LlmThinkingLevel>(options.ThinkingLevel, ignoreCase: true, out var level) ? new LlmThinking(level) : null;
    }

    // The API rejects level + budget together ("You can only set only one of thinking budget and thinking level"); budget wins.
    private static JsonObject? ThinkingConfig(LlmThinking? thinking)
    {
        if (thinking is null)
        {
            return null;
        }

        if (thinking.Budget is { } budget)
        {
            return new JsonObject { ["thinkingBudget"] = budget };
        }

        return thinking.Level is { } level ? new JsonObject { ["thinkingLevel"] = level.ToString().ToLowerInvariant() } : null;
    }

    private static JsonObject PartToJson(LlmPart part)
    {
        switch (part)
        {
            case TextPart text:
                var textJson = new JsonObject { ["text"] = text.Text };
                if (text.ThoughtSignature is not null)
                {
                    textJson["thoughtSignature"] = text.ThoughtSignature;
                }

                return textJson;
            case ImagePart image:
                return new JsonObject
                {
                    ["inlineData"] = new JsonObject { ["mimeType"] = image.MimeType, ["data"] = Convert.ToBase64String(image.Data) },
                };
            case FunctionCallPart call:
                var callBody = new JsonObject { ["name"] = call.Name, ["args"] = call.Args.DeepClone() };
                if (call.Id is not null)
                {
                    callBody["id"] = call.Id;
                }

                var callJson = new JsonObject { ["functionCall"] = callBody };
                if (call.ThoughtSignature is not null)
                {
                    callJson["thoughtSignature"] = call.ThoughtSignature;
                }

                return callJson;
            case FunctionResponsePart result:
                var response = result.Response is JsonObject obj ? (JsonNode)obj.DeepClone() : new JsonObject { ["result"] = result.Response?.DeepClone() };
                var resultBody = new JsonObject { ["name"] = result.Name, ["response"] = response };
                if (result.Id is not null)
                {
                    resultBody["id"] = result.Id;
                }

                return new JsonObject { ["functionResponse"] = resultBody };
            default:
                throw new NotSupportedException($"Unsupported LLM part {part.GetType().Name}.");
        }
    }

    /// <summary>Parses a 200 generateContent body. Throws for safety blocks and malformed function calls.</summary>
    public static LlmResponse ParseGenerateContent(JsonNode root, string requestedModel, TimeSpan latency, int attempts)
    {
        var usage = ParseUsage(root["usageMetadata"]);
        var model = root["modelVersion"]?.GetValue<string>() ?? requestedModel;

        var candidate = root["candidates"] is JsonArray { Count: > 0 } candidates ? candidates[0] : null;
        if (candidate is null)
        {
            var blockReason = root["promptFeedback"]?["blockReason"]?.GetValue<string>();
            if (blockReason is not null)
            {
                throw new LlmException(LlmErrorCodes.SafetyBlocked,
                    $"Gemini blocked the request before answering (blockReason {blockReason}). Rephrase the input or check the document content.",
                    attempts: attempts);
            }

            throw new LlmException(LlmErrorCodes.MalformedOutput, "Gemini returned no answer (no candidates) and gave no reason.", attempts: attempts);
        }

        var raw = candidate["finishReason"]?.GetValue<string>();
        switch (raw)
        {
            case "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT" or "SPII" or "IMAGE_SAFETY":
                throw new LlmException(LlmErrorCodes.SafetyBlocked,
                    $"Gemini stopped the answer for safety reasons (finishReason {raw}). Rephrase the input or check the document content.",
                    attempts: attempts);
            case "MALFORMED_FUNCTION_CALL" or "UNEXPECTED_TOOL_CALL":
                throw new LlmException(LlmErrorCodes.MalformedOutput,
                    $"Gemini produced an invalid tool call (finishReason {raw}).", attempts: attempts);
        }

        var parts = new List<LlmPart>();
        if (candidate["content"]?["parts"] is JsonArray array)
        {
            foreach (var node in array)
            {
                if (node is not JsonObject part || part["thought"]?.GetValue<bool>() == true)
                {
                    continue;
                }

                var signature = part["thoughtSignature"]?.GetValue<string>();
                if (part["functionCall"] is JsonObject call)
                {
                    parts.Add(new FunctionCallPart(
                        call["name"]?.GetValue<string>() ?? "",
                        call["args"] as JsonObject ?? new JsonObject(),
                        call["id"]?.GetValue<string>(),
                        signature));
                }
                else if (part["text"] is JsonValue text)
                {
                    parts.Add(new TextPart(text.GetValue<string>(), signature));
                }
            }
        }

        return new LlmResponse
        {
            Model = model,
            Parts = parts,
            FinishReason = raw switch { "STOP" => LlmFinishReason.Stop, "MAX_TOKENS" => LlmFinishReason.MaxTokens, null => LlmFinishReason.Stop, _ => LlmFinishReason.Other },
            FinishReasonRaw = raw,
            Usage = usage,
            Latency = latency,
            Attempts = attempts,
        };
    }

    public static LlmUsage ParseUsage(JsonNode? metadata)
    {
        if (metadata is null)
        {
            return LlmUsage.Zero;
        }

        int Count(string name) => metadata[name] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;

        var prompt = Count("promptTokenCount") + Count("toolUsePromptTokenCount");
        var candidates = Count("candidatesTokenCount");
        var thoughts = Count("thoughtsTokenCount");
        var total = Count("totalTokenCount");
        return new LlmUsage(prompt, candidates, thoughts, total > 0 ? total : prompt + candidates + thoughts, Count("cachedContentTokenCount"));
    }
}
