using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using System.Text.Json;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using WebApplication1.Contracts;
using WebApplication1.Data;
using WebApplication1.Domain;
using WebApplication1.Infrastructure;

namespace WebApplication1.Endpoints;

public static class TaskEndpoints
{
    private static readonly TimeSpan PublishTimeout = TimeSpan.FromSeconds(5);

    public static IEndpointRouteBuilder MapTaskEndpoints(this IEndpointRouteBuilder app)
    {
        var tasks = app.MapGroup("/tasks").WithTags("Tasks");

        tasks.MapPost("", CreateTask);
        tasks.MapGet("", GetTasks);
        tasks.MapPut("/{id:guid}/complete", CompleteTask);
        tasks.MapDelete("/{id:guid}", DeleteTask);

        return app;
    }

    private static async Task<Results<Created<TaskItem>, ValidationProblem>> CreateTask(
        HttpRequest httpRequest,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        CreateTaskRequest? input = null;
        if (httpRequest.ContentLength > 0 || httpRequest.Headers.ContainsKey("Transfer-Encoding"))
        {
            input = await httpRequest.ReadFromJsonAsync<CreateTaskRequest>(cancellationToken);
        }

        var request = input is null ? null : input with { Title = input.Title?.Trim() };

        if (request is not null && !TryValidate(request, out var errors))
        {
            return TypedResults.ValidationProblem(errors);
        }

        var task = new TaskItem
        {
            Id = Guid.NewGuid(),
            Title = request?.Title ?? "New task",
            IsCompleted = false,
            CreatedAt = DateTimeOffset.UtcNow,
            CompletedAt = null,
            Priority = request?.Priority ?? Priority.Medium
        };

        db.Tasks.Add(task);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/tasks/{task.Id}", task);
    }

    private static async Task<Ok<List<TaskItem>>> GetTasks(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var tasks = await db.Tasks
            .AsNoTracking()
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(tasks);
    }

    private static async Task<Results<Ok<TaskItem>, NotFound, Conflict, StatusCodeHttpResult>> CompleteTask(
        Guid id,
        AppDbContext db,
        IPublishEndpoint publishEndpoint,
        InFlightOperations inFlight,
        ILogger<TaskItem> logger,
        CancellationToken cancellationToken)
    {
        // При остановке приложения новые завершения не принимаются, чтобы не потерять событие.
        if (!inFlight.TryBegin())
        {
            return TypedResults.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        try
        {
            var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
            if (task is null)
            {
                return TypedResults.NotFound();
            }

            // Уже завершённую задачу не меняем и событие повторно не публикуем.
            if (task.IsCompleted)
            {
                return TypedResults.Ok(task);
            }

            task.IsCompleted = true;
            task.CompletedAt = DateTimeOffset.UtcNow;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Задачу параллельно изменил другой запрос: событие не публикуем.
                return TypedResults.Conflict();
            }

            var message = new TaskCompleted(task.Id, task.Title, task.CompletedAt.Value, task.Priority);
            using var publishCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            publishCts.CancelAfter(PublishTimeout);
            try
            {
                await publishEndpoint.Publish(
                    message,
                    context => context.SetRoutingKey(TaskEvents.CompletedRoutingKey),
                    publishCts.Token);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Задача уже сохранена: если брокер недоступен или не отвечает за PublishTimeout, завершение не откатываем, событие только логируем.
                logger.LogError(ex, "Не удалось опубликовать TaskCompleted для задачи {TaskId}", task.Id);
            }

            return TypedResults.Ok(task);
        }
        finally
        {
            inFlight.End();
        }
    }

    private static async Task<Results<NoContent, NotFound>> DeleteTask(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var deleted = await db.Tasks
            .Where(t => t.Id == id)
            .ExecuteDeleteAsync(cancellationToken);

        return deleted == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }

    private static bool TryValidate<T>(T model, out Dictionary<string, string[]> errors)
    {
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        if (Validator.TryValidateObject(model!, new ValidationContext(model!), results, validateAllProperties: true))
        {
            errors = [];
            return true;
        }

        errors = results
            .GroupBy(r => JsonNamingPolicy.CamelCase.ConvertName(r.MemberNames.FirstOrDefault() ?? string.Empty))
            .ToDictionary(g => g.Key, g => g.Select(r => r.ErrorMessage ?? "Invalid value.").ToArray());
        return false;
    }
}
