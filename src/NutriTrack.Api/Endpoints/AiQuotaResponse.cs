using NutriTrack.Api.Services;

namespace NutriTrack.Api.Endpoints;

/// <summary>
/// Uebersetzt eine gerissene Mengengrenze in eine Antwort, die dem Nutzer sagt, was er tun kann.
/// "Erschoepft" ist fuer die Minutengrenze schlicht falsch — sie ist nach Sekunden vorbei, und
/// wer das nicht weiss, laesst die KI fuer den Rest des Tages links liegen.
/// </summary>
internal static class AiQuotaResponse
{
    public static IResult From(AiQuotaException ex, HttpResponse response)
    {
        // Retry-After gehoert laut HTTP zu jedem 429. Ganze Sekunden aufgerundet, damit ein
        // Client, der stumpf so lange wartet, nicht eine Zehntelsekunde zu frueh wiederkommt.
        if (ex.RetryAfter is { } wartezeit)
            response.Headers.RetryAfter = ((int)Math.Ceiling(Math.Max(wartezeit.TotalSeconds, 1))).ToString();

        return Results.Json(new { Error = Message(ex) }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    private static string Message(AiQuotaException ex) => ex.Scope switch
    {
        AiQuotaScope.PerDay =>
            "Das KI-Tageskontingent ist aufgebraucht. Morgen geht es wieder — bis dahin kannst du von Hand eintragen.",

        AiQuotaScope.PerMinute =>
            $"Zu viele KI-Anfragen in kurzer Zeit. Versuche es {Gleich(ex.RetryAfter)} noch einmal.",

        // Google nannte keine Grenze: dann darf hier auch nicht behauptet werden, welche es war.
        _ => $"Die KI weist gerade weitere Anfragen ab. Versuche es {Gleich(ex.RetryAfter)} noch einmal.",
    };

    private static string Gleich(TimeSpan? retryAfter) => retryAfter is { } w
        ? $"in {Math.Max((int)Math.Ceiling(w.TotalSeconds), 1)} Sekunden"
        : "gleich";
}
