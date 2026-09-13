using System.Security.Claims;
using NutriTrack.Api.Contracts.Ai;
using NutriTrack.Api.Services;

namespace NutriTrack.Api.Endpoints;

public static class AiEndpoints
{
    public static void MapAiEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/ai").WithTags("Ai").RequireAuthorization();

        group.MapPost("/parse-meal", async (
            ParseMealRequest request,
            IConfiguration configuration,
            AiMealAssistant assistant,
            ClaimsPrincipal user,
            AiRateLimiter limiter,
            HttpResponse httpResponse,
            CancellationToken ct) =>
        {
            const int MaxMessages = 10;
            const int MaxMessageLength = 2000;

            // Fehlender Schluessel ist hier bewusst KEIN Startabbruch wie beim Jwt-Schluessel:
            // ein nicht eingerichtetes Zusatzfeature darf das Tagebuch nicht lahmlegen.
            if (string.IsNullOrWhiteSpace(configuration["Gemini:ApiKey"]))
                return Results.Json(
                    new { Error = "KI-Erfassung ist nicht eingerichtet (Gemini:ApiKey fehlt)." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            // Die Grenzpruefungen stehen vor dem Zaehler und vor dem Gemini-Aufruf: eine Eingabe,
            // die ohnehin abgelehnt wird, darf weder Kontingent noch Wartezeit kosten.
            if (request.Messages.Count is 0 or > MaxMessages)
                return Results.BadRequest(new { Error = $"Bitte 1 bis {MaxMessages} Nachrichten senden." });

            if (request.Messages.Any(m => string.IsNullOrWhiteSpace(m.Text) || m.Text.Length > MaxMessageLength))
                return Results.BadRequest(new { Error = $"Jede Nachricht muss 1 bis {MaxMessageLength} Zeichen haben." });

            var userId = user.FindFirst(ClaimTypes.NameIdentifier)!.Value;
            if (!limiter.TryAcquire(userId))
                return Results.Json(
                    new { Error = "Zu viele KI-Anfragen in der letzten Stunde. Versuche es später noch einmal." },
                    statusCode: StatusCodes.Status429TooManyRequests);

            try
            {
                var result = await assistant.ParseAsync(request.Messages, ct);
                return Results.Ok(result);
            }
            catch (GeminiQuotaException ex)
            {
                return AiQuotaResponse.From(ex, httpResponse);
            }
            catch (GeminiMalformedResponseException ex)
            {
                // Ein Wiederholungsversuch steckt bereits im Assistenten; kommt es hier an,
                // hat auch der zweite Anlauf Unsinn geliefert.
                return AiFailureResponse.From(
                    ex,
                    "Die KI hat unverständlich geantwortet. Formuliere es bitte anders.",
                    StatusCodes.Status502BadGateway);
            }
            catch (GeminiUnavailableException ex)
            {
                return AiFailureResponse.From(
                    ex,
                    "Die KI ist gerade nicht erreichbar.",
                    StatusCodes.Status503ServiceUnavailable);
            }
        });
    }
}
