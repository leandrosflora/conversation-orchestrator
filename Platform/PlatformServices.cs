using System.Collections.Concurrent;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace conversation_orchestrator.Platform;

public sealed class InternalAuthOptions
{
    public const string SectionName = "InternalAuth";
    public string Issuer { get; init; } = "conversational-ai-platform";
    public string ServiceName { get; init; } = "conversation-orchestrator";
    public string SigningKey { get; init; } = string.Empty;
    public int TokenTtlSeconds { get; init; } = 300;
}

public sealed class TenantContext
{
    private static readonly AsyncLocal<string?> Current = new();
    public string TenantId => Current.Value
        ?? throw new InvalidOperationException("Tenant context is not available.");

    public IDisposable Push(string tenantId)
    {
        var previous = Current.Value;
        Current.Value = tenantId;
        return new Scope(() => Current.Value = previous);
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}

public sealed class InternalTokenService(IOptions<InternalAuthOptions> options)
{
    public string CreateToken(string audience)
    {
        var value = options.Value;
        if (string.IsNullOrWhiteSpace(value.SigningKey))
        {
            throw new InvalidOperationException("InternalAuth:SigningKey is required.");
        }

        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: value.Issuer,
            audience: audience,
            claims: [new Claim(JwtRegisteredClaimNames.Sub, value.ServiceName)],
            notBefore: now,
            expires: now.AddSeconds(Math.Max(30, value.TokenTtlSeconds)),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(value.SigningKey)),
                SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public sealed class InternalRequestHandler(
    InternalTokenService tokenService,
    TenantContext tenantContext,
    string audience) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new("Bearer", tokenService.CreateToken(audience));
        request.Headers.Remove("X-Tenant-Id");
        request.Headers.TryAddWithoutValidation("X-Tenant-Id", tenantContext.TenantId);
        return base.SendAsync(request, cancellationToken);
    }
}

public sealed class PlatformMetrics
{
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly ConcurrentDictionary<string, double> _durationSums = new();
    private readonly ConcurrentDictionary<string, long> _durationCounts = new();

    public void Increment(string name, params (string Name, string Value)[] labels) =>
        _counters.AddOrUpdate(Key(name, labels), 1, (_, value) => value + 1);

    public void Observe(string name, double seconds, params (string Name, string Value)[] labels)
    {
        var key = Key(name, labels);
        _durationSums.AddOrUpdate(key, seconds, (_, value) => value + seconds);
        _durationCounts.AddOrUpdate(key, 1, (_, value) => value + 1);
    }

    public string Render()
    {
        var output = new StringBuilder();
        foreach (var item in _counters.OrderBy(item => item.Key))
            output.Append(item.Key).Append(' ').Append(item.Value).AppendLine();
        foreach (var item in _durationCounts.OrderBy(item => item.Key))
        {
            _durationSums.TryGetValue(item.Key, out var sum);
            output.Append(item.Key.Replace("_seconds", "_seconds_count", StringComparison.Ordinal))
                .Append(' ').Append(item.Value).AppendLine();
            output.Append(item.Key.Replace("_seconds", "_seconds_sum", StringComparison.Ordinal))
                .Append(' ').Append(sum.ToString(System.Globalization.CultureInfo.InvariantCulture)).AppendLine();
        }
        return output.ToString();
    }

    private static string Key(string name, params (string Name, string Value)[] labels)
    {
        if (labels.Length == 0) return Sanitize(name);
        return $"{Sanitize(name)}{{{string.Join(',', labels.Select(label => $"{Sanitize(label.Name)}=\"{Escape(label.Value)}\""))}}}";
    }

    private static string Sanitize(string value) => Regex.Replace(value, "[^a-zA-Z0-9_:]", "_");
    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

public sealed class PlatformMetricsMiddleware(RequestDelegate next, PlatformMetrics metrics)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        try { await next(context); }
        finally
        {
            stopwatch.Stop();
            var path = Regex.Replace(context.Request.Path.Value ?? "/", "/[A-Za-z0-9_-]{24,}", "/{id}");
            metrics.Increment("platform_http_requests_total",
                ("method", context.Request.Method), ("path", path), ("status", context.Response.StatusCode.ToString()));
            metrics.Observe("platform_http_request_duration_seconds", stopwatch.Elapsed.TotalSeconds,
                ("method", context.Request.Method), ("path", path));
        }
    }
}

public static class PlatformServiceExtensions
{
    public static IServiceCollection AddPlatformServices(this IServiceCollection services, IConfiguration configuration)
    {
        var auth = configuration.GetSection(InternalAuthOptions.SectionName).Get<InternalAuthOptions>()
            ?? new InternalAuthOptions();
        services.Configure<InternalAuthOptions>(configuration.GetSection(InternalAuthOptions.SectionName));
        services.AddSingleton<TenantContext>();
        services.AddSingleton<InternalTokenService>();
        services.AddSingleton<PlatformMetrics>();

        var key = string.IsNullOrWhiteSpace(auth.SigningKey)
            ? "invalid-missing-internal-auth-signing-key"
            : auth.SigningKey;
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options => options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = auth.Issuer,
                ValidateAudience = true,
                ValidAudience = auth.ServiceName,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            });
        services.AddAuthorization();
        return services;
    }

    public static WebApplication UsePlatformServices(this WebApplication app)
    {
        app.UseMiddleware<PlatformMetricsMiddleware>();
        app.UseAuthentication();
        app.UseAuthorization();
        return app;
    }

    public static WebApplication MapPlatformEndpoints(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" })).AllowAnonymous();
        app.MapGet("/metrics", (PlatformMetrics metrics) =>
            Results.Text(metrics.Render(), "text/plain; version=0.0.4")).AllowAnonymous();
        return app;
    }
}
