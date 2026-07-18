using System.Diagnostics;
using Confluent.Kafka;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

Activity.DefaultIdFormat = ActivityIdFormat.W3C;
Activity.ForceDefaultIdFormat = true;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddPlatformServices(builder.Configuration);
builder.Services.PostConfigure<JwtBearerOptions>(
    JwtBearerDefaults.AuthenticationScheme,
    options => options.MapInboundClaims = false);

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
        .AddSource(KafkaConversationEventPublisher.ActivitySourceName)
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
    .AddHttpMessageHandler(sp => new InternalRequestHandler(
        sp.GetRequiredService<InternalTokenService>(),
        sp.GetRequiredService<IOptions<InternalAuthOptions>>(),
        sp.GetRequiredService<TenantContext>(),
        "agent-runtime-renegotiation"))
    .AddStandardResilienceHandler(options =>
    {
        // Real OpenAI + MCP tool-call round trips observed taking ~21s end to end (see
        // docs/validation/2026-07-13-e2e-journey.md and the 2026-07-18 quick E2E test) -
        // comfortably past the framework's 10s/30s defaults. Retries are already disabled
        // for POST below, so AttemptTimeout is effectively the only budget that matters here;
        // sized with margin over the worst observed latency instead of retrying a call whose
        // side effects (LLM invocation, tool execution) aren't safe to repeat.
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(45);
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(60);
        // HttpStandardResilienceOptions requires CircuitBreaker.SamplingDuration >= 2x
        // AttemptTimeout.Timeout (validated at startup) - bumped along with AttemptTimeout
        // above to keep that invariant satisfied.
        options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(90);
        options.Retry.MaxRetryAttempts = 2;
        options.Retry.Delay = TimeSpan.FromMilliseconds(200);
        options.Retry.DisableForUnsafeHttpMethods();
    });

builder.Services.AddHttpClient<IChannelReplyClient, ChannelReplyClient>((sp, client) =>
    {
        var options = sp.GetRequiredService<IOptions<ChannelBffOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl);
    })
    .AddHttpMessageHandler(sp => new InternalRequestHandler(
        sp.GetRequiredService<InternalTokenService>(),
        sp.GetRequiredService<IOptions<InternalAuthOptions>>(),
        sp.GetRequiredService<TenantContext>(),
        "whatsapp-bff"))
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
    .AddHttpMessageHandler(sp => new InternalRequestHandler(
        sp.GetRequiredService<InternalTokenService>(),
        sp.GetRequiredService<IOptions<InternalAuthOptions>>(),
        sp.GetRequiredService<TenantContext>(),
        "conversation-handoff-service"))
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
    .AddHttpMessageHandler(sp => new InternalRequestHandler(
        sp.GetRequiredService<InternalTokenService>(),
        sp.GetRequiredService<IOptions<InternalAuthOptions>>(),
        sp.GetRequiredService<TenantContext>(),
        "conversation-audit-service"))
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
    .AddHttpMessageHandler(sp => new InternalRequestHandler(
        sp.GetRequiredService<InternalTokenService>(),
        sp.GetRequiredService<IOptions<InternalAuthOptions>>(),
        sp.GetRequiredService<TenantContext>(),
        "conversation-memory-service"))
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 2;
        options.Retry.Delay = TimeSpan.FromMilliseconds(200);
        options.Retry.DisableForUnsafeHttpMethods();
    });

builder.Services.AddSingleton<IProducer<string, string>>(sp =>
{
    var options = sp.GetRequiredService<IOptions<KafkaOptions>>().Value;
    var config = new ProducerConfig
    {
        BootstrapServers = options.BootstrapServers,
        EnableIdempotence = true,
        Acks = Acks.All,
        MessageSendMaxRetries = 3
    };
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
app.UsePlatformServices();
app.MapPlatformEndpoints();
app.MapMessageIngestionEndpoints();
app.Run();

public partial class Program;
