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
        [(JourneyStage.ProposalSelected, JourneyTrigger.ConfirmedAgreement)] = JourneyStage.AgreementProcessing,

        // Without this, HandoffRequested has no way out: every governed tool at
        // tool-service-renegotiation denies calls signed with journey_stage=HandoffRequested (see
        // policy.py's per-tool stage allow-lists, none of which include it), and
        // conversation-handoff-service has no endpoint to release a conversation back to the bot.
        // A customer whose conversation was ever handed off - even on a low-confidence false
        // positive - would otherwise be stuck forever, even if no human ever picks it up. Treat a
        // fresh renegotiation request the same as a brand-new conversation: let it restart
        // identification rather than staying silently dead.
        [(JourneyStage.HandoffRequested, JourneyTrigger.RequestedRenegotiation)] = JourneyStage.IdentificationPending
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
