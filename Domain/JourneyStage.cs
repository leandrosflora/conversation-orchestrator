namespace conversation_orchestrator.Domain;

/// <summary>
/// The 17 real stages of the debt-renegotiation journey. Not every stage is reachable by
/// the transition table in Application/UseCases/JourneyStageTransitions.cs yet - some
/// require a verified tool-execution outcome (eligibility, simulation, confirmation,
/// document generation) that agent-runtime-renegotiation does not surface to this service
/// today. Each member below notes its current reachability.
/// </summary>
public enum JourneyStage
{
    /// <summary>New session, nothing has happened yet.</summary>
    Started,

    /// <summary>Reachable: customer asked to renegotiate, awaiting identification.</summary>
    IdentificationPending,

    /// <summary>Not reachable yet - no distinct authentication step exists in this workspace
    /// (Core Bancário mock's client lookup has no separate auth call).</summary>
    AuthenticationPending,

    /// <summary>Reachable: customer provided identifying information.</summary>
    CustomerIdentified,

    /// <summary>Not reachable yet - collapsed into CustomerIdentified; no distinct signal
    /// makes a separate persisted stage meaningful today.</summary>
    ContractSelectionPending,

    /// <summary>Reachable: customer indicated which contract/debt to discuss.</summary>
    ContractSelected,

    /// <summary>Reachable: customer's message was classified as an eligibility check request.
    /// Note: this reflects the customer asking, not a verified eligibility result.</summary>
    EligibilityChecked,

    /// <summary>Reachable: customer asked for a renegotiation simulation.</summary>
    SimulationParametersPending,

    /// <summary>Reachable: a proposal was presented in the conversation.</summary>
    ProposalAvailable,

    /// <summary>Reachable: customer selected one of the presented proposals.</summary>
    ProposalSelected,

    /// <summary>Not reachable yet - conceptually sits between ProposalSelected and
    /// AgreementProcessing; ProposalSelected already carries this meaning in this increment.</summary>
    ConfirmationPending,

    /// <summary>Reachable: customer explicitly confirmed the agreement.</summary>
    AgreementProcessing,

    /// <summary>Not reachable yet - requires a verified `confirmar_acordo` tool outcome not
    /// surfaced to conversation-orchestrator today.</summary>
    AgreementConfirmed,

    /// <summary>Not reachable yet - requires a verified `gerar_documento` tool outcome.</summary>
    DocumentAvailable,

    /// <summary>Not reachable yet - requires knowing the journey genuinely finished.</summary>
    Completed,

    /// <summary>Reachable: RequiresHandoff=true from the Agent Runtime, from any stage.</summary>
    HandoffRequested,

    /// <summary>Not reachable yet - AgentDecision has no business-failure signal today.</summary>
    Failed,

    /// <summary>Reachable: customer explicitly asked to cancel/give up, from any stage
    /// except Completed/Cancelled.</summary>
    Cancelled
}
