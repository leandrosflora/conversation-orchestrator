using System.Diagnostics;
using System.Text.Json;
using conversation_orchestrator.Application.Outbox;
using conversation_orchestrator.Application.Ports.Inbound;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Domain;
using conversation_orchestrator.Platform;

namespace conversation_orchestrator.Application.UseCases;

/// <summary>
/// Skill-agnostic: this use case has no compiled knowledge of any business domain's journey
/// stages, triggers, or customer-text vocabulary. It resolves which skill's agent handles a
/// conversation (agent-skill-registry), forwards the message, persists whatever opaque
/// State/StructuredState that agent reports, and dispatches the same generic effects (reply,
/// memory, audit, handoff) regardless of which skill produced them. See journey-state-machine
/// for why: State's ordering/legality is no longer something the orchestrator can validate once
/// it's skill-defined, so it trusts the resolved agent's verified-evidence-based reporting
/// instead (the same standard agent-runtime-renegotiation already meets for its own State).
/// </summary>
public class IngestMessageUseCase(
    IMessageInboxStore inboxStore,
    IAgentSkillRegistry agentSkillRegistry,
    TenantContext tenantContext,
    PlatformMetrics metrics,
    ILogger<IngestMessageUseCase> logger) : IIngestMessageUseCase
{
    /// <summary>The one state value the orchestrator owns itself, regardless of skill - see
    /// journey-state-machine's "Handoff always transitions to the reserved HandoffRequested
    /// state" requirement.</summary>
    public const string HandoffRequestedState = "HandoffRequested";

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
            var previousState = checkpoint.State;
            var skillId = checkpoint.SkillId ?? agentSkillRegistry.ResolveTenantSkill(tenantContext.TenantId);
            var agentClient = skillId is not null ? agentSkillRegistry.Resolve(skillId) : null;

            AgentRuntimeResult result;
            // Not disposed: this is a short-lived, per-request value whose lifetime needs to
            // outlast this method (the real AgentRuntimeClient serializes it during the awaited
            // HTTP call below, but a caller inspecting the request object afterwards - as tests
            // legitimately do - would otherwise see it torn down first). JsonDocument.Dispose()
            // only returns pooled buffers early; skipping it just defers that to the GC.
            var priorStructuredState = checkpoint.StructuredState is not null
                ? JsonDocument.Parse(checkpoint.StructuredState)
                : null;

            if (agentClient is null)
            {
                // No skill assigned to this tenant, or the assigned/pinned skill id isn't (or is
                // no longer) configured - can't call an agent that doesn't exist. Treat exactly
                // like an unreachable Agent Runtime: require handoff, don't crash.
                result = AgentRuntimeResult.SkillNotConfigured();
                if (skillId is null)
                {
                    logger.LogWarning(
                        "No skill assigned for tenant {TenantId}, conversation {ConversationId}",
                        tenantId,
                        conversationId);
                }
                else
                {
                    logger.LogWarning(
                        "Skill {SkillId} is not configured, conversation {ConversationId}",
                        skillId,
                        conversationId);
                }
            }
            else
            {
                result = await agentClient.ProcessAsync(
                    new AgentRuntimeRequest
                    {
                        TenantId = tenantId.ToString("D"),
                        ConversationId = conversationId,
                        MessageId = messageId,
                        MessageType = message.Type.ToString(),
                        Text = message.Text ?? message.Interactive?.Title,
                        // HandoffRequested is the one state this orchestrator owns itself (see
                        // journey-state-machine) - it means nothing to the skill's own vocabulary,
                        // and confirmed live it left the agent with no reason to attempt any tool
                        // (every governed tool is stage-denied from it, so it just gave up). Since
                        // we're calling the agent again anyway, that already means we're routing
                        // back to it rather than a human - give it a clean slate instead of a state
                        // it can't act on, so it has a real chance to make progress this turn.
                        State = previousState == HandoffRequestedState ? null : previousState,
                        JourneyVersion = checkpoint.Version,
                        LastIntent = checkpoint.LastIntent,
                        StructuredState = priorStructuredState
                    },
                    cancellationToken);
            }

            metrics.Increment(
                "orchestrator_agent_decisions_total",
                ("outcome", ClassifyAgentOutcome(result)));

            var nextIntent = result.Intent ?? checkpoint.LastIntent;
            // The resolved agent already echoes back the previous StructuredState when it has
            // nothing new to report - this fallback only matters for a synthetic/unavailable
            // result, which has no way to know what the checkpoint held.
            var nextStructuredState = result.StructuredState is not null
                ? result.StructuredState.RootElement.GetRawText()
                : checkpoint.StructuredState;
            var nextSkillId = skillId ?? checkpoint.SkillId;
            var nextState = result.RequiresHandoff
                ? HandoffRequestedState
                : (result.State ?? previousState);

            if (!result.RequiresHandoff && nextState != previousState)
            {
                metrics.Increment(
                    "orchestrator_journey_transitions_total",
                    ("from", previousState),
                    ("to", nextState),
                    ("outcome", "applied"));
            }

            var now = DateTimeOffset.UtcNow;
            var outcome = result.RequiresHandoff ? "handoff" : "processed";
            var effects = BuildDurableEffects(
                tenantId,
                message,
                checkpoint,
                previousState,
                nextState,
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
                    nextState,
                    nextIntent,
                    checkpoint.Version,
                    effects,
                    nextSkillId,
                    nextStructuredState),
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
        string previousState,
        string nextState,
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
                    nextState,
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

        if (nextState != previousState)
        {
            effects.Add(DurableEffectFactory.Create(
                OutboxEffectTypes.StateChanged,
                $"state:{keyPrefix}",
                new StateChangedEffect(
                    conversationId,
                    previousState,
                    nextState,
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
        if (result.HandoffReason == AgentRuntimeResult.AgentRuntimeUnavailableReason
            || result.HandoffReason == AgentRuntimeResult.SkillNotConfiguredReason)
        {
            return "unavailable";
        }
        return result.RequiresHandoff ? "handoff" : "automatic";
    }
}
