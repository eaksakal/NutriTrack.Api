using System.Net;
using System.Text;
using System.Text.Json;
using NutriTrack.Api.Contracts.Ai;

namespace NutriTrack.Api.Services;

/// <summary>
/// Der zweite Anbieter, angebunden ueber OpenRouters OpenAI-Dialekt.
///
/// Es gibt ihn, weil Googles Freikontingent 20 Anfragen am TAG erlaubt (gemessen 2026-09-16) und
/// damit fuer ein Ernaehrungstagebuch zu klein ist. OpenRouter gibt 50 - dafuer brauchten die
/// kostenlosen Modelle im selben Test 24 bis 35 Sekunden statt Geminis 3 bis 9.
///
/// Systemanweisung und Naehrwert-Normalisierung kommen aus AiInstructions, der gemeinsamen Heimat
/// anbieterneutraler Fachregeln fuer beide IAiProvider-Umsetzungen - nicht aus einer eigenen
/// Abschrift und nicht aus GeminiService. Nur der Umschlag ist hier anders.
/// </summary>
public class OpenRouterService(
    HttpClient httpClient,
    IConfiguration configuration,
    AiSettingsProvider settingsProvider,
    ILogger<OpenRouterService> logger,
    TimeProvider timeProvider) : IAiProvider
{
    private const string Endpoint = "https://openrouter.ai/api/v1/chat/completions";

    public string Name => IAiProvider.OpenRouter;

    public string ApiKeySetting => "OpenRouter:ApiKey";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Der eine Ort, an dem "OpenRouter ist gerade nichts wert" entsteht - analog zu
    /// GeminiService.Unavailable. Wer wirft, schreibt es auch ins Log; sonst verschwindet der
    /// Grund zwischen Wurf und Endpunkt.
    /// </summary>
    private AiUnavailableException Unavailable(string grund, Exception? ursache = null)
    {
        logger.LogWarning(ursache, "OpenRouter nicht nutzbar: {Grund}", grund);
        return new AiUnavailableException(grund, ursache);
    }

    public async Task<AiParseResult> ParseAsync(
        IReadOnlyList<ChatMessage> messages, string historyBlock, CancellationToken ct)
    {
        var apiKey = configuration["OpenRouter:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw Unavailable("OpenRouter:ApiKey fehlt.");

        var transcript = new StringBuilder();

        // Derselbe Aufbau wie bei GeminiService.ParseAsync: der Verlauf steht VOR dem Gespraech,
        // sonst liest das Modell ihn leicht als letzte Aeusserung und zerlegt ihn selbst.
        if (!string.IsNullOrWhiteSpace(historyBlock))
        {
            transcript.AppendLine(historyBlock.TrimEnd());
            transcript.AppendLine();
        }

        foreach (var message in messages)
            transcript.AppendLine($"{(message.Role == "assistant" ? "Rueckfrage" : "Nutzer")}: {message.Text}");

        var inner = await SendAsync(AiInstructions.SystemInstruction, transcript.ToString(), ResponseSchema, ct);

        AiParseResult result;
        try
        {
            result = JsonSerializer.Deserialize<AiParseResult>(inner, JsonOptions)
                     ?? throw new AiMalformedResponseException("Leere Antwort.");
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "OpenRouter-Antwort passt nicht zum Schema.");

            // ex.Message reicht durch, nie der gelesene Wert - dieselbe Begruendung wie in
            // GeminiService.ParseAsync.
            throw new AiMalformedResponseException($"Antwort passt nicht zum Schema: {ex.Message}");
        }

        foreach (var item in result.Items)
        {
            item.MealType = AiInstructions.NormalizeMealType(item.MealType);
            // Unbekanntes wird "generic" - derselbe Sicherheitsgedanke wie bei GeminiService.
            item.ProductKind = item.ProductKind?.Trim().ToLowerInvariant() == "branded" ? "branded" : "generic";
            item.SourceRef = string.IsNullOrWhiteSpace(item.SourceRef) ? null : item.SourceRef.Trim();
            AiInstructions.NormalizeEstimate(item, logger);
        }

        return result;
    }

    /// <summary>
    /// Deutet einen Zielwunsch - Vorlage ist GeminiService.ParseWishAsync, nur Umschlag und
    /// Schema (samt additionalProperties:false) unterscheiden sich.
    /// </summary>
    public async Task<AiWishResult> ParseWishAsync(string wish, CancellationToken ct)
    {
        var inner = await SendAsync(AiInstructions.WishInstruction, $"Nutzer: {wish}", WishSchema, ct);

        try
        {
            var ergebnis = JsonSerializer.Deserialize<AiWishResult>(inner, JsonOptions)
                           ?? throw new AiMalformedResponseException("Leere Antwort.");

            ergebnis.Direction = ergebnis.Direction?.Trim().ToLowerInvariant() switch
            {
                "lose" => "lose",
                "gain" => "gain",
                _ => "hold",
            };

            ergebnis.Style = ergebnis.Style?.Trim() switch
            {
                "lowCarb" => "lowCarb",
                "highProtein" => "highProtein",
                _ => "balanced",
            };

            return ergebnis;
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "OpenRouter-Antwort zum Zielwunsch passt nicht zum Schema.");
            throw new AiMalformedResponseException($"Antwort passt nicht zum Schema: {ex.Message}");
        }
    }

    /// <summary>
    /// EIN Aufruf mit den geltenden Einstellungen und einer FESTEN Beispieleingabe - Vorlage ist
    /// GeminiService.ProbeAsync. Ruft bewusst NICHT SendAsync auf: die Probe soll den
    /// error-im-Rumpf-Fall in der Rohantwort zeigen, nicht als Ausnahme verstecken. Genau dieser
    /// Fall ist der Grund, warum es diese Methode ueberhaupt gibt.
    /// </summary>
    public async Task<AiProbeResult> ProbeAsync(CancellationToken ct)
    {
        var apiKey = configuration["OpenRouter:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw Unavailable("OpenRouter:ApiKey fehlt.");

        var einstellungen = settingsProvider.Read();
        var start = timeProvider.GetTimestamp();

        var payload = BuildBody(AiInstructions.SystemInstruction, "Nutzer: zwei Broetchen mit Gouda\n", ResponseSchema);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Authorization", $"Bearer {apiKey}");

        int status;
        string rumpf;
        try
        {
            using var response = await httpClient.SendAsync(request, ct);
            status = (int)response.StatusCode;
            rumpf = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException && !ct.IsCancellationRequested)
        {
            // Status 0 heisst "gar keine Antwort" - dieselbe Lesart wie bei GeminiService.
            status = 0;
            rumpf = ex.Message;
        }

        var dauer = (long)timeProvider.GetElapsedTime(start).TotalMilliseconds;

        if (rumpf.Length > 2000)
            rumpf = rumpf[..2000] + "\n… (gekürzt)";

        // ThinkingLevel: null statt eines erfundenen Werts. Die Denkstufe ist eine Eigenheit von
        // Gemini; OpenRouter kennt sie nicht, und ein Platzhalter wie "" oder "-" behauptete eine
        // Einstellung, die es dort nicht gibt.
        return new AiProbeResult(status, dauer, einstellungen.OpenRouterModel, null, rumpf);
    }

    /// <summary>
    /// Der eine Weg nach draussen fuer ParseAsync und ParseWishAsync - analog zu
    /// GeminiService.SendAsync. Die Fehlerabbildung folgt derselben: 429 wird zur
    /// AiQuotaException, ein Timeout ohne Abbruch durch den Aufrufer und jeder andere
    /// Fehlerstatus werden zur AiUnavailableException. Zusaetzlich - ohne Gegenstueck bei Gemini -
    /// der error-im-Rumpf-Fall aus ThrowIfErrorInBody.
    /// </summary>
    private async Task<string> SendAsync(string systemInstruction, string input, object schema, CancellationToken ct)
    {
        var apiKey = configuration["OpenRouter:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw Unavailable("OpenRouter:ApiKey fehlt.");

        var body = BuildBody(systemInstruction, input, schema);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Authorization", $"Bearer {apiKey}");

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Timeout des HttpClient, nicht Abbruch durch den Aufrufer.
            throw Unavailable("OpenRouter hat nicht rechtzeitig geantwortet.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw Unavailable("OpenRouter ist nicht erreichbar.", ex);
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw await ReadQuotaFailureAsync(response, ct);

        var responseBody = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw Unavailable($"OpenRouter antwortete mit {(int)response.StatusCode}.");

        // VOR dem Auspacken pruefen: OpenRouter beantwortet auch Anbieterausfaelle mit Status 200
        // (siehe ThrowIfErrorInBody). Wer erst ExtractContent aufruft, verbucht diesen Fall als
        // Schemabruch statt als Anbieterausfall - genau die Falle, die die Messung vom
        // 2026-09-16 aufgedeckt hat.
        ThrowIfErrorInBody(responseBody, Unavailable);

        return ExtractContent(responseBody);
    }

    /// <summary>
    /// Liest aus einem 429 die Fehlermeldung, damit sie im Log und in der Ausnahme steht statt
    /// nur "429". OpenRouter unterscheidet - anders als Google - keine Minuten- und Tagesgrenze
    /// ueber ein eigenes Feld, nur im Fliesstext von message ("Rate limit exceeded:
    /// free-models-per-day", belegt im eigenen Teststub). Ohne diese Zuordnung liest der Nutzer
    /// bei einer Tagessperre "Versuche es gleich noch einmal" - wortwoertlich der Fehler,
    /// dessentwegen dieser Branch gebaut wurde: eine Wartezeit im Sekundenbereich fuer eine
    /// Sperre, die bis morgen gilt.
    /// </summary>
    private async Task<AiQuotaException> ReadQuotaFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        string? message = null;

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("message", out var m)
                && m.ValueKind == JsonValueKind.String)
            {
                message = m.GetString();
            }
        }
        catch (JsonException)
        {
            // Ein 429 ohne lesbaren Rumpf bleibt ein 429 - nur eben ohne Begruendung.
        }

        var scope = message?.Contains("per-day", StringComparison.OrdinalIgnoreCase) == true
            ? AiQuotaScope.PerDay
            : AiQuotaScope.Unknown;

        // Der HTTP-Kopf ist eine zweite moegliche Quelle fuer die Wartezeit - dieselbe Lesart wie
        // in GeminiService.ReadQuotaFailureAsync. OpenRouter nennt im Rumpf selbst keine
        // Wartezeit (kein RetryInfo-Gegenstueck), deshalb bleibt das der einzige Weg dorthin.
        var retryAfter = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date && date > timeProvider.GetUtcNow()
                ? date - timeProvider.GetUtcNow()
                : null);

        logger.LogWarning(
            "OpenRouter lehnte mit 429 ab ({Scope}): {Message}", scope, message ?? "unbekannt");

        return new AiQuotaException(message ?? "OpenRouter: Mengengrenze gerissen.", scope, retryAfter);
    }

    /// <summary>
    /// Der Anfragerumpf im OpenAI-Dialekt. Anders als bei Gemini gibt es kein eigenes Feld fuer
    /// die Systemanweisung - sie ist die erste Nachricht mit der Rolle "system".
    /// </summary>
    private object BuildBody(string systemInstruction, string input, object schema)
    {
        var einstellungen = settingsProvider.Read();

        return new
        {
            model = einstellungen.OpenRouterModel,
            messages = new object[]
            {
                new { role = "system", content = systemInstruction },
                new { role = "user", content = input }
            },
            response_format = new
            {
                type = "json_schema",
                json_schema = new { name = "nutritrack", strict = true, schema }
            },
            max_tokens = einstellungen.MaxOutputTokens
        };
    }

    /// <summary>
    /// OpenRouter beantwortet auch ANBIETERAUSFAELLE mit Status 200 und legt den Fehler in den
    /// Rumpf - am 2026-09-16 beobachtet, als ein Modell "Upstream error from Nvidia: Service
    /// temporarily overloaded" mit 200 zurueckgab. Wer nur den Statuscode prueft, verbucht das
    /// als unverstaendliche Antwort und sucht den Fehler bei sich.
    ///
    /// Faengt JsonException ab: eine Antwort mit Status 200, die gar kein JSON ist (Gatewayseite,
    /// Wartungsseite, Proxy) ist derselbe Fehlerfall eine Stufe frueher - der Dienst hat
    /// geantwortet, aber nicht im vereinbarten Format. Ohne diesen Fang floege eine rohe
    /// JsonException heraus, die kein Endpunkt kennt (AiFailureResponse faengt nur AiUnavailable-,
    /// AiQuota- und AiMalformedResponseException), und der Nutzer saehe einen 500 ohne Rumpf.
    ///
    /// ValueKind wird bei error UND bei message geprueft, nicht nur TryGetProperty: ein Rumpf wie
    /// {"error":"ueberlastet"} oder eine message, die kein String ist, liesse TryGetProperty bzw.
    /// GetString() sonst eine InvalidOperationException werfen - dieselbe Kategorie Fehler, die
    /// der JsonException-Fang zwei Zeilen weiter oben bereits fuer "gar kein JSON" abfaengt.
    /// ReadQuotaFailureAsync macht diese Pruefung fuer message bereits richtig.
    /// </summary>
    private static void ThrowIfErrorInBody(string body, Func<string, Exception?, Exception> unavailable)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new AiMalformedResponseException("Antwort von OpenRouter ist kein JSON.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error))
            {
                return;
            }

            var message = error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var m)
                && m.ValueKind == JsonValueKind.String
                ? m.GetString()
                : null;

            throw unavailable($"OpenRouter meldet: {message ?? "unbekannter Fehler"}", null);
        }
    }

    /// <summary>
    /// Die Modellausgabe steckt in choices[0].message.content als Zeichenkette mit JSON darin -
    /// eine Schachtelung mehr als bei Gemini.
    ///
    /// Faengt JsonException wie ThrowIfErrorInBody ab (dieselbe Begruendung dort): SendAsync ruft
    /// zwar immer erst ThrowIfErrorInBody auf denselben Rumpf auf, aber diese Methode soll fuer
    /// sich selbst sicher sein und nicht stillschweigend von der Aufrufreihenfolge abhaengen.
    /// </summary>
    private static string ExtractContent(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new AiMalformedResponseException("Antwort von OpenRouter ist kein JSON.");
        }

        using (document)
        {
            if (document.RootElement.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var content)
                && content.GetString() is { Length: > 0 } text)
            {
                return text;
            }

            throw new AiMalformedResponseException(
                "Unerwarteter Antwortumschlag; erwartet wurde choices[0].message.content.");
        }
    }

    private static object WishSchema => new
    {
        type = "object",
        properties = new
        {
            direction = new { type = "string", @enum = new[] { "lose", "hold", "gain" } },
            intensityPercent = new { type = "number" },
            style = new { type = "string", @enum = new[] { "balanced", "highProtein", "lowCarb" } },
            interpretation = new { type = "string" },
        },
        required = new[] { "direction", "style", "interpretation" },
        additionalProperties = false,
    };

    /// <summary>
    /// Dasselbe Schema wie bei Gemini, aber mit additionalProperties:false an JEDEM Objekt:
    /// OpenRouters strikter Modus lehnt sonst ab. Googles Schema kennt das Feld nicht, deshalb
    /// zwei Aufbauten statt eines geteilten.
    /// </summary>
    private static object ResponseSchema => new
    {
        type = "object",
        properties = new
        {
            question = new { type = "string" },
            items = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        searchTerm = new { type = "string" },
                        label = new { type = "string" },
                        sourceRef = new
                        {
                            type = "string",
                            description = "Kennung einer Zeile aus dem Abschnitt \"Bisher gegessen\" "
                                          + "(zum Beispiel \"v2\"), wenn der Nutzer sich auf diesen "
                                          + "Eintrag bezieht. Sonst weglassen."
                        },
                        quantityInGrams = new { type = "number" },
                        mealType = new
                        {
                            type = "string",
                            @enum = new[] { "Breakfast", "Lunch", "Dinner", "Snack" }
                        },
                        productKind = new
                        {
                            type = "string",
                            @enum = new[] { "generic", "branded" },
                            description = "branded nur bei benannter Marke, Fertiggericht oder "
                                          + "Barcode; generic bei Grundnahrungsmitteln und allem "
                                          + "selbst Gekochten."
                        },
                        estimate = new
                        {
                            type = "object",
                            properties = new
                            {
                                calories = new { type = "number", description = "Kilokalorien (kcal) je 100 g" },
                                protein = new { type = "number", description = "Eiweiss in Gramm je 100 g" },
                                carbohydrates = new { type = "number", description = "Kohlenhydrate in Gramm je 100 g" },
                                fat = new { type = "number", description = "Fett in Gramm je 100 g" },
                                fiber = new { type = "number", description = "Ballaststoffe in Gramm je 100 g" },
                                sugar = new { type = "number", description = "Zucker in Gramm je 100 g" },
                                saturatedFat = new { type = "number", description = "Gesaettigte Fettsaeuren in Gramm je 100 g" },
                                sodium = new
                                {
                                    type = "number",
                                    description = "Natrium in GRAMM je 100 g, nicht in Milligramm. "
                                                  + "Beispiel: ein Broetchen hat etwa 0.45, nicht 450."
                                }
                            },
                            required = new[] { "calories", "protein", "carbohydrates", "fat" },
                            additionalProperties = false
                        }
                    },
                    required = new[] { "searchTerm", "label", "quantityInGrams", "mealType", "productKind", "estimate" },
                    additionalProperties = false
                }
            }
        },
        required = new[] { "items" },
        additionalProperties = false
    };
}
