using Microsoft.Extensions.Options;
using Npgsql;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Configuration;

namespace conversation_orchestrator.Adapters.Outbound.Persistence;

public sealed class PostgresMessageInboxStore(
    NpgsqlDataSource dataSource,
    IOptions<PostgresOptions> options) : IMessageInboxStore
{
    private readonly int _leaseSeconds = Math.Max(30, options.Value.InboxLeaseSeconds);
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _schemaReady;

    public async Task<InboxAcquireResult> TryAcquireAsync(
        string messageId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        const string acquireSql = """
            INSERT INTO ops.message_inbox
                (message_id, conversation_id, status, lease_until, attempt_count, received_at, updated_at)
            VALUES
                (@message_id, @conversation_id, 'processing', now() + make_interval(secs => @lease_seconds), 1, now(), now())
            ON CONFLICT (message_id) DO UPDATE
            SET conversation_id = EXCLUDED.conversation_id,
                status = 'processing',
                lease_until = EXCLUDED.lease_until,
                attempt_count = ops.message_inbox.attempt_count + 1,
                last_error = NULL,
                updated_at = now()
            WHERE ops.message_inbox.status = 'failed'
               OR (ops.message_inbox.status = 'processing' AND ops.message_inbox.lease_until < now())
            RETURNING status;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand(acquireSql, connection))
        {
            command.Parameters.AddWithValue("message_id", messageId);
            command.Parameters.AddWithValue("conversation_id", conversationId);
            command.Parameters.AddWithValue("lease_seconds", _leaseSeconds);

            var acquiredStatus = await command.ExecuteScalarAsync(cancellationToken);
            if (acquiredStatus is not null)
            {
                return InboxAcquireResult.Acquired;
            }
        }

        await using (var statusCommand = new NpgsqlCommand(
            "SELECT status FROM ops.message_inbox WHERE message_id = @message_id",
            connection))
        {
            statusCommand.Parameters.AddWithValue("message_id", messageId);
            var currentStatus = await statusCommand.ExecuteScalarAsync(cancellationToken) as string;

            return currentStatus switch
            {
                "completed" => InboxAcquireResult.Completed,
                "processing" => InboxAcquireResult.InProgress,
                "failed" => InboxAcquireResult.InProgress,
                _ => throw new InvalidOperationException($"Inbox row for message '{messageId}' could not be resolved.")
            };
        }
    }

    public async Task MarkCompletedAsync(string messageId, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        const string sql = """
            UPDATE ops.message_inbox
            SET status = 'completed',
                lease_until = NULL,
                completed_at = now(),
                updated_at = now(),
                last_error = NULL
            WHERE message_id = @message_id;
            """;

        await ExecuteUpdateAsync(sql, messageId, errorType: null, cancellationToken);
    }

    public async Task MarkFailedAsync(string messageId, string errorType, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);

        const string sql = """
            UPDATE ops.message_inbox
            SET status = 'failed',
                lease_until = NULL,
                updated_at = now(),
                last_error = @last_error
            WHERE message_id = @message_id;
            """;

        await ExecuteUpdateAsync(sql, messageId, errorType, cancellationToken);
    }

    private async Task ExecuteUpdateAsync(
        string sql,
        string messageId,
        string? errorType,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("message_id", messageId);

        if (errorType is not null)
        {
            command.Parameters.AddWithValue("last_error", errorType.Length <= 500 ? errorType : errorType[..500]);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
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

            const string sql = """
                CREATE SCHEMA IF NOT EXISTS ops;

                CREATE TABLE IF NOT EXISTS ops.message_inbox (
                    message_id text PRIMARY KEY,
                    conversation_id text NOT NULL,
                    status text NOT NULL CHECK (status IN ('processing', 'completed', 'failed')),
                    lease_until timestamptz,
                    attempt_count integer NOT NULL DEFAULT 0,
                    last_error text,
                    received_at timestamptz NOT NULL DEFAULT now(),
                    updated_at timestamptz NOT NULL DEFAULT now(),
                    completed_at timestamptz
                );

                CREATE INDEX IF NOT EXISTS idx_message_inbox_status_lease
                    ON ops.message_inbox (status, lease_until);
                """;

            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            _schemaReady = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }
}
