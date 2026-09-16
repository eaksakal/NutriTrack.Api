using System.Net;
using System.Text;

namespace NutriTrack.Api.Tests.Infrastructure;

/// <summary>
/// Ersetzt den Primary-Handler des OpenRouterService. Kein Test darf zu openrouter.ai sprechen -
/// das Freikontingent liegt bei 50 Anfragen am Tag, eine Testsuite haette es in einem Lauf
/// aufgebraucht.
/// </summary>
public sealed class StubOpenRouterHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
    : HttpMessageHandler
{
    /// <summary>
    /// Der Umschlag, den OpenRouter wirklich schickt (am 2026-09-16 gegen den echten Dienst
    /// abgeholt): die Modellausgabe steckt als Zeichenkette in choices[0].message.content, das
    /// enthaltene JSON also eine Schachtelung tiefer als bei Gemini.
    /// </summary>
    public static HttpResponseMessage Payload(string innerJson)
    {
        var escaped = System.Text.Json.JsonSerializer.Serialize(innerJson);
        var envelope = $$"""
        {
          "id": "gen-stub",
          "model": "nex-agi/nex-n2.5-pro:free",
          "object": "chat.completion",
          "choices": [
            {
              "index": 0,
              "finish_reason": "stop",
              "message": { "role": "assistant", "content": {{escaped}} }
            }
          ],
          "usage": { "prompt_tokens": 900, "completion_tokens": 120, "total_tokens": 1020 }
        }
        """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>
    /// EIN ANBIETERFEHLER MIT STATUS 200 - der Wortlaut stammt vom 2026-09-16, als
    /// nvidia/nemotron-3-super-120b-a12b:free so antwortete. Wer nur den Statuscode prueft,
    /// verbucht das als unverstaendliche Antwort und sucht den Fehler bei sich.
    /// </summary>
    public static HttpResponseMessage UpstreamError(
        string message = "Upstream error from Nvidia: Service temporarily overloaded", int code = 502)
    {
        var body = $$"""
        {
          "id": "gen-stub-error",
          "error": {
            "message": "{{message}}",
            "code": {{code}},
            "metadata": { "error_type": "provider_unavailable" }
          }
        }
        """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>Das Tageskontingent von 50 ist erschoepft.</summary>
    public static HttpResponseMessage RateLimited() =>
        new(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(
                """{"error":{"message":"Rate limit exceeded: free-models-per-day","code":429}}""",
                Encoding.UTF8, "application/json")
        };

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(responder(request));
}
