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

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddOptions<AgentRuntimeOptions>()
    .Bind(builder.Configuration.GetSection(AgentRuntimeOptions.SectionName));
builder.Services.AddOptions<ChannelBffOptions>()
    .Bind(builder.Configuration.GetSection(ChannelBffOptions.SectionName));
builder.Services.AddOptions<HandoffServiceOptions>()
    .Bind(builder.Configuration.GetSection(HandoffServiceOptions.SectionName));
builder.Services.AddOptions<AuditServiceOptions>()
    .Bind(builder.Configuration.GetSection(AuditServiceOptions.SectionName));
builder.Services.AddOptions<ConversationMemoryOptions>()
    .Bind(builder.Configuration.GetSection(ConversationMemoryOptions.SectionName));
builder.Services.AddOptions<KafkaOptions>()
    .Bind(builder.Configuration.GetSection(KafkaOptions.SectionName));
builder.Services.AddOptions<OtelOptions>()
    .Bind(builder.Configuration.GetSection(OtelOptions.SectionName));
builder.Services.AddOptions<PostgresOptions>()
    .Bind(builder.Configuration.GetSection(PostgresOptions.SectionName));

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
    var connectionString = new NpgsqlConnectionStringBuilder(options.ConnectionString)
    {
        Timeout = 5,
        CommandTimeout = 5
    };

    return new NpgsqlDataSourceBuilder(connectionString.ConnectionString).Build();
});
builder.Services.AddSingleton<IMessageInboxStore, PostgresMessageInboxStore>();

builder.Services.AddHttpClient<IAgentRuntimeClient, AgentRuntimeClient>((sp, client) =>
    {
        var options = sp.GetRequiredService<IOptions<AgentRuntimeOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl);
    })
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 2;
        options.Retry.Delay = TimeSpan.FromMilliseconds(200);
        options.Retry.DisableForUnsafeHttpMethods();
    });

builder.Services.AddHttpClient<IChannelReplyClient, ChannelReplyClient>((sp, client) =>
    {
        var options = sp.GetRequiredService<IOptions<ChannelBffOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl);
    })
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 2;
        options.Retry.Delay = TimeSpan.FromMilliseconds(200);
        options.Retry.DisableForUnsafeHttpMethods();
    });

builder.Services.AddHttpClient<IHandoffServiceClient, HandoffServiceClient>((sp, client) =>
    {
        var options = sp.GetRequiredService<IOptions<HandoffServiceOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl);
    })
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 2;
        options.Retry.Delay = TimeSpan.FromMilliseconds(200);
        options.Retry.DisableForUnsafeHttpMethods();
    });

builder.Services.AddHttpClient<IAuditServiceClient, AuditServiceClient>((sp, client) =>
    {
        var options = sp.GetRequiredService<IOptions<AuditServiceOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl);
    })
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 2;
        options.Retry.Delay = TimeSpan.FromMilliseconds(200);
        options.Retry.DisableForUnsafeHttpMethods();
    });

builder.Services.AddHttpClient<IConversationMemoryClient, ConversationMemoryClient>((sp, client) =>
    {
        var options = sp.GetRequiredService<IOptions<ConversationMemoryOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl);
    })
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 2;
        options.Retry.Delay = TimeSpan.FromMilliseconds(200);
        options.Retry.DisableForUnsafeHttpMethods();
    });

builder.Services.AddSingleton<IProducer<string, string>>(sp =>
{
    var options = sp.GetRequiredService<IOptions<KafkaOptions>>().Value;
    var config = new ProducerConfig { BootstrapServers = options.BootstrapServers };
    return new ProducerBuilder<string, string>(config).Build();
});
builder.Services.AddSingleton<IConversationEventPublisher, KafkaConversationEventPublisher>();

builder.Services.AddScoped<IIngestMessageUseCase, IngestMessageUseCase>();

builder.Logging.Configure(options =>
{
    options.ActivityTrackingOptions = ActivityTrackingOptions.TraceId
        | ActivityTrackingOptions.SpanId
        | ActivityTrackingOptions.ParentId;
});
builder.Logging.AddSimpleConsole(options => options.IncludeScopes = true);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.MapMessageIngestionEndpoints();
app.Run();

public partial class Program;
