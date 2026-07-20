using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Confluent.Kafka;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using JsonWebToken = Microsoft.IdentityModel.JsonWebTokens.JsonWebToken;

namespace conversation_orchestrator.Platform;

public sealed class InternalAuthOptions
{
    public const string SectionName = "InternalAuth";

    public bool Enabled { get; init; } = true;
    public string Issuer { get; init; } = "conversational-ai-platform";
    public string ServiceName { get; init; } = "conversation-orchestrator";
    public int TokenTtlSeconds { get; init; } = 300;

    /// <summary>Audience (callee service name) -> secret used to sign outbound tokens to that audience.</summary>
    public Dictionary<string, string> OutboundSecrets { get; init; } = new();

    /// <summary>Caller (issuer service name) -> secret used to validate inbound tokens from that caller.</summary>
    public Dictionary<string, string> InboundSecrets { get; init; } = new();

    public static bool HasValidSecret(string? secret) =>
        !string.IsNullOrEmpty(secret) && Encoding.UTF8.GetByteCount(secret) >= 32;
}

public sealed class TenantContext
{
    public const string ClaimType = "tenant_id";
    private static readonly AsyncLocal<string?> Current = new();

    public string TenantId => Current.Value
        ?? throw new InvalidOperationException("Tenant context is not available.");

    public IDisposable Push(string tenantId)
    {
        if (!TryNormalize(tenantId, out var normalizedTenantId))
        {
            throw new ArgumentException("Tenant ID must be a non-empty UUID.", nameof(tenantId));
        }

        var previous = Current.Value;
        Current.Value = normalizedTenantId;
        return new Scope(() => Current.Value = previous);
    }

    public static bool TryNormalize(string? tenantId, out string normalizedTenantId)
    {
        normalizedTenantId = string.Empty;
        if (!Guid.TryParse(tenantId?.Trim(), out var parsed) || parsed == Guid.Empty)
        {
            return false;
        }
        normalizedTenantId = parsed.ToString("D");
        return true;
    }

    public static bool TryResolveAuthenticatedTenant(
        ClaimsPrincipal principal,
        string? headerTenant,
        out string tenantId)
    {
        tenantId = string.Empty;
        if (!TryNormalize(headerTenant, out var canonicalHeader))
        {
            return false;
        }
        if (!TryNormalize(principal.FindFirstValue(ClaimType), out var canonicalClaim))
        {
            return false;
        }
        if (!string.Equals(canonicalHeader, canonicalClaim, StringComparison.Ordinal))
        {
            return false;
        }
        tenantId = canonicalClaim;
        return true;
    }

    private sealed class Scope(Action dispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            dispose();
        }
    }
}

public sealed class InternalTokenService(IOptions<InternalAuthOptions> options)
{
    public string CreateToken(string audience, string tenantId)
    {
        var value = options.Value;
        if (!value.Enabled)
        {
            throw new InvalidOperationException("Internal authentication is disabled.");
        }
        if (!value.OutboundSecrets.TryGetValue(audience, out var secret) || !InternalAuthOptions.HasValidSecret(secret))
        {
            throw new InvalidOperationException(
                $"InternalAuth:OutboundSecrets:{audience} must be configured with at least 32 UTF-8 bytes.");
        }
        if (!TenantContext.TryNormalize(tenantId, out var canonicalTenant))
        {
            throw new ArgumentException("Tenant ID must be a non-empty UUID.", nameof(tenantId));
        }

        var now = DateTime.UtcNow;
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, value.ServiceName),
            new Claim(TenantContext.ClaimType, canonicalTenant),
            new Claim(
                JwtRegisteredClaimNames.Iat,
                EpochTime.GetIntDate(now).ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("n"))
        };

        var signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)),
            SecurityAlgorithms.HmacSha256);

        var header = new JwtHeader(signingCredentials);
        header["kid"] = value.ServiceName;
        var payload = new JwtPayload(
            issuer: value.Issuer,
            audience: audience,
            claims: claims,
            notBefore: now,
            expires: now.AddSeconds(Math.Clamp(value.TokenTtlSeconds, 30, 900)),
            issuedAt: null);
        var token = new JwtSecurityToken(header, payload);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public sealed class InternalRequestHandler(
    InternalTokenService tokenService,
    IOptions<InternalAuthOptions> authOptions,
    TenantContext tenantContext,
    string audience) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var tenantId = tenantContext.TenantId;
        if (authOptions.Value.Enabled)
        {
            request.Headers.Authorization = new("Bearer", tokenService.CreateToken(audience, tenantId));
        }
        request.Headers.Remove("X-Tenant-Id");
        request.Headers.TryAddWithoutValidation("X-Tenant-Id", tenantId);
        return base.SendAsync(request, cancellationToken);
    }
}

