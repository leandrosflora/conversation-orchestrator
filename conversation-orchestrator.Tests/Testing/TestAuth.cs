using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace conversation_orchestrator.Tests.Testing;

/// <summary>
/// Mirrors conversation-orchestrator's own InternalTokenService (Platform/PlatformServices.cs) so
/// WebApplicationFactory-based endpoint tests can mint a JWT that satisfies the tenant claim check
/// in MessageIngestionEndpoints (TenantContext.TryResolveAuthenticatedTenant), which is enforced
/// regardless of the InternalAuth:Enabled toggle.
///
/// Under the per-pair internal-auth secret model, conversation-orchestrator's only inbound caller
/// is whatsapp-bff (POST /messages). Tokens are signed with the whatsapp-bff -> conversation-orchestrator
/// pair secret (InternalAuth:InboundSecrets:whatsapp-bff on this service) and carry kid == sub == "whatsapp-bff",
/// matching the kid/sub consistency check enforced in PlatformServices.cs's OnTokenValidated handler.
/// </summary>
public static class TestAuth
{
    public const string InboundCallerName = "whatsapp-bff";
    public const string SigningKey = "test-only-internal-auth-whatsapp-bff-secret-32-bytes-min";
    public const string Issuer = "conversational-ai-platform";
    public const string Audience = "conversation-orchestrator";
    public const string TenantId = "00000000-0000-0000-0000-000000000001";

    public static void ConfigureSigningKey(IWebHostBuilder builder) =>
        builder.UseSetting($"InternalAuth:InboundSecrets:{InboundCallerName}", SigningKey);

    public static string IssueToken()
    {
        var now = DateTime.UtcNow;
        var signingCredentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var header = new JwtHeader(signingCredentials);
        header["kid"] = InboundCallerName;
        var payload = new JwtPayload(
            issuer: Issuer,
            audience: Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, InboundCallerName),
                new Claim("tenant_id", TenantId)
            ],
            notBefore: now,
            expires: now.AddMinutes(5),
            issuedAt: null);
        var token = new JwtSecurityToken(header, payload);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
