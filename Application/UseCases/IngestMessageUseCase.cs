using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using conversation_orchestrator.Application.Outbox;
using conversation_orchestrator.Application.Ports.Inbound;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Domain;
using conversation_orchestrator.Platform;

namespace conversation_orchestrator.Application.UseCases;

public class IngestMessageUseCase(
    IMessageInboxStore inboxStore,
    IAgentRuntimeClient agentRuntimeClient,
    TenantContext tenantContext,
    PlatformMetrics metrics,
    ILogger<IngestMessageUseCase> logger) : IIngestMessageUseCase
{
    public async Task<IngestMessageResult> ExecuteAsync(
        InboundChannelMessage message,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var messageId = message.MessageId!;
        var conversationId = message.ConversationId!;
        var tenantId = Guid.Parse(tenantContext.TenantId);

        var lease = await inboxStore.TryAcquireAsync(
            tenantId,
            messageId,
            conversationId,
            message.ReceivedAt,
            cancellationToken);

        if (lease.Result == InboxAcquireResult.Completed)
        {
            metrics.Increment("orchestrator_inbox_duplicates_total", ("state", "completed"));
            return IngestMessageResult.AlreadyCompleted;
        }

        if (lease.Result == InboxAcquireResult.Late)
        {
            metrics.Increment("orchestrator_late_messages_total");
            logger.LogWarning(
                "Ignored late message {MessageId} for tenant {TenantId} conversation {ConversationId}",
                messageId,
                tenantId,
                conversationId);
            return IngestMessageResult.AlreadyCompleted;
        }

        if (lease.Result == InboxAcquireResult.InProgress || lease.Checkpoint is null)
        {
            metrics.Increment("orchestrator_inbox_duplicates_total", ("state", "processing"));
            return IngestMessageResult.InProgress;
        }

        var checkpoint = lease.Checkpoint;
        metrics.Increment("orchestrator_inbox_acquisitions_total", ("outcome", "acquired"));

        try
        {
            var previousStage = checkpoint.JourneyStage;
            var confirmationMessageId = ExplicitConfirmationDetector.IsExplicitConfirmation(message, previousStage)
                ? messageId
                : null;

            var result = await agentRuntimeClient.ProcessAsync(
                new AgentRuntimeRequest
                {
                    TenantId = tenantId.ToString("D"),
                    ConversationId = conversationId,
                    MessageId = messageId,
                    MessageType = message.Type.ToString(),
                    Text = message.Text ?? message.Interactive?.Title,
                    JourneyStage = previousStage.ToString(),
                    JourneyVersion = checkpoint.Version,
                    LastIntent = checkpoint.LastIntent,
                    ExplicitConfirmationMessageId = confirmationMessageId,
                    ActiveContractId = checkpoint.ActiveContractId,
                    ActiveSimulationId = checkpoint.ActiveSimulationId,
                    ActiveAgreementId = checkpoint.ActiveAgreementId
                },
                cancellationToken);

            metrics.Increment(
                "orchestrator_agent_decisions_total",
                ("outcome", ClassifyAgentOutcome(result)));

            var nextIntent = result.Intent ?? checkpoint.LastIntent;
            // The Agent Runtime already echoes back the previous value when it has nothing new
            // to report (see agent-runtime-renegotiation's core.py) - this fallback only matters
            // for the AgentRuntimeResult.Unavailable() path, which has no way to know what the
            // checkpoint held.
            var nextActiveContractId = result.ActiveContractId ?? checkpoint.ActiveContractId;
            var nextActiveSimulationId = result.ActiveSimulationId ?? checkpoint.ActiveSimulationId;
            var nextActiveAgreementId = result.ActiveAgreementId ?? checkpoint.ActiveAgreementId;
            var nextStage = previousStage;
            if (result.RequiresHandoff)
            {
                nextStage = JourneyStage.HandoffRequested;
            }
            else
            {
                // JourneyMilestone is computed by agent-runtime-renegotiation from verified
                // governed-tool outcomes (see journey-milestone-reporting), not from freeform
                // Intent text - prefer it whenever present and it doesn't regress the stage.
                // "Legal" here is deliberately just forward-or-equal in the enum's declaration
                // order, not a (from, trigger) transition-table lookup: a milestone can validly
                // jump several stages in one turn (e.g. Started -> ContractSelected when a
                // single-contract customer identifies themselves and their contract in the same
                // message), which the older trigger table - built for one hop per turn - doesn't
                // model.
                JourneyStage? milestoneStage = null;
                if (!string.IsNullOrWhiteSpace(result.JourneyMilestone)
                    && Enum.TryParse<JourneyStage>(result.JourneyMilestone, true, out var parsedMilestone)
                    && (int)parsedMilestone >= (int)previousStage)
                {
                    milestoneStage = parsedMilestone;
                }

                if (milestoneStage is not null)
                {
                    nextStage = milestoneStage.Value;
                    metrics.Increment(
                        "orchestrator_journey_transitions_total",
                        ("from", previousStage.ToString()),
                        ("to", nextStage.ToString()),
                        ("outcome", "applied_milestone"));
                }
                else if (ProposalSelectionDetector.IsProposalSelection(message, previousStage))
                {
                    // Reads the customer's own raw text, not the Agent Runtime's Intent label -
                    // see ProposalSelectionDetector's doc comment for why the Intent-based path
                    // doesn't reliably catch this hop.
                    nextStage = JourneyStage.ProposalSelected;
                    metrics.Increment(
                        "orchestrator_journey_transitions_total",
                        ("from", previousStage.ToString()),
                        ("to", nextStage.ToString()),
                        ("outcome", "applied_customer_text"));
                }
                else
                {
                    var trigger = JourneyTriggerClassifier.Classify(result.Intent);
                    if (JourneyStageTransitions.TryGetNext(previousStage, trigger, out var transitionedStage))
                    {
                        nextStage = transitionedStage;
                        metrics.Increment(
                            "orchestrator_journey_transitions_total",
                            ("from", previousStage.ToString()),
                            ("to", nextStage.ToString()),
                            ("outcome", "applied"));
                    }
                    else if (trigger != JourneyTrigger.None)
                    {
                        metrics.Increment(
                            "orchestrator_journey_transitions_total",
                            ("from", previousStage.ToString()),
                            ("to", previousStage.ToString()),
                            ("outcome", "rejected"));
                        metrics.Increment(
                            "orchestrator_journey_triggers_total",
                            ("trigger", trigger.ToString()),
                            ("outcome", "rejected"));
                        logger.LogInformation(
                            "Rejected journey trigger {Trigger} from stage {Stage} for conversation {ConversationId}",
                            trigger,
                            previousStage,
                            conversationId);
                    }
                }

                // JourneyTriggerClassifier is keyword-based on the model's freeform Intent, which
                // doesn't always name a trigger matching what actually happened (e.g. Intent
                // comes back as a tool name like "consultar_debitos" rather than something
                // classifying as ProvidedIdentification/SelectedContract) - confirmed live: a
                // real conversation got permanently stuck at IdentificationPending this way, with
                // ActiveContractId already populated proving identification and contract lookup
                // had genuinely succeeded. ActiveContractId newly appearing is unambiguous
                // structural proof of that progress, independent of how the model phrased its
                // intent - use it as a fallback so the stage doesn't wait forever for a keyword
                // match that may never come.
                if (nextStage == previousStage
                    && nextActiveContractId is not null
                    && checkpoint.ActiveContractId is null
                    && previousStage is JourneyStage.Started or JourneyStage.IdentificationPending or JourneyStage.CustomerIdentified)
                {
                    nextStage = JourneyStage.ContractSelected;
                    metrics.Increment(
                        "orchestrator_journey_transitions_total",
                        ("from", previousStage.ToString()),
                        ("to", nextStage.ToString()),
                        ("outcome", "applied_structural_fallback"));
                }
            }

            var now = DateTimeOffset.UtcNow;
            var outcome = result.RequiresHandoff ? "handoff" : "processed";
            var effects = BuildDurableEffects(
                tenantId,
                message,
                checkpoint,
                previousStage,
                nextStage,
                nextIntent,
                result,
                outcome,
                now);

            await inboxStore.CompleteAsync(
                new CompleteMessageCommand(
                    tenantId,
                    messageId,
                    conversationId,
                    message.ReceivedAt,
                    nextStage,
                    nextIntent,
                    checkpoint.Version,
                    effects,
                    nextActiveContractId,
                    nextActiveSimulationId,
                    nextActiveAgreementId),
                cancellationToken);

            metrics.Increment("orchestrator_journey_outcomes_total", ("outcome", outcome));
            metrics.Increment("orchestrator_outbox_effects_persisted_total", ("outcome", outcome));
            logger.LogInformation(
                "Persisted message {MessageId} tenant {TenantId} conversation {ConversationId} at journey version {JourneyVersion} with {EffectCount} durable effects",
                messageId,
                tenantId,
                conversationId,
                checkpoint.Version + 1,
                effects.Count);
            return IngestMessageResult.Accepted;
        }
        catch (Exception ex)
        {
            metrics.Increment(
                "orchestrator_processing_failures_total",
                ("exception", ex.GetType().Name));
            logger.LogError(
                ex,
                "Message {MessageId} tenant {TenantId} conversation {ConversationId} failed before transactional completion",
                messageId,
                tenantId,
                conversationId);
            await MarkFailedBestEffortAsync(tenantId, messageId, ex.GetType().Name);
            throw;
        }
        finally
        {
            stopwatch.Stop();
            metrics.Observe(
                "orchestrator_message_processing_duration_seconds",
                stopwatch.Elapsed.TotalSeconds);
        }
    }

    private static List<DurableEffect> BuildDurableEffects(
        Guid tenantId,
        InboundChannelMessage message,
        ConversationCheckpoint checkpoint,
        JourneyStage previousStage,
        JourneyStage nextStage,
        string? nextIntent,
        AgentRuntimeResult result,
        string outcome,
        DateTimeOffset now)
    {
        var messageId = message.MessageId!;
        var conversationId = message.ConversationId!;
        var keyPrefix = $"{tenantId:D}:{messageId}";
        var effects = new List<DurableEffect>
        {
            DurableEffectFactory.Create(
                OutboxEffectTypes.MemoryAppendMessage,
                $"memory-user:{keyPrefix}",
                new MemoryAppendMessageEffect(
                    conversationId,
                    "user",
                    message.Text ?? message.Interactive?.Title ?? string.Empty,
                    messageId)),
            DurableEffectFactory.Create(
                OutboxEffectTypes.MemorySaveSession,
                $"memory-session:{keyPrefix}",
                new MemorySaveSessionEffect(
                    conversationId,
                    checkpoint.LastReceivedAt ?? message.ReceivedAt,
                    message.ReceivedAt,
                    nextStage.ToString(),
                    nextIntent)),
            DurableEffectFactory.Create(
                OutboxEffectTypes.AuditRecord,
                $"audit:{keyPrefix}",
                new AuditRecordEffect(
                    conversationId,
                    result.Intent,
                    outcome,
                    now))
        };

        if (!string.IsNullOrWhiteSpace(result.Intent))
        {
            effects.Add(DurableEffectFactory.Create(
                OutboxEffectTypes.IntentDetected,
                $"intent:{keyPrefix}",
                new IntentDetectedEffect(
                    conversationId,
                    result.Intent,
                    result.Confidence,
                    now)));
        }

        if (nextStage != previousStage)
        {
            effects.Add(DurableEffectFactory.Create(
                OutboxEffectTypes.StateChanged,
                $"state:{keyPrefix}",
                new StateChangedEffect(
                    conversationId,
                    previousStage.ToString(),
                    nextStage.ToString(),
                    now)));
        }

        if (result.RequiresHandoff)
        {
            effects.Add(DurableEffectFactory.Create(
                OutboxEffectTypes.HandoffRequest,
                $"handoff:{keyPrefix}",
                new HandoffRequestEffect(
                    conversationId,
                    result.HandoffReason ?? "unspecified")));
        }

        // Independent of RequiresHandoff: the agent may hand off *and* still have produced a
        // reply (e.g. "vou transferir você para um atendente"). Dropping that text left the
        // customer with total silence on every handoff, even though the agent had something to
        // say - see docs/validation/2026-07-23-renegotiation-scenario-homologation.md.
        if (!string.IsNullOrWhiteSpace(result.ReplyText))
        {
            effects.Add(DurableEffectFactory.Create(
                OutboxEffectTypes.ChannelReply,
                $"reply:{keyPrefix}",
                new ChannelReplyEffect(conversationId, result.ReplyText)));
            effects.Add(DurableEffectFactory.Create(
                OutboxEffectTypes.MemoryAppendMessage,
                $"memory-assistant:{keyPrefix}",
                new MemoryAppendMessageEffect(
                    conversationId,
                    "assistant",
                    result.ReplyText,
                    null)));
        }

        return effects;
    }

    private async Task MarkFailedBestEffortAsync(Guid tenantId, string messageId, string errorType)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await inboxStore.MarkFailedAsync(tenantId, messageId, errorType, cts.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark Inbox message {MessageId} as failed", messageId);
        }
    }

    private static string ClassifyAgentOutcome(AgentRuntimeResult result)
    {
        if (result.HandoffReason == AgentRuntimeResult.AgentRuntimeUnavailableReason)
        {
            return "unavailable";
        }
        return result.RequiresHandoff ? "handoff" : "automatic";
    }
}

