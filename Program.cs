using Confluent.Kafka;
using Microsoft.Extensions.Options;
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

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
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

// Exports the ASP.NET Core + outgoing HttpClient Activities that ActivityTrackingOptions
// below already correlates in logs (TraceId/SpanId/ParentId) to Jaeger via OTLP, so the
// same trace that ties log lines together also shows up as a real distributed trace.
var otelEndpoint = builder.Configuration.GetSection(OtelOptions.SectionName).Get<OtelOptions>()?.OtlpEndpoint
    ?? "http://localhost:4317";
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("conversation-orchestrator"))
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otelEndpoint)));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IMessageDedupeStore, InMemoryMessageDedupeStore>();

builder.Services.AddHttpClient<IAgentRuntimeClient, AgentRuntimeClient>((sp, client) =>
    {
        var options = sp.GetRequiredService<IOptions<AgentRuntimeOptions>>().Value;
        client.BaseAddress = new Uri(options.BaseUrl);
    })
    .AddStandardResilienceHandler(options =>
    {
        options.Retry.MaxRetryAttempts = 2;
        options.Retry.Delay = TimeSpan.FromMilliseconds(200);
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

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapMessageIngestionEndpoints();

app.Run();

public partial class Program;