public sealed class PlatformMetrics
{
    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly ConcurrentDictionary<string, double> _durationSums = new();
    private readonly ConcurrentDictionary<string, long> _durationCounts = new();

    public void Increment(string name, params (string Name, string Value)[] labels) =>
        _counters.AddOrUpdate(Key(name, labels), 1, static (_, value) => value + 1);

    public void Observe(string name, double seconds, params (string Name, string Value)[] labels)
    {
        var key = Key(name, labels);
        _durationSums.AddOrUpdate(key, seconds, (_, value) => value + seconds);
        _durationCounts.AddOrUpdate(key, 1, static (_, value) => value + 1);
    }

    public string Render()
    {
        var output = new StringBuilder();
        foreach (var item in _counters.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            output.Append(item.Key).Append(' ').Append(item.Value).AppendLine();
        }
        foreach (var item in _durationCounts.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            _durationSums.TryGetValue(item.Key, out var sum);
            output.Append(ReplaceMetricName(item.Key, "_seconds", "_seconds_count"))
                .Append(' ').Append(item.Value).AppendLine();
            output.Append(ReplaceMetricName(item.Key, "_seconds", "_seconds_sum"))
                .Append(' ').Append(sum.ToString(CultureInfo.InvariantCulture)).AppendLine();
        }
        return output.ToString();
    }

    private static string Key(string name, params (string Name, string Value)[] labels)
    {
        var metricName = Sanitize(name);
        if (labels.Length == 0)
        {
            return metricName;
        }
        var renderedLabels = string.Join(",", labels.Select(label => $"{Sanitize(label.Name)}=\"{Escape(label.Value)}\""));
        return $"{metricName}{{{renderedLabels}}}";
    }

    private static string ReplaceMetricName(string key, string suffix, string replacement)
    {
        var labelsIndex = key.IndexOf('{');
        var name = labelsIndex >= 0 ? key[..labelsIndex] : key;
        var labels = labelsIndex >= 0 ? key[labelsIndex..] : string.Empty;
        return name.EndsWith(suffix, StringComparison.Ordinal)
            ? name[..^suffix.Length] + replacement + labels
            : name + replacement + labels;
    }

    private static string Sanitize(string value) => Regex.Replace(value, "[^a-zA-Z0-9_:]", "_");

    private static string Escape(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);
}

public sealed class PlatformMetricsMiddleware(RequestDelegate next, PlatformMetrics metrics)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await next(context);
        }
        finally
        {
            stopwatch.Stop();
            var path = NormalizePath(context.Request.Path.Value ?? "/");
            metrics.Increment(
                "platform_http_requests_total",
                ("method", context.Request.Method),
                ("path", path),
                ("status", context.Response.StatusCode.ToString(CultureInfo.InvariantCulture)));
            metrics.Observe(
                "platform_http_request_duration_seconds",
                stopwatch.Elapsed.TotalSeconds,
                ("method", context.Request.Method),
                ("path", path));
        }
    }

    private static string NormalizePath(string path)
    {
        path = Regex.Replace(path, "/[0-9a-fA-F]{8}-[0-9a-fA-F-]{27,}", "/{id}");
        path = Regex.Replace(path, "/[A-Za-z0-9_-]{24,}", "/{id}");
        return path;
    }
}

public static class PlatformServiceExtensions
{
    /// <summary>Audiences conversation-orchestrator issues outbound internal-auth tokens for.
    /// Mirrors the InternalRequestHandler registrations in Program.cs.</summary>
    private static readonly string[] ExpectedOutboundAudiences =
    [
        "whatsapp-bff",
        "agent-runtime-renegotiation",
        "conversation-audit-service",
        "conversation-handoff-service",
        "conversation-memory-service"
    ];

    /// <summary>Callers conversation-orchestrator accepts inbound internal-auth tokens from.</summary>
    private static readonly string[] ExpectedInboundCallers =
    [
        "whatsapp-bff"
    ];

    public static IServiceCollection AddPlatformServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var auth = configuration.GetSection(InternalAuthOptions.SectionName).Get<InternalAuthOptions>()
            ?? new InternalAuthOptions();

