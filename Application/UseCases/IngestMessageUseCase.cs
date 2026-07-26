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

    /// <summary>Reserved state for a tenant with 2+ assigned skills and no skill pinned yet -
    /// the flow-selection menu was just sent (or re-sent) and the orchestrator is waiting for a
    /// button tap. See agent-skill-registry.</summary>
    public const string AwaitingSkillSelectionState = "AwaitingSkillSelection";

    private const string SkillMenuBodyText =
        "Como posso te ajudar hoje? Escolha uma das opções abaixo:";

    /// <summary>A conversation's session is capped at 15 minutes from its own start, not an
    /// inactivity timeout - see journey-state-machine's session-window requirement. Owned by the
    /// orchestrator itself (like HandoffRequestedState above) since it's about conversation
    /// lifecycle, not any skill's own vocabulary.</summary>
    private static readonly TimeSpan SessionDuration = TimeSpan.FromMinutes(15);

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
            var now = DateTimeOffset.UtcNow;
            var previousState = checkpoint.State;
            // Anchored to the session's own start, not the last message - a customer who keeps
            // messaging every few minutes still gets reset at 15 minutes total, same as one who
            // goes quiet. LastReceivedAt is only a fallback for a checkpoint that predates this
            // column (backfilled to created_at at migration time, but defensive here too).
            var sessionStartedAt = checkpoint.SessionStartedAt ?? checkpoint.LastReceivedAt ?? now;
            var sessionExpired = now - sessionStartedAt > SessionDuration;
            var nextSessionStartedAt = sessionExpired ? now : sessionStartedAt;
            if (sessionExpired)
            {
                metrics.Increment("orchestrator_session_resets_total");
            }

            // TTL expiry also un-pins the skill, not just State/StructuredState - a session past
            // its 15-minute window restarts at skill selection too, same as a brand-new
            // conversation, rather than silently resuming whatever skill the expired session had
            // pinned. No-op for a single-skill tenant, which just auto-pins the same skill again.
            var pinnedSkillId = sessionExpired ? null : checkpoint.SkillId;
            var tenantSkillIds = agentSkillRegistry.ResolveTenantSkills(tenantContext.TenantId);

            string? skillId = pinnedSkillId;
            var showMenu = false;
            if (skillId is null)
            {
                if (tenantSkillIds.Count == 1)
                {
                    skillId = tenantSkillIds[0];
                }
                else if (tenantSkillIds.Count > 1)
                {
                    skillId = message.Interactive?.Id is { } buttonId
                        ? agentSkillRegistry.ResolveSkillIdBySelectionButton(tenantSkillIds, buttonId)
                        : null;
                    // A tenant with 2+ skills and nothing pinned yet needs an explicit choice
                    // before any agent is called - free text here (ignoring the menu's buttons)
                    // re-sends the same menu rather than guessing which skill was meant.
                    showMenu = skillId is null;
                }
            }

            var agentClient = (!showMenu && skillId is not null)
                ? agentSkillRegistry.Resolve(skillId)
                : null;

            AgentRuntimeResult result;
            var resetToSkillSelection = showMenu;
            // Not disposed: this is a short-lived, per-request value whose lifetime needs to
            // outlast this method (the real AgentRuntimeClient serializes it during the awaited
            // HTTP call below, but a caller inspecting the request object afterwards - as tests
            // legitimately do - would otherwise see it torn down first). JsonDocument.Dispose()
            // only returns pooled buffers early; skipping it just defers that to the GC.
            var priorStructuredState = checkpoint.StructuredState is not null
                ? JsonDocument.Parse(checkpoint.StructuredState)
                : null;

            if (showMenu)
            {
                result = BuildSkillMenuResult(agentSkillRegistry.GetSkillEntries(tenantSkillIds));
            }
            else if (agentClient is null)
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
                        // HandoffRequested and AwaitingSkillSelection are the two state values
                        // this orchestrator owns itself (see journey-state-machine /
                        // agent-skill-registry) - neither means anything to the skill's own
                        // vocabulary. Confirmed live for HandoffRequested: it left the agent with
                        // no reason to attempt any tool (every governed tool is stage-denied from
                        // it, so it just gave up). AwaitingSkillSelection hits the exact same
                        // failure live too: once a customer picks a skill from the menu, this
                        // reserved value would otherwise keep being echoed back as that skill's
                        // own journey_stage on every turn (see nextState below - the agent
                        // reporting no fresh State of its own doesn't clear it), which its
                        // downstream tool-service stage policy doesn't recognize and denies every
                        // governed tool call from - a customer who just picked "renegotiation"
                        // could never get past giving their CPF, since consultar_cliente was
                        // denied every single turn. Since we're calling the (possibly
                        // newly-resolved) agent anyway, give it a clean slate instead of a state
                        // it can't act on, so it has a real chance to make progress this turn.
                        // A session past its 15-minute window gets the same clean slate, for the
                        // same reason: whatever state/identity it resolved belongs to a session
                        // that's now over.
                        State = (previousState == HandoffRequestedState
                            || previousState == AwaitingSkillSelectionState
                            || sessionExpired) ? null : previousState,
                        JourneyVersion = checkpoint.Version,
                        LastIntent = checkpoint.LastIntent,
                        StructuredState = sessionExpired ? null : priorStructuredState,
                        SessionReset = sessionExpired,
                        SessionStartedAt = nextSessionStartedAt
                    },
                    cancellationToken);

                if (result.OutOfScope)
                {
                    if (tenantSkillIds.Count > 1)
                    {
                        // The resolved skill judged this message outside its own domain and
                        // another skill is available - discard progress (State/StructuredState)
                        // and un-pin so the customer picks again. See agent-skill-registry.
                        result = BuildSkillMenuResult(agentSkillRegistry.GetSkillEntries(tenantSkillIds));
                        skillId = null;
                        resetToSkillSelection = true;
                    }
                    else
                    {
                        // No alternative skill to route to - out-of-scope has nowhere useful to
                        // go, so treat it like any other agent-can't-help case.
                        result = new AgentRuntimeResult
                        {
                            RequiresHandoff = true,
                            HandoffReason = AgentRuntimeResult.OutOfScopeNoAlternativeSkillReason
                        };
                    }
                }
            }

            metrics.Increment(
                "orchestrator_agent_decisions_total",
                ("outcome", ClassifyAgentOutcome(result)));

            var nextIntent = result.Intent ?? checkpoint.LastIntent;
            // The resolved agent already echoes back the previous StructuredState when it has
            // nothing new to report - this fallback only matters for a synthetic/unavailable
            // result, or a session-reset turn where the agent made no progress of its own (e.g.
            // it just asked for the CPF again), neither of which has a legitimate prior
            // StructuredState to fall back to. A skill-selection reset always discards it, per
            // the same explicit "discard progress on switch" decision as OutOfScope above.
            var nextStructuredState = resetToSkillSelection
                ? null
                : (result.StructuredState is not null
                    ? result.StructuredState.RootElement.GetRawText()
                    : (sessionExpired ? null : checkpoint.StructuredState));
            var nextSkillId = resetToSkillSelection ? null : (skillId ?? checkpoint.SkillId);
            // Same reasoning as StructuredState above: a session-reset turn where the agent
            // reports no new State of its own must land on a real clean slate (Started), not
            // fall back to whatever stage the *expired* session had reached. A skill-selection
            // reset always lands on the reserved AwaitingSkillSelection state instead, regardless
            // of what State the agent itself reported this turn. Falling back to Started rather
            // than previousState also applies whenever previousState was itself
            // AwaitingSkillSelection (the turn a skill just got resolved from the menu, or any
            // turn its own agent reports no fresh State) - otherwise the reserved value would
            // persist turn after turn, later getting echoed straight back to that skill as its own
            // journey_stage above, which is exactly the live bug this fixes (see the State
            // assignment's comment).
            var nextState = result.RequiresHandoff
                ? HandoffRequestedState
                : (resetToSkillSelection
                    ? AwaitingSkillSelectionState
                    : (result.State ?? (
                        (sessionExpired || previousState == AwaitingSkillSelectionState)
                            ? ConversationCheckpoint.StartedState
                            : previousState)));

            if (!result.RequiresHandoff && nextState != previousState)
            {
                metrics.Increment(
                    "orchestrator_journey_transitions_total",
                    ("from", previousState),
                    ("to", nextState),
                    ("outcome", "applied"));
            }

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
                    nextStructuredState,
                    nextSessionStartedAt),
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

    /// <summary>Synthesizes an orchestrator-owned "show the flow-selection menu" result - no
    /// agent is called for this turn. See agent-skill-registry.</summary>
    private static AgentRuntimeResult BuildSkillMenuResult(IReadOnlyList<Configuration.AgentSkillEntry> entries) =>
        new()
        {
            ReplyText = SkillMenuBodyText,
            RequiresHandoff = false,
            MenuOptions = entries
                .Where(e => e.SelectionButtonId is not null && e.SelectionButtonTitle is not null)
                .Select(e => new MenuOption(e.SelectionButtonId!, e.SelectionButtonTitle!))
                .ToList()
        };

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
        if (result.MenuOptions is { Count: > 0 } menuOptions)
        {
            effects.Add(DurableEffectFactory.Create(
                OutboxEffectTypes.ChannelMenu,
                $"menu:{keyPrefix}",
                new ChannelMenuEffect(conversationId, result.ReplyText ?? string.Empty, menuOptions)));
            effects.Add(DurableEffectFactory.Create(
                OutboxEffectTypes.MemoryAppendMessage,
                $"memory-assistant:{keyPrefix}",
                new MemoryAppendMessageEffect(
                    conversationId,
                    "assistant",
                    result.ReplyText ?? string.Empty,
                    null)));
        }
        else if (!string.IsNullOrWhiteSpace(result.ReplyText))
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
