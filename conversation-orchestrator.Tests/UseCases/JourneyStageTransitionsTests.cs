using conversation_orchestrator.Application.UseCases;
using conversation_orchestrator.Domain;
using Xunit;

namespace conversation_orchestrator.Tests.UseCases;

public class JourneyStageTransitionsTests
{
    [Theory]
    [InlineData(JourneyStage.Started, JourneyTrigger.RequestedRenegotiation, JourneyStage.IdentificationPending)]
    [InlineData(JourneyStage.IdentificationPending, JourneyTrigger.ProvidedIdentification, JourneyStage.CustomerIdentified)]
    [InlineData(JourneyStage.CustomerIdentified, JourneyTrigger.SelectedContract, JourneyStage.ContractSelected)]
    [InlineData(JourneyStage.ContractSelected, JourneyTrigger.EligibilityConfirmed, JourneyStage.EligibilityChecked)]
    [InlineData(JourneyStage.EligibilityChecked, JourneyTrigger.RequestedSimulation, JourneyStage.SimulationParametersPending)]
    [InlineData(JourneyStage.SimulationParametersPending, JourneyTrigger.ProposalPresented, JourneyStage.ProposalAvailable)]
    [InlineData(JourneyStage.ProposalAvailable, JourneyTrigger.SelectedProposal, JourneyStage.ProposalSelected)]
    [InlineData(JourneyStage.ProposalSelected, JourneyTrigger.ConfirmedAgreement, JourneyStage.AgreementProcessing)]
    [InlineData(JourneyStage.HandoffRequested, JourneyTrigger.RequestedRenegotiation, JourneyStage.IdentificationPending)]
    public void TryGetNext_LegalTransition_ReturnsExpectedNextStage(JourneyStage from, JourneyTrigger trigger, JourneyStage expected)
    {
        var result = JourneyStageTransitions.TryGetNext(from, trigger, out var next);

        Assert.True(result);
        Assert.Equal(expected, next);
    }

    [Theory]
    [InlineData(JourneyStage.Started, JourneyTrigger.ConfirmedAgreement)]
    [InlineData(JourneyStage.Started, JourneyTrigger.EligibilityConfirmed)]
    [InlineData(JourneyStage.ProposalAvailable, JourneyTrigger.ProvidedIdentification)]
    [InlineData(JourneyStage.HandoffRequested, JourneyTrigger.ProvidedIdentification)]
    public void TryGetNext_IllegalTransitionFromCurrentStage_ReturnsFalse(JourneyStage from, JourneyTrigger trigger)
    {
        var result = JourneyStageTransitions.TryGetNext(from, trigger, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryGetNext_NoneTrigger_ReturnsFalse()
    {
        var result = JourneyStageTransitions.TryGetNext(JourneyStage.Started, JourneyTrigger.None, out _);

        Assert.False(result);
    }

    [Theory]
    [InlineData(JourneyStage.Started)]
    [InlineData(JourneyStage.IdentificationPending)]
    [InlineData(JourneyStage.ProposalSelected)]
    [InlineData(JourneyStage.AgreementProcessing)]
    public void TryGetNext_RequestedCancellation_LegalFromMostStages(JourneyStage from)
    {
        var result = JourneyStageTransitions.TryGetNext(from, JourneyTrigger.RequestedCancellation, out var next);

        Assert.True(result);
        Assert.Equal(JourneyStage.Cancelled, next);
    }

    [Theory]
    [InlineData(JourneyStage.Completed)]
    [InlineData(JourneyStage.Cancelled)]
    public void TryGetNext_RequestedCancellation_NotLegalFromTerminalStages(JourneyStage from)
    {
        var result = JourneyStageTransitions.TryGetNext(from, JourneyTrigger.RequestedCancellation, out _);

        Assert.False(result);
    }
}