        services.Configure<InternalAuthOptions>(configuration.GetSection(InternalAuthOptions.SectionName));
        services.AddSingleton<TenantContext>();
        services.AddSingleton<InternalTokenService>();
        services.AddSingleton<PlatformMetrics>();

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    RequireSignedTokens = true,
                    ValidateIssuer = true,
                    ValidIssuer = auth.Issuer,
                    ValidateAudience = true,
                    ValidAudience = auth.ServiceName,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKeyResolver = (token, securityToken, kid, parameters) =>
                    {
                        if (kid is null ||
                            !auth.InboundSecrets.TryGetValue(kid, out var secret) ||
                            !InternalAuthOptions.HasValidSecret(secret))
                        {
                            return Array.Empty<SecurityKey>();
                        }
                        return new SecurityKey[] { new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)) };
                    },
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = JwtRegisteredClaimNames.Sub
                };
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = context =>
                    {
                        var kid = context.SecurityToken switch
                        {
                            JwtSecurityToken jwtSecurityToken => jwtSecurityToken.Header?.Kid,
                            JsonWebToken jsonWebToken => jsonWebToken.Kid,
                            _ => null
                        };
                        var sub = context.Principal?.FindFirstValue(JwtRegisteredClaimNames.Sub);

                        if (kid is null || !string.Equals(kid, sub, StringComparison.Ordinal))
                        {
                            context.HttpContext.RequestServices
                                .GetRequiredService<PlatformMetrics>()
                                .Increment("platform_internal_auth_failures_total", ("reason", "kid_sub_mismatch"));
                            context.Fail("Token 'kid' header does not match 'sub' claim.");
                        }

                        return Task.CompletedTask;
                    },
                    OnAuthenticationFailed = context =>
                    {
                        context.HttpContext.RequestServices
                            .GetRequiredService<PlatformMetrics>()
                            .Increment("platform_internal_auth_failures_total", ("reason", "invalid_token"));
                        return Task.CompletedTask;
                    },
                    OnChallenge = context =>
                    {
                        context.HttpContext.RequestServices
                            .GetRequiredService<PlatformMetrics>()
                            .Increment("platform_internal_auth_failures_total", ("reason", "challenge"));
                        return Task.CompletedTask;
                    }
                };
            });

        services.AddAuthorization(options =>
        {
            options.DefaultPolicy = auth.Enabled
                ? new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .RequireClaim(JwtRegisteredClaimNames.Sub)
                    .RequireClaim(TenantContext.ClaimType)
                    .Build()
                : new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                    .RequireAssertion(_ => true)
                    .Build();
        });

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
        app.MapGet("/health/live", () => Results.Ok(new { status = "live" }))
            .AllowAnonymous();

        app.MapGet("/health/ready", async (
                IOptions<InternalAuthOptions> authOptions,
                NpgsqlDataSource dataSource,
                IAdminClient adminClient,
                CancellationToken cancellationToken) =>
            {
                var failures = new List<string>();
                var auth = authOptions.Value;

                if (auth.Enabled)
                {
                    foreach (var audience in ExpectedOutboundAudiences)
                    {
                        if (!auth.OutboundSecrets.TryGetValue(audience, out var secret) ||
                            !InternalAuthOptions.HasValidSecret(secret))
                        {
                            failures.Add($"internal_auth_outbound_secret_invalid:{audience}");
                        }
                    }

                    foreach (var caller in ExpectedInboundCallers)
                    {
                        if (!auth.InboundSecrets.TryGetValue(caller, out var secret) ||
                            !InternalAuthOptions.HasValidSecret(secret))
                        {
                            failures.Add($"internal_auth_inbound_secret_invalid:{caller}");
                        }
                    }
                }

                try
                {
                    await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
                    await using var command = new NpgsqlCommand("SELECT 1", connection);
                    await command.ExecuteScalarAsync(cancellationToken);
                }
                catch
                {
                    failures.Add("postgres_unavailable");
                }

                try
                {
                    await Task.Run(() => adminClient.GetMetadata(TimeSpan.FromSeconds(2)), cancellationToken);
                }
                catch
                {
                    failures.Add("kafka_unavailable");
                }

                return failures.Count == 0
                    ? Results.Ok(new { status = "ready", failures })
                    : Results.Json(
                        new { status = "not_ready", failures },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
            })
            .AllowAnonymous();

        app.MapGet("/metrics", (PlatformMetrics metrics) =>
                Results.Text(metrics.Render(), "text/plain; version=0.0.4; charset=utf-8"))
            .AllowAnonymous();

        return app;
    }
}
