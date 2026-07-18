namespace conversation_orchestrator.Application.UseCases;

/// <summary>
/// A recognized journey-advancing event, classified by JourneyTriggerClassifier from the
/// Agent Runtime's freeform Intent string. The Orchestrator - not the LLM - decides what
/// counts as a trigger and whether it's legal to apply from the session's current stage
/// (see JourneyStageTransitions).
/// </summary>
public enum JourneyTrigger
{
    /// <summary>Intent didn't match any recognized trigger (or was null) - no transition.</summary>
    None,
    RequestedRenegotiation,
    ProvidedIdentification,
    SelectedContract,
    EligibilityConfirmed,
    RequestedSimulation,
    ProposalPresented,
    SelectedProposal,
    ConfirmedAgreement,
    RequestedCancellation
}
