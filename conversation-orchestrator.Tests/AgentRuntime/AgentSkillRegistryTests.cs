using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using conversation_orchestrator.Tests.Testing;
using Xunit;
using conversation_orchestrator.Adapters.Outbound.Http;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Configuration;
using conversation_orchestrator.Platform;

namespace conversation_orchestrator.Tests.AgentRuntime;

public class AgentSkillRegistryTests
{
    [Fact]
    public void Resolve_ConfiguredSkillId_ReturnsAClient()
    {
        var registry = BuildRegistry(
            skills: [new AgentSkillEntry { Id = "renegotiation", BaseUrl = "http://localhost/" }],
            assignments: []);

        var client = registry.Resolve("renegotiation");

        Assert.NotNull(client);
    }

    [Fact]
    public void Resolve_UnknownSkillId_ReturnsNull()
    {
        var registry = BuildRegistry(
            skills: [new AgentSkillEntry { Id = "renegotiation", BaseUrl = "http://localhost/" }],
            assignments: []);

        var client = registry.Resolve("pix");

        Assert.Null(client);
    }

    [Fact]
    public void ResolveTenantSkill_AssignedTenant_ReturnsItsSkillId()
    {
        var registry = BuildRegistry(
            skills: [new AgentSkillEntry { Id = "renegotiation", BaseUrl = "http://localhost/" }],
            assignments: new Dictionary<string, string> { ["tenant-a"] = "renegotiation" });

        var skillId = registry.ResolveTenantSkill("tenant-a");

        Assert.Equal("renegotiation", skillId);
    }

    [Fact]
    public void ResolveTenantSkill_UnassignedTenant_ReturnsNullWithoutThrowing()
    {
        var registry = BuildRegistry(
            skills: [new AgentSkillEntry { Id = "renegotiation", BaseUrl = "http://localhost/" }],
            assignments: []);

        var skillId = registry.ResolveTenantSkill("tenant-unknown");

        Assert.Null(skillId);
    }

    private static IAgentSkillRegistry BuildRegistry(
        List<AgentSkillEntry> skills,
        Dictionary<string, string> assignments)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new PlatformMetrics());
        foreach (var skill in skills)
        {
            services.AddHttpClient(AgentSkillRegistry.HttpClientName(skill.Id), client =>
                {
                    client.BaseAddress = new Uri(skill.BaseUrl);
                })
                .ConfigurePrimaryHttpMessageHandler(() => new StubHttpMessageHandler(
                    _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)));
        }
        services.AddSingleton(Options.Create(new AgentSkillOptions { Skills = skills }));
        services.AddSingleton(Options.Create(new TenantSkillOptions { Assignments = assignments }));
        services.AddSingleton<IAgentSkillRegistry, AgentSkillRegistry>();

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IAgentSkillRegistry>();
    }
}
