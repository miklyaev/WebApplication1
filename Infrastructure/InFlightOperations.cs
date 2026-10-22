namespace WebApplication1.Infrastructure;

/// <summary>
/// Отслеживает операции, которые публикуют сообщения в RabbitMQ. При остановке приложения
/// новые операции отклоняются, а остановка ждёт завершения уже начатых.
/// </summary>
public sealed class InFlightOperations
{
    private readonly object _gate = new();
    private int _active;
    private bool _stopping;

    /// <returns>false, если приложение останавливается и новые операции не принимаются.</returns>
    public bool TryBegin()
    {
        lock (_gate)
        {
            if (_stopping)
            {
                return false;
            }

            _active++;
            return true;
        }
    }

    public void End()
    {
        lock (_gate)
        {
            _active--;
            if (_active == 0)
            {
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>Запрещает новые операции и ждёт завершения активных не дольше timeout.</summary>
    public void StopAndWait(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        lock (_gate)
        {
            _stopping = true;

            while (_active > 0)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    return;
                }

                Monitor.Wait(_gate, remaining);
            }
        }
    }
}
