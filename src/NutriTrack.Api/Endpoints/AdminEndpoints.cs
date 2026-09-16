using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NutriTrack.Api.Contracts.Admin;
using NutriTrack.Api.Services;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Endpoints;

public static class AdminEndpoints
{
    public static void MapAdminEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/admin").WithTags("Admin").RequireAuthorization();

        group.MapGet("/settings", (ClaimsPrincipal user, IConfiguration configuration, AiSettingsProvider provider) =>
        {
            if (!IstAdmin(user, configuration))
                return Results.NotFound();

            return Results.Ok(Antwort(provider));
        });

        group.MapPut("/settings", async (
            UpdateAiSettingsRequest request,
            ClaimsPrincipal user,
            IConfiguration configuration,
            AiSettingsProvider provider,
            AppDbContext db,
            CancellationToken ct) =>
        {
            if (!IstAdmin(user, configuration))
                return Results.NotFound();

            if (request.MaxOutputTokens is { } tokens && (tokens < 256 || tokens > 65536))
                return Results.BadRequest(new { Error = "maxOutputTokens muss zwischen 256 und 65536 liegen." });

            var zeile = await db.AiSettings.SingleOrDefaultAsync(ct);
            if (zeile is null)
            {
                zeile = new AiSettings { Id = 1 };
                db.AiSettings.Add(zeile);
            }

            // Leer heisst "zurueck zur Umgebungsvariable", nicht "leerer Modellname". Deshalb
            // wird auf null normalisiert statt die Eingabe durchzureichen.
            zeile.Model = Leer(request.Model);
            zeile.ThinkingLevel = Leer(request.ThinkingLevel);
            zeile.MaxOutputTokens = request.MaxOutputTokens;
            zeile.UpdatedAt = DateTime.UtcNow;

            await db.SaveChangesAsync(ct);

            // Ohne das gilt der alte Wert bis zum Neustart - und der Betreiber haelt die
            // Oberflaeche fuer kaputt.
            provider.Invalidate();

            return Results.Ok(Antwort(provider));
        });

        group.MapPost("/settings/probe", async (
            ClaimsPrincipal user,
            IConfiguration configuration,
            GeminiService gemini,
            AiRateLimiter limiter,
            CancellationToken ct) =>
        {
            if (!IstAdmin(user, configuration))
                return Results.NotFound();

            // Fehlender Schluessel ist die wahrscheinlichste Fehlkonfiguration ueberhaupt, und
            // ausgerechnet dafuer sagte die Probe bisher nichts Brauchbares: ohne diese Pruefung
            // wirft GeminiService.ProbeAsync eine GeminiUnavailableException, die hier ungefangen
            // durchschlaegt - dieses Projekt hat weder UseExceptionHandler noch AddProblemDetails,
            // also kommt ein 500 ohne Rumpf heraus. Vor der Bremse und nicht danach: ein Aufruf,
            // der ohnehin nicht klappen kann, soll keinen Platz aus dem Kontingentzaehler
            // verbrennen (siehe Kommentar an TryAcquire unten).
            if (string.IsNullOrWhiteSpace(configuration["Gemini:ApiKey"]))
                return Results.Json(
                    new { Error = "Verbindungstest ist nicht möglich (Gemini:ApiKey fehlt)." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var userId = user.FindFirst(ClaimTypes.NameIdentifier)!.Value;

            // Dieselbe Bremse wie die normale Erfassung: jede Probe verbraucht eine Anfrage aus
            // Googles Minutenkontingent von 20. Am 2026-09-15 brauchte genau so eine Diagnose das
            // Kontingent auf, und die Erfassung sah danach nur noch 429.
            if (!limiter.TryAcquire(userId))
                return Results.Json(
                    new { Error = "Zu viele KI-Anfragen in der letzten Stunde. Versuche es später noch einmal." },
                    statusCode: StatusCodes.Status429TooManyRequests);

            var ergebnis = await gemini.ProbeAsync(ct);

            return Results.Ok(new AiProbeResponse
            {
                StatusCode = ergebnis.StatusCode,
                DurationMs = ergebnis.DurationMs,
                Model = ergebnis.Model,
                ThinkingLevel = ergebnis.ThinkingLevel,
                RawBody = ergebnis.RawBody
            });
        });

        group.MapGet("/failures", async (
            ClaimsPrincipal user, IConfiguration configuration, AppDbContext db, CancellationToken ct) =>
        {
            if (!IstAdmin(user, configuration))
                return Results.NotFound();

            var eintraege = await db.AiFailures
                .AsNoTracking()
                .OrderByDescending(f => f.OccurredAt)
                .Take(AiFailureRecorder.MaxEntries)
                .Select(f => new AiFailureLogEntry
                {
                    OccurredAt = f.OccurredAt,
                    Kind = f.Kind,
                    Model = f.Model,
                    ThinkingLevel = f.ThinkingLevel,
                    DurationMs = f.DurationMs,
                    StatusCode = f.StatusCode,
                    Reason = f.Reason
                })
                .ToListAsync(ct);

            return Results.Ok(eintraege);
        });
    }

    /// <summary>
    /// Genau ein Administrator, benannt in der Konfiguration. Ist nichts gesetzt, ist NIEMAND
    /// Administrator - ein vergessener Eintrag darf die Verwaltung nicht fuer alle oeffnen.
    /// </summary>
    private static bool IstAdmin(ClaimsPrincipal user, IConfiguration configuration)
    {
        var erlaubt = configuration["Admin:Email"];
        if (string.IsNullOrWhiteSpace(erlaubt))
            return false;

        var email = user.FindFirst(ClaimTypes.Email)?.Value;
        return !string.IsNullOrWhiteSpace(email)
               && string.Equals(email.Trim(), erlaubt.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static AiSettingsResponse Antwort(AiSettingsProvider provider)
    {
        var s = provider.Read();

        return new AiSettingsResponse
        {
            Model = s.Model,
            ModelFromDatabase = s.ModelFromDb,
            ThinkingLevel = s.ThinkingLevel,
            ThinkingLevelFromDatabase = s.ThinkingLevelFromDb,
            MaxOutputTokens = s.MaxOutputTokens,
            MaxOutputTokensFromDatabase = s.MaxOutputTokensFromDb
        };
    }

    private static string? Leer(string? wert) => string.IsNullOrWhiteSpace(wert) ? null : wert.Trim();
}
