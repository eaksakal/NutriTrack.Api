using Microsoft.EntityFrameworkCore;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Services;

/// <summary>
/// Schreibt Fehlschlaege der KI-Erfassung mit, damit die Diagnose nicht wieder am
/// Betriebsrechner stattfinden muss - und kappt die Tabelle dabei selbst.
///
/// WAS NIE HINEINGEHOERT: der Esstext. Der Aufrufer uebergibt den Ausnahmetext, und die
/// Ausnahmen dieses Pfades nennen Feldnamen und Pfade, keine Inhalte (siehe
/// GeminiService.DescribeRoot). Kaeme hier je ein Aufrufer mit dem Prompt um die Ecke, waere das
/// ein Rueckschritt hinter die Zusage in .env.example.
/// </summary>
public class AiFailureRecorder(
    AppDbContext db,
    AiSettingsProvider settingsProvider,
    TimeProvider timeProvider,
    ILogger<AiFailureRecorder> logger)
{
    /// <summary>
    /// Ringpuffer statt Aufbewahrungsfrist: eine Tabelle, die nie waechst, braucht keinen
    /// Aufraeumjob und keine Einstellung, die jemand pflegen muss. 50 sind genug, um ein Muster
    /// zu erkennen ("seit dem Modellwechsel jedes Mal Schema"), und wenig genug, um sie ohne
    /// Blaettern zu lesen.
    /// </summary>
    public const int MaxEntries = 50;

    private const int MaxReasonLength = 200;

    /// <summary>
    /// Kein <see cref="CancellationToken"/> vom Aufrufer: das waere hier falsch statt bloss
    /// unnoetig. Der Fall, der das Protokoll am dringendsten sehen soll, ist genau der, bei dem
    /// der Nutzer die Geduld verliert und mitten im 45-Sekunden-Zeitdeckel abbricht - stuenden
    /// Schreiben und Kappen unter seinem Token, wuerde ausgerechnet dieser Zeitdeckel-Fehlschlag
    /// nie im Protokoll landen. Die Schreibarbeit hier ist kurz und lokal (SQLite, keine
    /// Fremddienste) und lohnt keinen eigenen Abbruchmechanismus.
    /// </summary>
    public async Task RecordAsync(string kind, string reason, long durationMs, int? statusCode)
    {
        var einstellungen = settingsProvider.Read();

        try
        {
            db.AiFailures.Add(new AiFailure
            {
                Id = Guid.NewGuid(),
                OccurredAt = timeProvider.GetUtcNow().UtcDateTime,
                Kind = kind,
                Model = einstellungen.Model,
                ThinkingLevel = einstellungen.ThinkingLevel,
                DurationMs = (int)Math.Min(durationMs, int.MaxValue),
                StatusCode = statusCode,
                Reason = reason.Length > MaxReasonLength ? reason[..MaxReasonLength] : reason
            });

            await db.SaveChangesAsync(CancellationToken.None);

            // Erst schreiben, dann kappen: faellt das Kappen aus, ist der neue Eintrag trotzdem
            // da. Andersherum verloere man im Fehlerfall genau die Zeile, derentwegen jemand
            // nachsieht.
            var zuViele = await db.AiFailures
                .OrderByDescending(f => f.OccurredAt)
                .Skip(MaxEntries)
                .Select(f => f.Id)
                .ToListAsync(CancellationToken.None);

            if (zuViele.Count > 0)
            {
                await db.AiFailures.Where(f => zuViele.Contains(f.Id)).ExecuteDeleteAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            // Das Protokoll ist eine Nebensache. Scheitert es, hat der Nutzer bereits einen
            // Fehlschlag vor sich - ihm daraufhin einen zweiten zu zeigen, hilft niemandem.
            logger.LogWarning(ex, "Fehlschlag liess sich nicht protokollieren.");
        }
    }
}
