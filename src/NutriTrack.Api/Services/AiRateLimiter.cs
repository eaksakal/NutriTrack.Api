using System.Collections.Concurrent;

namespace NutriTrack.Api.Services;

/// <summary>
/// Gleitendes Zeitfenster je Nutzer, im Speicher. Bewusst keine Tabelle: die Grenze schuetzt vor
/// versehentlichem Dauerfeuer und vor dem Aufbrauchen des Freikontingents, nicht vor einem
/// Angreifer — und ein Neustart, der den Zaehler vergisst, ist hier folgenlos.
/// </summary>
public class AiRateLimiter(TimeProvider timeProvider)
{
    private const int MaxCallsPerWindow = 30;
    private static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _calls = new();

    public bool TryAcquire(string userId)
    {
        var now = timeProvider.GetUtcNow();
        var queue = _calls.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());

        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > Window)
                queue.Dequeue();

            if (queue.Count >= MaxCallsPerWindow)
                return false;

            queue.Enqueue(now);
            return true;
        }
    }
}
