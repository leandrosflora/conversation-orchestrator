namespace conversation_orchestrator.Application.Ports.Outbound;

public interface IHandoffServiceClient
{
    /// <summary>Never throws; logs and returns on failure to reach the Handoff Service.</summary>
    Task RequestHandoffAsync(
        HandoffRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

public class HandoffRequest
{
    public required string ConversationId { get; init; }
    public required string Reason { get; init; }
}
