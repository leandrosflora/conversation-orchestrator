using Microsoft.Extensions.Options;
using Npgsql;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Configuration;
using conversation_orchestrator.Domain;

namespace conversation_orchestrator.Adapters.Outbound.Persistence;

public sealed class PostgresMessageInboxStore(
    NpgsqlDataSource dataSource,
    IOptions<PostgresOptions> options) : IMessageInboxStore, IOutboxStore
{
    private static readonly Guid LegacyTenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private readonly int _leaseSeconds = Math.Max(30, options.Value.InboxLeaseSeconds);
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaReady;

    public async Task<InboxLease> TryAcquireAsync(
        Guid tenantId,
        string messageId,
        string conversationId,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const string acquireInboxSql = """
            INSERT INTO ops.message_inbox
                (tenant_id, message_id, conversation_id, status, lease_until, attempt_count,
                 source_received_at, received_at, updated_at)
            VALUES
                (@tenant_id, @message_id, @conversation_id, 'processing',
                 now() + make_interval(secs => @lease_seconds), 1, @source_received_at, now(), now())
            ON CONFLICT (tenant_id, message_id) DO UPDATE
            SET conversation_id = EXCLUDED.conversation_id,
                status = 'processing',
                lease_until = EXCLUDED.lease_until,
                attempt_count = ops.message_inbox.attempt_count + 1,
                source_received_at = EXCLUDED.source_received_at,
                last_error = NULL,
                completion_reason = NULL,
                updated_at = now()
            WHERE ops.message_inbox.status = 'failed'
               OR (ops.message_inbox.status = 'processing' AND ops.message_inbox.lease_until < now())
            RETURNING status;
            """;

        await using (var command = new NpgsqlCommand(acquireInboxSql, connection, transaction))
        {
            command.Parameters.AddWithValue("tenant_id", tenantId);
            command.Parameters.AddWithValue("message_id", messageId);
            command.Parameters.AddWithValue("conversation_id", conversationId);
            command.Parameters.AddWithValue("lease_seconds", _leaseSeconds);
            command.Parameters.AddWithValue("source_received_at", receivedAt);

            var acquiredStatus = await command.ExecuteScalarAsync(cancellationToken);
            if (acquiredStatus is null)
            {
                await using var statusCommand = new NpgsqlCommand(
                    "SELECT status FROM ops.message_inbox WHERE tenant_id = @tenant_id AND message_id = @message_id",
                    connection,
                    transaction);
                statusCommand.Parameters.AddWithValue("tenant_id", tenantId);
                statusCommand.Parameters.AddWithValue("message_id", messageId);
                var currentStatus = await statusCommand.ExecuteScalarAsync(cancellationToken) as string;
                await transaction.CommitAsync(cancellationToken);
                return currentStatus switch
                {
                    "completed" => new InboxLease(InboxAcquireResult.Completed),
                    "processing" => new InboxLease(InboxAcquireResult.InProgress),
                    "failed" => new InboxLease(InboxAcquireResult.InProgress),
                    _ => throw new InvalidOperationException($"Inbox row for message '{messageId}' could not be resolved.")
                };
            }
        }

        const string ensureStateSql = """
            INSERT INTO ops.conversation_state
                (tenant_id, conversation_id, journey_stage, version, created_at, updated_at)
            VALUES
                (@tenant_id, @conversation_id, @journey_stage, 0, now(), now())
            ON CONFLICT (tenant_id, conversation_id) DO NOTHING;
            """;
        await using (var ensureState = new NpgsqlCommand(ensureStateSql, connection, transaction))
        {
            ensureState.Parameters.AddWithValue("tenant_id", tenantId);
            ensureState.Parameters.AddWithValue("conversation_id", conversationId);
            ensureState.Parameters.AddWithValue("journey_stage", JourneyStage.Started.ToString());
            await ensureState.ExecuteNonQueryAsync(cancellationToken);
        }

        const string acquireConversationSql = """
            UPDATE ops.conversation_state
            SET processing_message_id = @message_id,
                processing_lease_until = now() + make_interval(secs => @lease_seconds),
                updated_at = now()
            WHERE tenant_id = @tenant_id
              AND conversation_id = @conversation_id
              AND (processing_lease_until IS NULL OR processing_lease_until < now())
            RETURNING journey_stage, last_intent, version, last_received_at, last_message_id;
            """;

        ConversationCheckpoint? checkpoint = null;
        await using (var acquireConversation = new NpgsqlCommand(acquireConversationSql, connection, transaction))
        {
            acquireConversation.Parameters.AddWithValue("tenant_id", tenantId);
            acquireConversation.Parameters.AddWithValue("conversation_id", conversationId);
            acquireConversation.Parameters.AddWithValue("message_id", messageId);
            acquireConversation.Parameters.AddWithValue("lease_seconds", _leaseSeconds);
            await using var reader = await acquireConversation.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var stageText = reader.GetString(0);
                checkpoint = new ConversationCheckpoint(
                    Enum.TryParse<JourneyStage>(stageText, true, out var stage) ? stage : JourneyStage.Started,
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetInt64(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4));
            }
        }

        if (checkpoint is null)
        {
            await MarkInboxFailedAsync(connection, transaction, tenantId, messageId, "conversation_busy", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new InboxLease(InboxAcquireResult.InProgress);
        }

        if (IsLate(receivedAt, messageId, checkpoint.LastReceivedAt, checkpoint.LastMessageId))
        {
            const string lateSql = """
                UPDATE ops.message_inbox
                SET status = 'completed', lease_until = NULL, completed_at = now(), updated_at = now(),
                    completion_reason = 'late_message', last_error = NULL
                WHERE tenant_id = @tenant_id AND message_id = @message_id;

                UPDATE ops.conversation_state
                SET processing_message_id = NULL, processing_lease_until = NULL, updated_at = now()
                WHERE tenant_id = @tenant_id AND conversation_id = @conversation_id
                  AND processing_message_id = @message_id;
                """;
            await using var late = new NpgsqlCommand(lateSql, connection, transaction);
            late.Parameters.AddWithValue("tenant_id", tenantId);
            late.Parameters.AddWithValue("message_id", messageId);
            late.Parameters.AddWithValue("conversation_id", conversationId);
            await late.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new InboxLease(InboxAcquireResult.Late, checkpoint);
        }

        await transaction.CommitAsync(cancellationToken);
        return new InboxLease(InboxAcquireResult.Acquired, checkpoint);
    }

    public async Task CompleteAsync(
        CompleteMessageCommand command,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var nextVersion = command.ExpectedVersion + 1;

        const string updateStateSql = """
            UPDATE ops.conversation_state
            SET journey_stage = @journey_stage,
                last_intent = @last_intent,
                version = version + 1,
                last_received_at = @received_at,
                last_message_id = @message_id,
                processing_message_id = NULL,
                processing_lease_until = NULL,
                updated_at = now()
            WHERE tenant_id = @tenant_id
              AND conversation_id = @conversation_id
              AND version = @expected_version
              AND processing_message_id = @message_id;
            """;
        await using (var updateState = new NpgsqlCommand(updateStateSql, connection, transaction))
        {
            updateState.Parameters.AddWithValue("tenant_id", command.TenantId);
            updateState.Parameters.AddWithValue("conversation_id", command.ConversationId);
            updateState.Parameters.AddWithValue("journey_stage", command.JourneyStage.ToString());
            updateState.Parameters.AddWithValue("last_intent", (object?)command.LastIntent ?? DBNull.Value);
            updateState.Parameters.AddWithValue("received_at", command.ReceivedAt);
            updateState.Parameters.AddWithValue("message_id", command.MessageId);
            updateState.Parameters.AddWithValue("expected_version", command.ExpectedVersion);
            var updated = await updateState.ExecuteNonQueryAsync(cancellationToken);
            if (updated != 1)
            {
                throw new InvalidOperationException("Conversation state version or lease changed before completion.");
            }
        }

        const string insertOutboxSql = """
            INSERT INTO ops.orchestrator_outbox
                (tenant_id, message_id, conversation_id, journey_version, effect_type,
                 idempotency_key, payload, status, attempt_count, next_attempt_at, created_at, updated_at)
            VALUES
                (@tenant_id, @message_id, @conversation_id, @journey_version, @effect_type,
                 @idempotency_key, @payload::jsonb, 'pending', 0, now(), now(), now())
            ON CONFLICT (tenant_id, idempotency_key) DO NOTHING;
            """;
        foreach (var effect in command.Effects)
        {
            await using var insertOutbox = new NpgsqlCommand(insertOutboxSql, connection, transaction);
            insertOutbox.Parameters.AddWithValue("tenant_id", command.TenantId);
            insertOutbox.Parameters.AddWithValue("message_id", command.MessageId);
            insertOutbox.Parameters.AddWithValue("conversation_id", command.ConversationId);
            insertOutbox.Parameters.AddWithValue("journey_version", nextVersion);
            insertOutbox.Parameters.AddWithValue("effect_type", effect.EffectType);
            insertOutbox.Parameters.AddWithValue("idempotency_key", effect.IdempotencyKey);
            insertOutbox.Parameters.AddWithValue("payload", effect.Payload);
            await insertOutbox.ExecuteNonQueryAsync(cancellationToken);
        }

        const string completeInboxSql = """
            UPDATE ops.message_inbox
            SET status = 'completed', lease_until = NULL, completed_at = now(), updated_at = now(),
                completion_reason = 'effects_persisted', last_error = NULL
            WHERE tenant_id = @tenant_id AND message_id = @message_id;
            """;
        await using (var completeInbox = new NpgsqlCommand(completeInboxSql, connection, transaction))
        {
            completeInbox.Parameters.AddWithValue("tenant_id", command.TenantId);
            completeInbox.Parameters.AddWithValue("message_id", command.MessageId);
            var updated = await completeInbox.ExecuteNonQueryAsync(cancellationToken);
            if (updated != 1)
            {
                throw new InvalidOperationException("Inbox row disappeared before transactional completion.");
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkFailedAsync(
        Guid tenantId,
        string messageId,
        string errorType,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await MarkInboxFailedAsync(connection, transaction, tenantId, messageId, errorType, cancellationToken);

        const string releaseConversationSql = """
            UPDATE ops.conversation_state state
            SET processing_message_id = NULL, processing_lease_until = NULL, updated_at = now()
            FROM ops.message_inbox inbox
            WHERE inbox.tenant_id = @tenant_id
              AND inbox.message_id = @message_id
              AND state.tenant_id = inbox.tenant_id
              AND state.conversation_id = inbox.conversation_id
              AND state.processing_message_id = @message_id;
            """;
        await using var release = new NpgsqlCommand(releaseConversationSql, connection, transaction);
        release.Parameters.AddWithValue("tenant_id", tenantId);
        release.Parameters.AddWithValue("message_id", messageId);
        await release.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OutboxEnvelope>> ClaimBatchAsync(
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        const string sql = """
            WITH candidates AS (
                SELECT candidate.outbox_id
                FROM ops.orchestrator_outbox candidate
                WHERE candidate.status IN ('pending', 'failed')
                  AND candidate.next_attempt_at <= now()
                  AND (candidate.locked_until IS NULL OR candidate.locked_until < now())
                  AND NOT EXISTS (
                      SELECT 1
                      FROM ops.orchestrator_outbox predecessor
                      WHERE predecessor.tenant_id = candidate.tenant_id
                        AND predecessor.conversation_id = candidate.conversation_id
                        AND predecessor.journey_version < candidate.journey_version
                        AND predecessor.status <> 'published'
                  )
                ORDER BY candidate.created_at, candidate.outbox_id
                FOR UPDATE SKIP LOCKED
                LIMIT @batch_size
            )
            UPDATE ops.orchestrator_outbox outbox
            SET status = 'publishing',
                locked_until = now() + make_interval(secs => @lease_seconds),
                attempt_count = attempt_count + 1,
                updated_at = now()
            FROM candidates
            WHERE outbox.outbox_id = candidates.outbox_id
            RETURNING outbox.outbox_id, outbox.tenant_id, outbox.message_id,
                      outbox.conversation_id, outbox.effect_type, outbox.idempotency_key,
                      outbox.payload::text, outbox.attempt_count;
            """;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("batch_size", Math.Clamp(batchSize, 1, 100));
        command.Parameters.AddWithValue("lease_seconds", Math.Max(10, (int)lease.TotalSeconds));
        var results = new List<OutboxEnvelope>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new OutboxEnvelope(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetInt32(7)));
        }
        await reader.DisposeAsync();
        await transaction.CommitAsync(cancellationToken);
        return results;
    }

    public async Task MarkPublishedAsync(Guid outboxId, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        const string sql = """
            UPDATE ops.orchestrator_outbox
            SET status = 'published', published_at = now(), locked_until = NULL,
                last_error = NULL, updated_at = now()
            WHERE outbox_id = @outbox_id;
            """;
        await ExecuteOutboxUpdateAsync(sql, outboxId, null, TimeSpan.Zero, cancellationToken);
    }

    public async Task MarkFailedAsync(
        Guid outboxId,
        string errorType,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        const string sql = """
            UPDATE ops.orchestrator_outbox
            SET status = 'failed', locked_until = NULL, last_error = @last_error,
                next_attempt_at = now() + make_interval(secs => @retry_seconds), updated_at = now()
            WHERE outbox_id = @outbox_id;
            """;
        await ExecuteOutboxUpdateAsync(sql, outboxId, errorType, retryDelay, cancellationToken);
    }

    private async Task ExecuteOutboxUpdateAsync(
        string sql,
        Guid outboxId,
        string? errorType,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("outbox_id", outboxId);
        if (errorType is not null)
        {
            command.Parameters.AddWithValue("last_error", Truncate(errorType));
            command.Parameters.AddWithValue("retry_seconds", Math.Max(1, (int)retryDelay.TotalSeconds));
        }
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkInboxFailedAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid tenantId,
        string messageId,
        string errorType,
        CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE ops.message_inbox
            SET status = 'failed', lease_until = NULL, updated_at = now(), last_error = @last_error
            WHERE tenant_id = @tenant_id AND message_id = @message_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("tenant_id", tenantId);
        command.Parameters.AddWithValue("message_id", messageId);
        command.Parameters.AddWithValue("last_error", Truncate(errorType));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static bool IsLate(
        DateTimeOffset receivedAt,
        string messageId,
        DateTimeOffset? lastReceivedAt,
        string? lastMessageId)
    {
        if (lastReceivedAt is null)
        {
            return false;
        }
        if (receivedAt < lastReceivedAt.Value)
        {
            return true;
        }
        return receivedAt == lastReceivedAt.Value
            && string.CompareOrdinal(messageId, lastMessageId ?? string.Empty) <= 0;
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (_schemaReady)
        {
            return;
        }
        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady)
            {
                return;
            }
            var legacyTenant = LegacyTenantId;
            const string sql = """
                CREATE EXTENSION IF NOT EXISTS pgcrypto;
                CREATE SCHEMA IF NOT EXISTS ops;

                CREATE TABLE IF NOT EXISTS ops.message_inbox (
                    tenant_id uuid,
                    message_id text NOT NULL,
                    conversation_id text NOT NULL,
                    status text NOT NULL CHECK (status IN ('processing', 'completed', 'failed')),
                    lease_until timestamptz,
                    attempt_count integer NOT NULL DEFAULT 0,
                    last_error text,
                    source_received_at timestamptz,
                    completion_reason text,
                    received_at timestamptz NOT NULL DEFAULT now(),
                    updated_at timestamptz NOT NULL DEFAULT now(),
                    completed_at timestamptz
                );

                ALTER TABLE ops.message_inbox ADD COLUMN IF NOT EXISTS tenant_id uuid;
                ALTER TABLE ops.message_inbox ADD COLUMN IF NOT EXISTS source_received_at timestamptz;
                ALTER TABLE ops.message_inbox ADD COLUMN IF NOT EXISTS completion_reason text;
                UPDATE ops.message_inbox SET tenant_id = @legacy_tenant WHERE tenant_id IS NULL;
                ALTER TABLE ops.message_inbox ALTER COLUMN tenant_id SET NOT NULL;
                ALTER TABLE ops.message_inbox DROP CONSTRAINT IF EXISTS message_inbox_pkey;
                CREATE UNIQUE INDEX IF NOT EXISTS ux_message_inbox_tenant_message
                    ON ops.message_inbox (tenant_id, message_id);
                CREATE INDEX IF NOT EXISTS idx_message_inbox_status_lease
                    ON ops.message_inbox (status, lease_until);

                CREATE TABLE IF NOT EXISTS ops.conversation_state (
                    tenant_id uuid NOT NULL,
                    conversation_id text NOT NULL,
                    journey_stage text NOT NULL,
                    last_intent text,
                    version bigint NOT NULL DEFAULT 0,
                    last_received_at timestamptz,
                    last_message_id text,
                    processing_message_id text,
                    processing_lease_until timestamptz,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    updated_at timestamptz NOT NULL DEFAULT now(),
                    PRIMARY KEY (tenant_id, conversation_id)
                );
                CREATE INDEX IF NOT EXISTS idx_conversation_state_processing_lease
                    ON ops.conversation_state (processing_lease_until);

                CREATE TABLE IF NOT EXISTS ops.orchestrator_outbox (
                    outbox_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
                    tenant_id uuid NOT NULL,
                    message_id text NOT NULL,
                    conversation_id text NOT NULL,
                    journey_version bigint NOT NULL DEFAULT 0,
                    effect_type text NOT NULL,
                    idempotency_key text NOT NULL,
                    payload jsonb NOT NULL,
                    status text NOT NULL CHECK (status IN ('pending', 'publishing', 'published', 'failed')),
                    attempt_count integer NOT NULL DEFAULT 0,
                    next_attempt_at timestamptz NOT NULL DEFAULT now(),
                    locked_until timestamptz,
                    last_error text,
                    created_at timestamptz NOT NULL DEFAULT now(),
                    updated_at timestamptz NOT NULL DEFAULT now(),
                    published_at timestamptz,
                    UNIQUE (tenant_id, idempotency_key)
                );
                ALTER TABLE ops.orchestrator_outbox
                    ADD COLUMN IF NOT EXISTS journey_version bigint NOT NULL DEFAULT 0;
                CREATE INDEX IF NOT EXISTS idx_orchestrator_outbox_dispatch
                    ON ops.orchestrator_outbox (status, next_attempt_at, locked_until, created_at);
                CREATE INDEX IF NOT EXISTS idx_orchestrator_outbox_conversation_version
                    ON ops.orchestrator_outbox (tenant_id, conversation_id, journey_version, status);
                """;
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("legacy_tenant", legacyTenant);
            await command.ExecuteNonQueryAsync(cancellationToken);
            _schemaReady = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private static string Truncate(string value) => value.Length <= 500 ? value : value[..500];
}
