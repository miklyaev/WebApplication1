using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using WebApplication1.Data;

namespace WebApplication1.IntegrationTests;

// Требует запущенных контейнеров: docker compose up -d
public sealed class TaskCompletionIntegrationTests
{
    private const string PostgresConnectionString =
        "Host=localhost;Port=5433;Database=TestDb;Username=postgres;Password=postgres";
    private const string RabbitMqHost = "localhost";
    private const string QueueName = "task.completed";
    private const string ExchangeName = "task.events";
    private const string RoutingKey = "task.completed";

    private sealed record TaskResponse(Guid Id, string Title, string Priority, DateTimeOffset? CompletedAt);

    [Fact]
    public async Task CreatingTaskWithoutBody_UsesDefaultValues()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", PostgresConnectionString);
            builder.UseSetting("RabbitMq:Host", RabbitMqHost);
        });

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
        }

        var client = factory.CreateClient();
        var response = await client.PostAsync("/tasks", content: null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var task = json.RootElement;
        var taskId = task.GetProperty("id").GetGuid();

        try
        {
            Assert.Equal("New task", task.GetProperty("title").GetString());
            Assert.False(task.GetProperty("isCompleted").GetBoolean());
            Assert.Equal("Medium", task.GetProperty("priority").GetString());
            Assert.Null(task.GetProperty("completedAt").GetString());
        }
        finally
        {
            await client.DeleteAsync($"/tasks/{taskId}");
        }
    }

    [Fact]
    public async Task CompletingTaskViaApi_DeliversTaskCompletedMessageToRabbitMq()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Postgres", PostgresConnectionString);
            builder.UseSetting("RabbitMq:Host", RabbitMqHost);
        });

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
        }

        var client = factory.CreateClient();

        // Подписка до завершения задачи, чтобы не потерять сообщение.
        await using var connection = await new ConnectionFactory { HostName = RabbitMqHost }.CreateConnectionAsync();
        await using var channel = await connection.CreateChannelAsync();
        await channel.QueueDeclareAsync(QueueName, durable: true, exclusive: false, autoDelete: false);

        var received = new TaskCompletionSource<(string Exchange, string RoutingKey, string Body)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new AsyncEventingBasicConsumer(channel);
        Guid? taskId = null;
        consumer.ReceivedAsync += (_, args) =>
        {
            var body = Encoding.UTF8.GetString(args.Body.ToArray());
            // В очереди могут остаться сообщения от прошлых запусков, берём только сообщение нашей задачи.
            using var json = JsonDocument.Parse(body);
            if (taskId is not null
                && json.RootElement.GetProperty("taskId").GetGuid() == taskId.Value)
            {
                received.TrySetResult((args.Exchange, args.RoutingKey, body));
            }

            return Task.CompletedTask;
        };
        await channel.BasicConsumeAsync(QueueName, autoAck: true, consumer);

        // 1. Создать задачу через API.
        var createResponse = await client.PostAsJsonAsync("/tasks", new { title = "Integration test task" });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<TaskResponse>();
        Assert.NotNull(created);
        taskId = created.Id;

        try
        {
            // 2. Завершить задачу.
            var completeResponse = await client.PutAsync($"/tasks/{created.Id}/complete", content: null);
            Assert.Equal(HttpStatusCode.OK, completeResponse.StatusCode);

            // 3. Проверить сообщение в RabbitMQ.
            var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(ExchangeName, message.Exchange);
            Assert.Equal(RoutingKey, message.RoutingKey);

            using var json = JsonDocument.Parse(message.Body);
            var root = json.RootElement;
            Assert.Equal(created.Id, root.GetProperty("taskId").GetGuid());
            Assert.Equal("Integration test task", root.GetProperty("title").GetString());
            Assert.Equal("Medium", root.GetProperty("priority").GetString());
            Assert.True(root.TryGetProperty("completedAt", out var completedAt));
            Assert.Equal(JsonValueKind.String, completedAt.ValueKind);
        }
        finally
        {
            await client.DeleteAsync($"/tasks/{created.Id}");
        }
    }
}
