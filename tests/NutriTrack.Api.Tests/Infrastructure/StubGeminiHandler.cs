using System.Net;
using System.Text;

namespace NutriTrack.Api.Tests.Infrastructure;

/// <summary>
/// Ersetzt den Primary-Handler des GeminiService. Kein Test darf zu Google sprechen — weder wegen
/// der Kosten noch wegen der Bedingungen fuer die unbezahlte Nutzung.
/// Das Verhalten je Test kommt ueber <see cref="NutriTrackApiFactory.GeminiResponder"/>.
/// </summary>
public sealed class StubGeminiHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    /// <summary>
    /// Der Antwortumschlag der Interactions-API (Revision 2026-05-20): die Modellausgabe steckt in
    /// <c>steps[]</c> im Schritt <c>type == "model_output"</c>, dort in <c>content[].text</c>.
    /// Bewusst vollstaendig nachgebaut — samt der Felder, die der Code NICHT liest (id, status,
    /// usage) und samt des vorangehenden <c>user_input</c>-Schritts: haette der Stub nur die zwei
    /// Felder, die ExtractPayload anfasst, bestaetigte er wieder nur die eigene Annahme.
    /// </summary>
    public static HttpResponseMessage Payload(string innerJson)
    {
        var escaped = System.Text.Json.JsonSerializer.Serialize(innerJson);
        var envelope = $$"""
        {
          "id": "v1_stub",
          "model": "gemini-3.5-flash",
          "status": "completed",
          "steps": [
            { "type": "user_input", "content": [ { "type": "text", "text": "stub" } ] },
            {
              "type": "model_output",
              "status": "completed",
              "content": [ { "type": "text", "text": {{escaped}} } ]
            }
          ],
          "usage": { "total_input_tokens": 7, "total_output_tokens": 20 }
        }
        """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json")
        };
    }

    /// <summary>Dieselbe Nutzlast im Bequemfeld <c>output_text</c> auf der Wurzel, das die SDKs
    /// ausweisen — der zweite Weg, den <c>ExtractPayload</c> akzeptieren muss.</summary>
    public static HttpResponseMessage OutputTextPayload(string innerJson)
    {
        var escaped = System.Text.Json.JsonSerializer.Serialize(innerJson);
        var envelope = $$"""
        { "id": "v1_stub", "model": "gemini-3.5-flash", "status": "completed", "output_text": {{escaped}} }
        """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/json")
        };
    }

    public static HttpResponseMessage Status(HttpStatusCode code) => new(code)
    {
        Content = new StringContent("""{"error":{"message":"stub"}}""", Encoding.UTF8, "application/json")
    };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(responder(request));
}
