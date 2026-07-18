using conversation_orchestrator.Application.Ports.Inbound;
using conversation_orchestrator.Domain;

namespace conversation_orchestrator.Adapters.Inbound.Http;

public static class MessageIngestionEndpoints
{
    public static IEndpointRouteBuilder MapMessageIngestionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/messages", HandleAsync);
        return endpoints;
    }

    private static async Task<IResult> HandleAsync(
        InboundChannelMessage message,
        IIngestMessageUseCase useCase,
        ILogger<MessageIngestionLogCategory> logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(message.MessageId)
            || string.IsNullOrWhiteSpace(message.From)
            || string.IsNullOrWhiteSpace(message.ConversationId))
        {
            return Results.BadRequest(new { error = "MessageId, From, and ConversationId are required." });
        }

        var correlationId = Guid.NewGuid().ToString("n");
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = correlationId,
            ["ConversationId"] = message.ConversationId,
            ["MessageId"] = message.MessageId
        });

        var result = await useCase.ExecuteAsync(message, cancellationToken);

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
