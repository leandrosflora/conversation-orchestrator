using Microsoft.Extensions.Caching.Memory;
using conversation_orchestrator.Application.Ports.Outbound;

namespace conversation_orchestrator.Adapters.Outbound.Persistence;

/// <summary>Mirrors whatsapp-bff's MemoryCacheMessageDedupeStore, one layer downstream: whatsapp-bff
/// only dedupes redeliveries from the WhatsApp Cloud API itself, not its own HTTP-client/Kafka-consumer
/// retries against this service, so the same MessageId can still reach POST /messages more than once.</summary>
public class InMemoryMessageDedupeStore(IMemoryCache cache) : IMessageDedupeStore
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    public bool TryMarkProcessed(string messageId)
    {
        if (cache.TryGetValue(messageId, out _))
        {
            return false;
        }

        cache.Set(messageId, true, Ttl);
        return true;
    }
}
