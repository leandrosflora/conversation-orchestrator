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
    public void ResolveTenantSkills_AssignedTenant_ReturnsItsSkillIds()
    {
        var registry = BuildRegistry(
            skills: [new AgentSkillEntry { Id = "renegotiation", BaseUrl = "http://localhost/" }],
            assignments: new Dictionary<string, List<string>> { ["tenant-a"] = ["renegotiation"] });

        var skillIds = registry.ResolveTenantSkills("tenant-a");

        Assert.Equal(["renegotiation"], skillIds);
    }

    [Fact]
    public void ResolveTenantSkills_TenantWithMultipleSkills_ReturnsAllInOrder()
    {
        var registry = BuildRegistry(
            skills:
            [
                new AgentSkillEntry { Id = "renegotiation", BaseUrl = "http://localhost/" },
                new AgentSkillEntry { Id = "cartao-credito", BaseUrl = "http://localhost/" }
            ],
            assignments: new Dictionary<string, List<string>>
            {
                ["tenant-a"] = ["renegotiation", "cartao-credito"]
            });

        var skillIds = registry.ResolveTenantSkills("tenant-a");

        Assert.Equal(["renegotiation", "cartao-credito"], skillIds);
    }

    [Fact]
    public void ResolveTenantSkills_UnassignedTenant_ReturnsEmptyWithoutThrowing()
    {
        var registry = BuildRegistry(
            skills: [new AgentSkillEntry { Id = "renegotiation", BaseUrl = "http://localhost/" }],
            assignments: []);

        var skillIds = registry.ResolveTenantSkills("tenant-unknown");

        Assert.Empty(skillIds);
    }

    [Fact]
    public void GetSkillEntries_ReturnsConfiguredEntriesForGivenIds()
    {
        var renegotiation = new AgentSkillEntry
        {
            Id = "renegotiation",
            BaseUrl = "http://localhost/",
            SelectionButtonId = "skill_renegotiation",
            SelectionButtonTitle = "Renegociar dívidas"
        };
        var cartao = new AgentSkillEntry
        {
            Id = "cartao-credito",
            BaseUrl = "http://localhost/",
            SelectionButtonId = "skill_cartao",
            SelectionButtonTitle = "Fatura do cartão"
        };
        var registry = BuildRegistry(skills: [renegotiation, cartao], assignments: []);

        var entries = registry.GetSkillEntries(["renegotiation", "cartao-credito"]);

        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Id == "renegotiation");
        Assert.Contains(entries, e => e.Id == "cartao-credito");
    }

    [Fact]
    public void GetSkillEntries_ExcludesSkillsNotInTheGivenIdList()
    {
        var registry = BuildRegistry(
            skills:
            [
                new AgentSkillEntry { Id = "renegotiation", BaseUrl = "http://localhost/" },
                new AgentSkillEntry { Id = "cartao-credito", BaseUrl = "http://localhost/" }
            ],
            assignments: []);

        var entries = registry.GetSkillEntries(["renegotiation"]);

        Assert.Single(entries);
        Assert.Equal("renegotiation", entries[0].Id);
    }

    [Fact]
    public void ResolveSkillIdBySelectionButton_MatchingButtonId_ReturnsSkillId()
    {
        var registry = BuildRegistry(
            skills:
            [
                new AgentSkillEntry
                {
                    Id = "cartao-credito",
                    BaseUrl = "http://localhost/",
                    SelectionButtonId = "skill_cartao",
                    SelectionButtonTitle = "Fatura do cartão"
                }
            ],
            assignments: []);

        var skillId = registry.ResolveSkillIdBySelectionButton(["cartao-credito"], "skill_cartao");

        Assert.Equal("cartao-credito", skillId);
    }

    [Fact]
    public void ResolveSkillIdBySelectionButton_UnknownButtonId_ReturnsNull()
    {
        var registry = BuildRegistry(
            skills:
            [
                new AgentSkillEntry
                {
                    Id = "cartao-credito",
                    BaseUrl = "http://localhost/",
                    SelectionButtonId = "skill_cartao",
                    SelectionButtonTitle = "Fatura do cartão"
                }
            ],
            assignments: []);

        var skillId = registry.ResolveSkillIdBySelectionButton(["cartao-credito"], "not_a_real_button");

        Assert.Null(skillId);
    }

    [Fact]
    public void ResolveSkillIdBySelectionButton_ButtonIdOutsideCandidateList_ReturnsNull()
    {
        var registry = BuildRegistry(
            skills:
            [
                new AgentSkillEntry
                {
                    Id = "renegotiation",
                    BaseUrl = "http://localhost/",
                    SelectionButtonId = "skill_renegotiation",
                    SelectionButtonTitle = "Renegociar dívidas"
                }
            ],
            assignments: []);

        // The button matches a configured skill, but that skill isn't among this tenant's
        // assigned candidates - must not resolve to a skill the tenant was never offered.
        var skillId = registry.ResolveSkillIdBySelectionButton([], "skill_renegotiation");

        Assert.Null(skillId);
    }

    private static IAgentSkillRegistry BuildRegistry(
        List<AgentSkillEntry> skills,
        Dictionary<string, List<string>> assignments)
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
