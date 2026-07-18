using Confluent.Kafka;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using conversation_orchestrator.Adapters.Inbound.Http;
using conversation_orchestrator.Adapters.Outbound.Http;
using conversation_orchestrator.Adapters.Outbound.Messaging;
using conversation_orchestrator.Adapters.Outbound.Persistence;
using conversation_orchestrator.Application.Ports.Inbound;
using conversation_orchestrator.Application.Ports.Outbound;
using conversation_orchestrator.Application.UseCases;
using conversation_orchestrator.Configuration;
using conversation_orchestrator.Platform;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddPlatformServices(builder.Configuration);

builder.Services.AddOptions<AgentRuntimeOptions>().Bind(builder.Configuration.GetSection(AgentRuntimeOptions.SectionName));
builder.Services.AddOptions<ChannelBffOptions>().Bind(builder.Configuration.GetSection(ChannelBffOptions.SectionName));
builder.Services.AddOptions<HandoffServiceOptions>().Bind(builder.Configuration.GetSection(HandoffServiceOptions.SectionName));
builder.Services.AddOptions<AuditServiceOptions>().Bind(builder.Configuration.GetSection(AuditServiceOptions.SectionName));
builder.Services.AddOptions<ConversationMemoryOptions>().Bind(builder.Configuration.GetSection(ConversationMemoryOptions.SectionName));
builder.Services.AddOptions<KafkaOptions>().Bind(builder.Configuration.GetSection(KafkaOptions.SectionName));
builder.Services.AddOptions<OtelOptions>().Bind(builder.Configuration.GetSection(OtelOptions.SectionName));
builder.Services.AddOptions<PostgresOptions>().Bind(builder.Configuration.GetSection(PostgresOptions.SectionName));

var otelEndpoint = builder.Configuration.GetSection(OtelOptions.SectionName).Get<OtelOptions>()?.OtlpEndpoint
    ?? "http://localhost:4317";
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("conversation-orchestrator"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddNpgsql()
        .AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otelEndpoint)));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<NpgsqlDataSource>(sp =>
{
    var options = sp.GetRequiredService<IOptions<PostgresOptions>>().Value;
    var cs = new NpgsqlConnectionStringBuilder(options.ConnectionString) { Timeout = 5, CommandTimeout = 5 };
    return new NpgsqlDataSourceBuilder(cs.ConnectionString).Build();
});
builder.Services.AddSingleton<IMessageInboxStore, PostgresMessageInboxStore>();

static IHttpClientBuilder AddInternalClient<TClient, TImplementation, TOptions>(
    IServiceCollection services,
    string audience,
    Func<TOptions, string> resolveUrl)
    where TClient : class
    where TImplementation : class, TClient
    where TOptions : class
{
    return services.AddHttpClient<TClient, TImplementation>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<TOptions>>().Value;
            client.BaseAddress = new Uri(resolveUrl(options));
        })
        .AddHttpMessageHandler(sp => new InternalRequestHandler(
            sp.GetRequiredService<InternalTokenService>(),
            sp.GetRequiredService<TenantContext>(),
            audience))
        .AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 2;
            options.Retry.Delay = TimeSpan.FromMilliseconds(200);
            options.Retry.DisableForUnsafeHttpMethods();
        });
}

AddInternalClient<IAgentRuntimeClient, AgentRuntimeClient, AgentRuntimeOptions>(
    builder.Services, "agent-runtime-renegotiation", options => options.BaseUrl);
AddInternalClient<IChannelReplyClient, ChannelReplyClient, ChannelBffOptions>(
    builder.Services, "whatsapp-bff", options => options.BaseUrl);
AddInternalClient<IHandoffServiceClient, HandoffServiceClient, HandoffServiceOptions>(
    builder.Services, "conversation-handoff-service", options => options.BaseUrl);
AddInternalClient<IAuditServiceClient, AuditServiceClient, AuditServiceOptions>(
    builder.Services, "conversation-audit-service", options => options.BaseUrl);
AddInternalClient<IConversationMemoryClient, ConversationMemoryClient, ConversationMemoryOptions>(
    builder.Services, "conversation-memory-service", options => options.BaseUrl);

builder.Services.AddSingleton<IProducer<string, string>>(sp =>
{
    var options = sp.GetRequiredService<IOptions<KafkaOptions>>().Value;
    return new ProducerBuilder<string, string>(new ProducerConfig
    {
        BootstrapServers = options.BootstrapServers,
        EnableIdempotence = true,
        Acks = Acks.All
    }).Build();
});
builder.Services.AddSingleton<IAdminClient>(sp =>
{
    var options = sp.GetRequiredService<IOptions<KafkaOptions>>().Value;
    return new AdminClientBuilder(new AdminClientConfig { BootstrapServers = options.BootstrapServers }).Build();
});
builder.Services.AddSingleton<IConversationEventPublisher, KafkaConversationEventPublisher>();
builder.Services.AddScoped<IIngestMessageUseCase, IngestMessageUseCase>();

builder.Logging.Configure(options => options.ActivityTrackingOptions =
    ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId);
builder.Logging.AddSimpleConsole(options => options.IncludeScopes = true);

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UsePlatformServices();
app.MapPlatformEndpoints();
app.MapGet("/health/ready", async (
    NpgsqlDataSource dataSource,
    IAdminClient adminClient,
    IOptions<InternalAuthOptions> authOptions,
    CancellationToken cancellationToken) =>
{
    var failures = new List<string>();
    if (string.IsNullOrWhiteSpace(authOptions.Value.SigningKey)) failures.Add("internal_auth_signing_key_missing");
    try
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync(cancellationToken);
    }
    catch { failures.Add("postgres_unavailable"); }
    try { adminClient.GetMetadata(TimeSpan.FromSeconds(2)); }
    catch { failures.Add("kafka_unavailable"); }

    return failures.Count == 0
        ? Results.Ok(new { status = "ready", failures })
        : Results.Json(new { status = "not_ready", failures }, statusCode: 503);
}).AllowAnonymous();

app.MapMessageIngestionEndpoints();
app.Run();

public partial class Program;
