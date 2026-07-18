namespace conversation_orchestrator.Configuration;

public class PostgresOptions
{
    public const string SectionName = "Postgres";

    public string ConnectionString { get; init; } =
        "Host=localhost;Port=5432;Database=conversational_ai;Username=postgres;Password=postgres";

    public int InboxLeaseSeconds { get; init; } = 300;
}
