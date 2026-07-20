namespace conversation_orchestrator.Application.Outbox;

/// <summary>
/// Signals that an outbox effect dispatch failed in a way the downstream service has already
/// classified as permanent (e.g. whatsapp-bff's /internal/messages returning
/// { retryable: false } once a delivery outcome is settled) - OutboxDispatcherService must not
/// keep retrying these with the normal exponential backoff, since the outcome will never change.
/// </summary>
public sealed class NonRetryableDispatchException(string message) : Exception(message);
