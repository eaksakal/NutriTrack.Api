using NutriTrack.Api.Services;

namespace NutriTrack.Api.Endpoints;

/// <summary>
/// Haengt an die freundliche Meldung den technischen Grund. Beides wird gebraucht und keins
/// ersetzt das andere: <c>Error</c> sagt dem Nutzer, was er jetzt tun kann, <c>Detail</c> sagt,
/// warum es nicht ging. Ohne das Zweite ist ein abgelaufener Schluessel von einem Zeitdeckel
/// nicht zu unterscheiden - beide lesen sich als "die KI ist gerade nicht erreichbar".
/// </summary>
internal static class AiFailureResponse
{
    public static IResult From(Exception ex, string message, int statusCode) =>
        Results.Json(new { Error = message, Detail = Detail(ex) }, statusCode: statusCode);

    private static string Detail(Exception ex) => ex switch
    {
        GeminiUnavailableException unavailable => unavailable.Detail,
        _ => ex.Message,
    };
}
