using WebApplication1.DTO;

namespace WebApplication1.Contracts;

public static class TaskEvents
{
    public const string Exchange = "task.events";
    public const string CompletedRoutingKey = "task.completed";
}

/// <summary>
/// Событие завершения задачи. Отправляется в обменник task.events с routing key task.completed
/// как plain JSON в camelCase (без конверта MassTransit), Priority — строкой.
/// </summary>
public record TaskCompleted(Guid TaskId, string Title, DateTimeOffset CompletedAt, Priority Priority);
