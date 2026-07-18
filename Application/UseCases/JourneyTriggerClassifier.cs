namespace conversation_orchestrator.Application.UseCases;

/// <summary>
/// Classifies the Agent Runtime's freeform Intent string into a recognized JourneyTrigger.
/// Keyword-based, not exact-match: agent-runtime-renegotiation's AgentDecision.intent has no
/// constrained vocabulary (confirmed against app/agent/prompts.py), and real runs this
/// session produced inconsistent labels for similar meanings ("iniciar_renegociacao" vs
/// "renegotiation_request"). This is a best-effort approximation, not a guarantee - see
/// design.md's Open Questions for the natural follow-up (constrain the Agent Runtime's
/// intent vocabulary instead).
/// </summary>
public static class JourneyTriggerClassifier
{
    public static JourneyTrigger Classify(string? intent)
    {
        if (string.IsNullOrWhiteSpace(intent))
        {
            return JourneyTrigger.None;
        }

        var normalized = intent.ToLowerInvariant();

        if (normalized.Contains("cancel") || normalized.Contains("desist"))
        {
            return JourneyTrigger.RequestedCancellation;
        }

        if (normalized.Contains("confirm"))
        {
            return JourneyTrigger.ConfirmedAgreement;
        }

        if (normalized.Contains("aceit") || normalized.Contains("escolh"))
        {
            return JourneyTrigger.SelectedProposal;
        }

        if (normalized.Contains("proposta") || normalized.Contains("proposal"))
        {
            return JourneyTrigger.ProposalPresented;
        }

        if (normalized.Contains("simul"))
        {
            return JourneyTrigger.RequestedSimulation;
        }

        if (normalized.Contains("elegib"))
        {
            return JourneyTrigger.EligibilityConfirmed;
        }

        if (normalized.Contains("contrat"))
        {
            return JourneyTrigger.SelectedContract;
        }

        if (normalized.Contains("identific") || normalized.Contains("cpf"))
        {
            return JourneyTrigger.ProvidedIdentification;
        }

        if (normalized.Contains("renegoc"))
        {
            return JourneyTrigger.RequestedRenegotiation;
        }

        return JourneyTrigger.None;
    }
}
