using System.Text.Json;
using System.Text.Json.Serialization;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using WebApplication1.Contracts;
using WebApplication1.Data;
using WebApplication1.Endpoints;
using WebApplication1.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Общий лимит на остановку хоста (Ctrl+C, SIGTERM). Ожидание публикаций внутри него.
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));

builder.Services.AddSingleton<InFlightOperations>();

// Priority принимается и отдаётся строкой (Low, Medium, High).
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("Postgres")));

var rabbitMq = builder.Configuration.GetSection("RabbitMq");

builder.Services.Configure<MassTransitHostOptions>(options => options.StopTimeout = TimeSpan.FromSeconds(5));

builder.Services.AddMassTransit(bus =>
{
    bus.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(
            rabbitMq["Host"] ?? "localhost",
            rabbitMq["VirtualHost"] ?? "/",
            host =>
            {
                host.Username(rabbitMq["Username"] ?? "guest");
                host.Password(rabbitMq["Password"] ?? "guest");
            });

        // Событие уходит как plain JSON, без конверта MassTransit.
        cfg.UseRawJsonSerializer(isDefault: true);
        cfg.ConfigureJsonSerializerOptions(options =>
        {
            options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        });

        cfg.Message<TaskCompleted>(x => x.SetEntityName(TaskEvents.Exchange));
        cfg.Publish<TaskCompleted>(x => x.ExchangeType = "topic");

        cfg.ConfigureEndpoints(context);
    });
});

var app = builder.Build();

// Срабатывает до остановки шины и веб-сервера: сначала дожидаемся начатых публикаций, потом гасим RabbitMQ.
var inFlight = app.Services.GetRequiredService<InFlightOperations>();
app.Lifetime.ApplicationStopping.Register(() => inFlight.StopAndWait(TimeSpan.FromSeconds(5)));

app.MapGet("/", () => "Hello World!");
app.MapTaskEndpoints();

app.Run();

// Нужен для WebApplicationFactory в интеграционных тестах.
public partial class Program;
