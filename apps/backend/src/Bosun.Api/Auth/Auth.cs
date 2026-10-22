using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;
using Bosun.Application;
using Bosun.Infrastructure.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;

namespace Bosun.Api.Auth;

public sealed record AuthUser(string Name, string Password, IReadOnlyList<string> Roles);

/// <summary>
/// The configured human users. Admin comes from ADMIN_USERNAME / ADMIN_PASSWORD. A second user with the
/// <c>approver</c> role is needed for the SIL/ESD second approval: from APPROVER_USERNAME / APPROVER_PASSWORD, and in
/// Development only a documented, deliberately obvious default is added (see <see cref="DevApproverUsername"/>).
/// </summary>
public sealed class UserStore
{
    /// <summary>DEVELOPMENT ONLY. Never added outside the Development environment.</summary>
    public const string DevApproverUsername = "approver";

    /// <summary>DEVELOPMENT ONLY. Public on purpose: it only exists so the second-approver flow can be demonstrated locally.</summary>
    public const string DevApproverPassword = "approver-dev-only";

    private readonly List<AuthUser> users = [];

    public UserStore(IConfiguration configuration, IHostEnvironment environment)
    {
        // An account without a password does not exist: an empty password is never accepted, in any environment.
        // Read raw env vars directly alongside the config snapshot so container runtimes (e.g. Render) that
        // inject env vars into the process environment are always honoured.
        var admin = configuration["Auth:AdminUsername"]
            ?? Environment.GetEnvironmentVariable("ADMIN_USERNAME")
            ?? "admin";
        var adminPassword = Environment.GetEnvironmentVariable("ADMIN_PASSWORD")
            is { Length: > 0 } rawPwd ? rawPwd : configuration["Auth:AdminPassword"];
        if (!string.IsNullOrEmpty(adminPassword))
        {
            users.Add(new AuthUser(admin, adminPassword, ["admin"]));
        }

        var approver = configuration["Auth:ApproverUsername"]
            ?? Environment.GetEnvironmentVariable("APPROVER_USERNAME");
        var approverPassword = Environment.GetEnvironmentVariable("APPROVER_PASSWORD")
            is { Length: > 0 } rawAppPwd ? rawAppPwd : configuration["Auth:ApproverPassword"];
        if (!string.IsNullOrEmpty(approver) && !string.IsNullOrEmpty(approverPassword))
        {
            users.Add(new AuthUser(approver, approverPassword, [UserContext.ApproverRole]));
        }
        else if (environment.IsDevelopment())
        {
            users.Add(new AuthUser(DevApproverUsername, DevApproverPassword, [UserContext.ApproverRole]));
        }
    }

    public IEnumerable<AuthUser> Users => users;

    public AuthUser? Validate(string? username, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return null;
        }

        // Compare fixed-length hashes in constant time; always scan every user so timing does not reveal which names exist.
        var candidate = Hash(password);
        AuthUser? match = null;
        foreach (var user in users)
        {
            var passwordOk = CryptographicOperations.FixedTimeEquals(Hash(user.Password), candidate);
            var nameOk = string.Equals(user.Name, username, StringComparison.Ordinal);
            if (passwordOk && nameOk)
            {
                match = user;
            }
        }

        return match;
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}

public static class AuthSetup
{
    public const string CookieName = "bosun.session";

    public const string LoginRateLimitPolicy = "login";

    /// <summary>Per signed-in user: POST /api/projects/{id}/ask spends LLM quota. Default 20 per minute (Knowledge:AskRateLimit:PermitLimit / WindowSeconds).</summary>
    public const string AskRateLimitPolicy = "ask";

