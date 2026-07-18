using System.Diagnostics;
using conversation_orchestrator.Application.Ports.Inbound;
using conversation_orchestrator.Domain;
using conversation_orchestrator.Platform;

namespace conversation_orchestrator.Adapters.Inbound.Http;

public static class MessageIngestionEndpoints
{
    public static IEndpointRouteBuilder MapMessageIngestionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/messages", HandleAsync)
            .RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        HttpRequest request,
        InboundChannelMessage message,
        IIngestMessageUseCase useCase,
        TenantContext tenantContext,
        PlatformMetrics metrics,
        ILogger<MessageIngestionLogCategory> logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message.MessageId)
            || string.IsNullOrWhiteSpace(message.From)
            || string.IsNullOrWhiteSpace(message.ConversationId))
        {
            metrics.Increment("orchestrator_messages_rejected_total", ("reason", "invalid_message"));
            return Results.BadRequest(new { error = "MessageId, From, and ConversationId are required." });
        }

        if (!TenantContext.TryNormalize(request.Headers["X-Tenant-Id"].ToString(), out var tenantId))
        {
            metrics.Increment("orchestrator_messages_rejected_total", ("reason", "invalid_tenant"));
            return Results.BadRequest(new
            {
                error = "X-Tenant-Id is required and must contain only letters, numbers, dot, underscore, colon, or hyphen."
            });
        }

        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("n");
        using var tenantScope = tenantContext.Push(tenantId);
        using var logScope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["TenantId"] = tenantId,
            ["ConversationId"] = message.ConversationId,
            ["MessageId"] = message.MessageId
        });

        var result = await useCase.ExecuteAsync(message, cancellationToken);
        metrics.Increment(
            "orchestrator_messages_total",
            ("outcome", result.ToString().ToLowerInvariant()));

        return result switch
        {
            IngestMessageResult.Accepted => Results.Accepted(),
            IngestMessageResult.AlreadyCompleted => Results.Accepted(),
            IngestMessageResult.InProgress => Results.Conflict(new
            {
                error = "Message is already being processed. Retry after the active Inbox lease completes."
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, null)
        };
    }
}

public sealed class MessageIngestionLogCategory;
