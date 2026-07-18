using conversation_orchestrator.Domain;

namespace conversation_orchestrator.Application.UseCases;

/// <summary>
/// The fixed (fromStage, trigger) -> toStage table the Orchestrator owns. A trigger with no
/// entry for the session's current stage is illegal from there and must be rejected by the
/// caller (see IngestMessageUseCase) - this is what makes stage progression something the
/// Orchestrator enforces rather than something the LLM can jump around in.
/// </summary>
public static class JourneyStageTransitions
{
    private static readonly Dictionary<(JourneyStage From, JourneyTrigger Trigger), JourneyStage> Table = new()
    {
        [(JourneyStage.Started, JourneyTrigger.RequestedRenegotiation)] = JourneyStage.IdentificationPending,
        [(JourneyStage.IdentificationPending, JourneyTrigger.ProvidedIdentification)] = JourneyStage.CustomerIdentified,
        [(JourneyStage.CustomerIdentified, JourneyTrigger.SelectedContract)] = JourneyStage.ContractSelected,
        [(JourneyStage.ContractSelected, JourneyTrigger.EligibilityConfirmed)] = JourneyStage.EligibilityChecked,
        [(JourneyStage.EligibilityChecked, JourneyTrigger.RequestedSimulation)] = JourneyStage.SimulationParametersPending,
        [(JourneyStage.SimulationParametersPending, JourneyTrigger.ProposalPresented)] = JourneyStage.ProposalAvailable,
        [(JourneyStage.ProposalAvailable, JourneyTrigger.SelectedProposal)] = JourneyStage.ProposalSelected,
        [(JourneyStage.ProposalSelected, JourneyTrigger.ConfirmedAgreement)] = JourneyStage.AgreementProcessing
    };

    private static readonly HashSet<JourneyStage> CancellationExcludedStages =
    [
        JourneyStage.Completed,
        JourneyStage.Cancelled
    ];

    public static bool TryGetNext(JourneyStage from, JourneyTrigger trigger, out JourneyStage to)
    {
        // RequestedCancellation is legal from any stage except the two already-terminal ones -
        // handled separately so it doesn't need a table row per stage.
        if (trigger == JourneyTrigger.RequestedCancellation && !CancellationExcludedStages.Contains(from))
        {
            to = JourneyStage.Cancelled;
            return true;
        }

        return Table.TryGetValue((from, trigger), out to);
    }
}