internal static class PortugueseTextNormalizer
{
    public static string RemoveDiacritics(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}

internal static class ExplicitConfirmationDetector
{
    private static readonly Regex PositiveConfirmation = new(
        @"\b(confirmo|aceito|pode confirmar|pode fechar|fechar acordo|quero fechar|sim confirmo|sim aceito)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsExplicitConfirmation(InboundChannelMessage message, JourneyStage stage)
    {
        if (stage is not (JourneyStage.ProposalSelected or JourneyStage.ConfirmationPending))
        {
            return false;
        }

        var value = message.Interactive?.Title ?? message.Text;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = PortugueseTextNormalizer.RemoveDiacritics(value).ToLowerInvariant();
        if (Regex.IsMatch(normalized, @"\b(nao|nunca|cancel|desist)\b"))
        {
            return false;
        }
        return PositiveConfirmation.IsMatch(normalized);
    }
}

/// <summary>
/// Design decision 5 (stabilize-renegotiation-journey-progression): ProposalAvailable ->
/// ProposalSelected is fundamentally about what the *customer* decided, not a governed-tool
/// outcome - there's no MCP tool call for "customer picked this proposal". JourneyTriggerClassifier
/// classifies the Agent Runtime's own Intent, not the customer's words - confirmed live this
/// mismatches: a customer message "Aceito essa proposta" produced Intent
/// "confirm_agreement_request", which classifies as ConfirmedAgreement (needs ProposalSelected
/// already) rather than SelectedProposal, so the transition table found no legal entry from
/// ProposalAvailable and the stage never moved. Reads the customer's own raw message instead,
/// mirroring ExplicitConfirmationDetector.
/// </summary>
internal static class ProposalSelectionDetector
{
    private static readonly Regex PositiveSelection = new(
        @"\b(aceito|aceita|escolho|essa mesma|essa proposta|fechar essa|quero essa|pode ser essa|gostei dessa)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsProposalSelection(InboundChannelMessage message, JourneyStage stage)
    {
        if (stage != JourneyStage.ProposalAvailable)
        {
            return false;
        }

        var value = message.Interactive?.Title ?? message.Text;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = PortugueseTextNormalizer.RemoveDiacritics(value).ToLowerInvariant();
        if (Regex.IsMatch(normalized, @"\b(nao|nunca|cancel|desist)\b"))
        {
            return false;
        }
        return PositiveSelection.IsMatch(normalized);
    }
}