    public static IServiceCollection AddBosunAuth(this IServiceCollection services)
    {
        services.AddSingleton<UserStore>();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.Cookie.Name = CookieName;
                options.Cookie.HttpOnly = true;

                // The SPAs reach the API through the Vite/nginx same-origin proxy, so Strict costs nothing and blocks CSRF.
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // tightened to Always outside Development below
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
                options.SlidingExpiration = true;

                // An API answers 401/403 with a problem body; it never redirects to a login page.
                options.Events.OnRedirectToLogin = ctx => WriteStatus(ctx.HttpContext, StatusCodes.Status401Unauthorized);
                options.Events.OnRedirectToAccessDenied = ctx => WriteStatus(ctx.HttpContext, StatusCodes.Status403Forbidden);
            });
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<IHostEnvironment>((options, environment) =>
            {
                // Behind TLS termination (nginx) the app sees http; UseForwardedHeaders restores the original scheme, so
                // "Always" is safe there. Only the local Development loop (plain http://127.0.0.1) needs SameAsRequest.
                options.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            });
        services.AddAuthorization();

        // Credential-guessing brake: a fixed window per client address on POST /api/auth/login only.
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(LoginRateLimitPolicy, http =>
            {
                var config = http.RequestServices.GetRequiredService<IConfiguration>();
                var permits = config.GetValue("Auth:LoginRateLimit:PermitLimit", 10);
                var window = config.GetValue("Auth:LoginRateLimit:WindowSeconds", 60);
                return RateLimitPartition.GetFixedWindowLimiter(
                    http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromSeconds(window), QueueLimit = 0 });
            });
            options.AddPolicy(AskRateLimitPolicy, http =>
            {
                // The limiter runs after authentication; an anonymous call is not counted here (authorization answers it with 401).
                if (http.User.Identity is not { IsAuthenticated: true, Name: { Length: > 0 } user })
                {
                    return RateLimitPartition.GetNoLimiter("anonymous");
                }

                var config = http.RequestServices.GetRequiredService<IConfiguration>();
                var permits = config.GetValue("Knowledge:AskRateLimit:PermitLimit", 20);
                var window = config.GetValue("Knowledge:AskRateLimit:WindowSeconds", 60);
                return RateLimitPartition.GetFixedWindowLimiter(
                    "ask:" + user,
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromSeconds(window), QueueLimit = 0 });
            });
            options.OnRejected = async (context, ct) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                var policy = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
                var problem = policy == AskRateLimitPolicy
                    ? Problems.Build(StatusCodes.Status429TooManyRequests, "rate_limited", "Too many questions in a short time (limit: 20 per minute per user). Wait for the Retry-After seconds and ask again.")
                    : Problems.Build(StatusCodes.Status429TooManyRequests, "login_rate_limited", "Too many sign-in attempts. Wait a minute and try again.");
                context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await context.HttpContext.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", ct);
            };
        });
        return services;
    }

    private static Task WriteStatus(HttpContext context, int status)
    {
        context.Response.StatusCode = status;
        return Task.CompletedTask; // the status-code-pages middleware renders the RFC 7807 body
    }

    /// <summary>Refuses to boot outside Development with the documented default admin password (or none).</summary>
    public static void EnsureSafeCredentials(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            return;
        }

        if (string.Equals(Environment.GetEnvironmentVariable("ALLOW_DEFAULT_CREDENTIALS"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // BosunEnvironment maps ADMIN_PASSWORD → Auth:AdminPassword at startup via a MemoryConfigurationSource
        // snapshot. Read the raw env var directly as well so container runtimes (e.g. Render) that inject
        // environment variables into the process environment are always honoured, regardless of snapshot timing.
        var password = configuration["Auth:AdminPassword"];
        var rawEnvPassword = Environment.GetEnvironmentVariable("ADMIN_PASSWORD");
        var effectivePassword = string.IsNullOrWhiteSpace(rawEnvPassword) ? password : rawEnvPassword;

        if (string.IsNullOrWhiteSpace(effectivePassword) || effectivePassword == BosunEnvironment.DefaultAdminPassword)
        {
            throw new InvalidOperationException(
                $"Refusing to start in the '{environment.EnvironmentName}' environment with the default or an empty ADMIN_PASSWORD. Set ADMIN_PASSWORD to a real secret.");
        }

        if (configuration["Auth:ApproverPassword"] == UserStore.DevApproverPassword)
        {
            throw new InvalidOperationException(
                $"Refusing to start in the '{environment.EnvironmentName}' environment with the development approver password.");
        }
    }

    public static UserContext ToUser(this ClaimsPrincipal principal) =>
        new(principal.Identity?.Name ?? "", principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToHashSet());

    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/login", async Task<Results<Ok<SessionDto>, ProblemHttpResult>> (LoginBody? body, UserStore users, HttpContext http) =>
        {
            var user = users.Validate(body?.Username, body?.Password);
            if (user is null)
            {
                return Problems.Result(StatusCodes.Status401Unauthorized, "invalid_credentials", "Invalid username or password.");
            }

            var identity = new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, user.Name), .. user.Roles.Select(r => new Claim(ClaimTypes.Role, r))],
                CookieAuthenticationDefaults.AuthenticationScheme);
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return TypedResults.Ok(new SessionDto(user.Name, user.Roles));
        }).AllowAnonymous().RequireRateLimiting(LoginRateLimitPolicy).WithSummary("Sign in; sets the HttpOnly session cookie 'bosun.session'. Rate limited per client address (429 login_rate_limited).");

        group.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return TypedResults.NoContent();
        }).AllowAnonymous();

        group.MapGet("/me", (ClaimsPrincipal principal) =>
        {
            var user = principal.ToUser();
            return TypedResults.Ok(new SessionDto(user.Name, user.Roles.Order().ToList()));
        }).RequireAuthorization().WithSummary("The signed-in user, or 401.");
    }
}

public sealed record LoginBody(string? Username, string? Password);

public sealed record SessionDto(string Username, IReadOnlyList<string> Roles);
