using System.Text.Json;
using Bosun.Application;
using Bosun.Application.Llm;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Bosun.Api;

/// <summary>RFC 7807 problem details with a stable <c>code</c> member that the frontend maps.</summary>
public static class Problems
{
    public static ProblemHttpResult Result(int status, string code, string detail, IReadOnlyDictionary<string, object?>? extensions = null)
    {
        var problem = Build(status, code, detail, extensions);
        return TypedResults.Problem(problem);
    }

    public static ProblemDetails Build(int status, string code, string? detail, IReadOnlyDictionary<string, object?>? extensions = null)
    {
        var problem = new ProblemDetails
        {
            Type = $"https://bosun.local/problems/{code}",
            Title = Title(status),
            Status = status,
            Detail = detail,
        };
        problem.Extensions["code"] = code;
        if (extensions is not null)
        {
            foreach (var (key, value) in extensions)
            {
                problem.Extensions[key] = value;
            }
        }

        return problem;
    }

    public static int StatusFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.PayloadTooLarge => StatusCodes.Status413PayloadTooLarge,
        ErrorKind.UnsupportedMedia => StatusCodes.Status415UnsupportedMediaType,
        _ => StatusCodes.Status422UnprocessableEntity,
    };

    /// <summary>
    /// HTTP status for an LLM failure. A rejected server-side key is a 502, never a 401: a 401 would make the SPA think the
    /// user's session expired.
    /// </summary>
    public static int StatusFor(LlmException ex) => ex.Code switch
    {
        LlmErrorCodes.RateLimited => StatusCodes.Status429TooManyRequests,
        LlmErrorCodes.SafetyBlocked => StatusCodes.Status422UnprocessableEntity,
        LlmErrorCodes.Timeout => StatusCodes.Status504GatewayTimeout,
        LlmErrorCodes.Unavailable or LlmErrorCodes.NotConfigured => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status502BadGateway, // auth_failed, malformed_output, truncated, bad_request
    };

    /// <summary>Default <c>code</c> for responses produced by the framework (bare 404, 405, 415 ...).</summary>
    public static string CodeFor(int status) => status switch
    {
        400 => "invalid_request",
        401 => "unauthorized",
        403 => "forbidden",
        404 => "not_found",
        405 => "method_not_allowed",
        409 => "conflict",
        413 => "payload_too_large",
        415 => "unsupported_media_type",
        422 => "unprocessable",
        >= 500 => "internal_error",
        _ => $"http_{status}",
    };

    private static string Title(int status) => status switch
    {
        400 => "Bad request",
        401 => "Authentication required",
        403 => "Forbidden",
        404 => "Not found",
        405 => "Method not allowed",
        409 => "Conflict",
        413 => "Payload too large",
        415 => "Unsupported media type",
        422 => "Unprocessable request",
        >= 500 => "Server error",
        _ => "Request failed",
    };
}

/// <summary>Turns expected failures (<see cref="AppException"/>), malformed requests and bugs into problem responses.</summary>
public sealed class ProblemExceptionHandler(IProblemDetailsService problems, ILogger<ProblemExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        ProblemDetails problem;
        switch (exception)
        {
            case AppException app:
                problem = Problems.Build(Problems.StatusFor(app.Kind), app.Code, app.Message, app.Extensions);
                break;
            case LlmException llm:
                var extensions = new Dictionary<string, object?> { ["attempts"] = llm.Attempts };
                if (llm.RetryAfter is { } wait)
                {
                    extensions["retryAfterSeconds"] = (int)Math.Ceiling(wait.TotalSeconds);
                    context.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                logger.LogWarning("LLM call failed: {code} {message}", llm.Code, llm.Message);
                problem = Problems.Build(Problems.StatusFor(llm), llm.Code, llm.Message, extensions);
                break;
            case BadHttpRequestException bad:
                problem = Problems.Build(
                    bad.StatusCode,
                    bad.StatusCode switch
                    {
                        StatusCodes.Status415UnsupportedMediaType => "unsupported_media_type",
                        StatusCodes.Status413PayloadTooLarge => "payload_too_large",
                        _ => "invalid_request",
                    },
                    bad.InnerException is JsonException ? "The request body is not valid JSON for this endpoint." : bad.Message);
                break;
            default:
                logger.LogError(exception, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
                problem = Problems.Build(StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred.");
                break;
        }

        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = context, ProblemDetails = problem, Exception = exception });
    }
}
