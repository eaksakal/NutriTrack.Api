using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NutriTrack.Api.Contracts.Ai;
using NutriTrack.Domain.Entities;

namespace NutriTrack.Api.Services;

/// <summary>Google ist erreichbar, aber nicht nutzbar (Timeout, Netz, 5xx).</summary>
public class GeminiUnavailableException(string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>
    /// Der Grund in einem Satz, samt der tiefsten Ursache. Zeitdeckel, ein 500 von Google und ein
    /// abgelaufener Schluessel sehen von aussen gleich aus, verlangen aber verschiedene Reaktionen;
    /// wer das nicht erfaehrt, tippt sein Essen von Hand ein statt den Schluessel zu erneuern.
    /// </summary>
    public string Detail
    {
        get
        {
            var ursache = InnerException;
            while (ursache?.InnerException is { } tiefer)
                ursache = tiefer;

            return ursache is null ? Message : $"{Message} ({ursache.GetType().Name}: {ursache.Message})";
        }
    }
}

/// <summary>Welche Grenze Google gerissen sah. Google beantwortet alle mit demselben 429.</summary>
public enum GeminiQuotaScope
{
    /// <summary>Der Rumpf nannte keine auswertbare Grenze.</summary>
    Unknown,

    /// <summary>Anfragen pro Minute — in Sekunden vorbei, kein Grund zur Aufregung.</summary>
    PerMinute,

    /// <summary>Anfragen pro Tag — bis Mitternacht (Pazifik) ist Schluss.</summary>
    PerDay,
}

/// <summary>Eine Mengengrenze wurde gerissen (429). <see cref="Scope"/> sagt welche.</summary>
public class GeminiQuotaException(
    string message,
    GeminiQuotaScope scope = GeminiQuotaScope.Unknown,
    TimeSpan? retryAfter = null) : Exception(message)
{
    public GeminiQuotaScope Scope { get; } = scope;

    /// <summary>Von Google genannte Wartezeit; null, wenn er keine nannte.</summary>
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Antwort kam an, passt aber nicht zum erzwungenen Schema.</summary>
public class GeminiMalformedResponseException(string message) : Exception(message);

/// <summary>Wie ein Zielwunsch in Worten zu lesen ist. Mehr braucht der Rechner nicht.</summary>
public class GeminiWishResult
{
    /// <summary>"lose", "hold" oder "gain".</summary>
    [JsonPropertyName("direction")]
    public string Direction { get; set; } = "hold";

    /// <summary>Abweichung vom Erhaltungsbedarf in Prozent; der Rechner kappt sie ohnehin.</summary>
    [JsonPropertyName("intensityPercent")]
    public decimal? IntensityPercent { get; set; }

    /// <summary>"balanced", "highProtein" oder "lowCarb".</summary>
    [JsonPropertyName("style")]
    public string Style { get; set; } = "balanced";

    /// <summary>Ein Satz, wie der Wunsch verstanden wurde - zur Gegenkontrolle durch den Nutzer.</summary>
    [JsonPropertyName("interpretation")]
    public string Interpretation { get; set; } = string.Empty;
}

public class GeminiParseResult
{
    public string? Question { get; set; }
    public List<GeminiItem> Items { get; set; } = [];
}

public class GeminiItem
{
    [JsonPropertyName("searchTerm")]
    public string SearchTerm { get; set; } = string.Empty;

    [JsonPropertyName("label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Kennung einer Zeile aus dem Verlaufsblock ("v2"), wenn der Nutzer sich auf etwas frueher
    /// Gegessenes bezogen hat. Null im Normalfall. Aufgeloest wird sie im Assistenten - hier steht
    /// nur, was das Modell gesagt hat.
    /// </summary>
    [JsonPropertyName("sourceRef")]
    public string? SourceRef { get; set; }

    [JsonPropertyName("quantityInGrams")]
    public decimal QuantityInGrams { get; set; }

    [JsonPropertyName("mealType")]
    public string MealType { get; set; } = "Snack";

    /// <summary>"branded" oder "generic" - siehe Systemanweisung. Steuert, ob ueberhaupt eine
    /// Produktdatenbank gefragt wird.</summary>
    [JsonPropertyName("productKind")]
    public string ProductKind { get; set; } = "generic";

    [JsonPropertyName("estimate")]
    public NutrientEstimate Estimate { get; set; } = new();
}

public class GeminiService(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<GeminiService> logger,
    TimeProvider timeProvider)
{
    private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/interactions";

    // Die Interactions-API ist revisioniert. Ohne diesen Header liefert Google die jeweils
    // neueste Revision aus — und damit potenziell einen anderen Antwortumschlag, als
    // ExtractPayload auspackt. Der Wert nagelt genau die Revision fest, gegen die diese Klasse
    // geschrieben ist; ein Wechsel ist dann eine bewusste Aenderung hier statt einer stillen
    // Ueberraschung im Betrieb.
    private const string ApiRevision = "2026-05-20";

    // Die Grenze zwischen Nachfragen und Annehmen entscheidet, ob das Feature im Alltag taugt:
    // zu viele Rueckfragen sind laestiger als die bestehende Suche.
    //
    // Die Einheiten stehen hier AUSDRUECKLICH je Feld. Der Rest der Anwendung fuehrt Natrium in
    // GRAMM je 100 g (OpenFoodFacts-Feld sodium_100g, siehe MealEndpoints.CalcMicro); ein
    // Sprachmodell nennt Natrium von sich aus praktisch immer in Milligramm. Ohne diesen Satz
    // landet der Wert um den Faktor 1000 zu hoch im Tagebuch.
    private const string SystemInstruction = """
        Du zerlegst deutschsprachige Beschreibungen von Mahlzeiten in einzelne Posten.
        Fuer jeden Posten lieferst du: searchTerm (kurzer Suchbegriff fuer eine
        Lebensmitteldatenbank, ohne Mengenangabe), label (lesbarer Name), quantityInGrams
        (Menge in Gramm, Fluessigkeiten in Milliliter gleich Gramm), mealType (genau einer von
        Breakfast, Lunch, Dinner, Snack), productKind und estimate (Naehrwerte je 100 g).

        productKind entscheidet, woher die Naehrwerte am Ende kommen:
          "branded"  Ein gekauftes, verpacktes Produkt, das der Nutzer benennbar gemacht hat -
                     eine Marke ("Koelln Zarte Haferflocken", "Alpro Sojadrink"), ein
                     Fertiggericht oder ein Barcode. Nur dann wird eine Produktdatenbank gefragt.
          "generic"  Alles andere: ein Grundnahrungsmittel ohne Marke ("eine Banane", "Magerquark")
                     und jedes selbst gekochte oder zubereitete Gericht ("Spaghetti Bolognese",
                     "Linsensuppe", "Ruehrei"). Hier zaehlt DEIN Wert, nicht die Datenbank.
        Im Zweifel "generic". Eine Produktdatenbank kennt fuer "Spaghetti" nur TROCKENE Nudeln
        (etwa 360 kcal je 100 g); gekochte haben etwa 150. Ein falsches "branded" macht daraus
        den doppelten Wert.

        Zubereitungszustand gehoert in label UND in estimate: "Spaghetti (gekocht)" mit etwa
        150 kcal je 100 g, nicht der Trockenwert. Dasselbe gilt fuer Reis, Nudeln und
        Huelsenfruechte.
        Die Einheiten in estimate sind bindend und beziehen sich IMMER auf 100 g des
        Lebensmittels:
          calories       Kilokalorien (kcal) je 100 g
          protein        Gramm je 100 g
          carbohydrates  Gramm je 100 g
          fat            Gramm je 100 g
          fiber          Gramm je 100 g
          sugar          Gramm je 100 g
          saturatedFat   Gramm je 100 g
          sodium         GRAMM je 100 g, NICHT Milligramm. Ein Broetchen hat etwa 0.45,
                         nicht 450. Teile einen in Milligramm gedachten Wert durch 1000.
        Rechne Haushaltsmasse um: eine Scheibe Kaese etwa 30 g, eine Tasse Kaffee etwa 200 ml,
        ein Broetchen etwa 60 g.
        Fehlt eine Angabe, die den Naehrwert deutlich veraendert, stelle GENAU EINE kurze
        Rueckfrage im Feld question und lasse items leer. Bei Kleinigkeiten nimm den ueblichen
        Wert an, statt nachzufragen.
        Ausdruecklich nachfragen musst du bei unbestimmten Mengenangaben zu einer vollstaendigen
        Mahlzeit - "grosse Portion", "eine Schuessel", "ein Teller", "viel", "wenig". Bei diesen
        Formulierungen liegen zwischen zwei plausiblen Annahmen leicht 300 kcal, und das ist die
        groesste Fehlerquelle ueberhaupt. Eine Zahl zu raten, die der Nutzer in zwei Sekunden
        haette nennen koennen, ist der schlechtere Weg.
        Nenne in der Rueckfrage ruhig eine Groessenordnung zur Auswahl, damit sie leicht zu
        beantworten ist.
        Steht ueber dem Gespraech ein Abschnitt "Bisher gegessen", dann ist das der Verlauf der
        letzten Tage, jede Zeile mit einer Kennung in eckigen Klammern. Bezieht sich der Nutzer auf
        eine dieser Zeilen ("das Eis von gestern", "nochmal das Fruehstueck", "den Rest davon"),
        setze sourceRef auf ihre Kennung, zum Beispiel "v2". Die Naehrwerte sind dann bereits
        bekannt und dein estimate wird verworfen - fuelle es trotzdem, das Schema verlangt es.
        quantityInGrams gilt weiterhin und ist deine Aufgabe: "die andere Haelfte" und "nochmal
        dasselbe" meinen die Menge aus der Zeile, "die Haelfte davon" die halbe.
        Ohne erkennbaren Bezug laesst du sourceRef weg und verfaehrst wie bisher. Erfinde NIE eine
        Kennung, die nicht im Abschnitt steht.
        Antworte ausschliesslich im vorgegebenen Schema.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Der eine Ort, an dem "Gemini ist gerade nichts wert" entsteht. Bis hierher wurde dieser Pfad
    /// - anders als Mengengrenze und Schemabruch - gar nicht protokolliert: der Grund verschwand
    /// zwischen Wurf und Endpunkt, und im Log stand nichts. Wer wirft, schreibt es also auch auf.
    /// </summary>
    private GeminiUnavailableException Unavailable(string grund, Exception? ursache = null)
    {
        logger.LogWarning(ursache, "Gemini nicht nutzbar: {Grund}", grund);
        return new GeminiUnavailableException(grund, ursache);
    }

    public async Task<GeminiParseResult> ParseAsync(
        IReadOnlyList<ChatMessage> messages, string historyBlock, CancellationToken ct)
    {
        var apiKey = configuration["Gemini:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw Unavailable("Gemini:ApiKey fehlt.");

        var transcript = new StringBuilder();

        // Der Verlauf steht VOR dem Gespraech: er ist Hintergrund, nicht Teil dessen, was der
        // Nutzer gerade gesagt hat. Stuende er dahinter, liest ihn das Modell leicht als letzte
        // Aeusserung und zerlegt den Verlauf selbst in Posten.
        if (!string.IsNullOrWhiteSpace(historyBlock))
        {
            transcript.AppendLine(historyBlock.TrimEnd());
            transcript.AppendLine();
        }

        foreach (var message in messages)
            transcript.AppendLine($"{(message.Role == "assistant" ? "Rueckfrage" : "Nutzer")}: {message.Text}");

        var inner = await SendAsync(SystemInstruction, transcript.ToString(), ResponseSchema, ct);

        GeminiParseResult result;
        try
        {
            result = JsonSerializer.Deserialize<GeminiParseResult>(inner, JsonOptions)
                     ?? throw new GeminiMalformedResponseException("Leere Antwort.");
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Gemini-Antwort passt nicht zum Schema.");
            throw new GeminiMalformedResponseException("Antwort passt nicht zum Schema.");
        }

        foreach (var item in result.Items)
        {
            item.MealType = NormalizeMealType(item.MealType);
            // Unbekanntes wird "generic": lieber der eigene Standardwert als ein zufaelliges
            // Markenprodukt aus der Datenbank. Der Fehler faellt dann kleiner aus.
            item.ProductKind = item.ProductKind?.Trim().ToLowerInvariant() == "branded" ? "branded" : "generic";
            // Leere Zeichenkette ist dasselbe wie "kein Bezug". Das Modell liefert bei einem
            // optionalen Feld gern "" statt es wegzulassen, und ein leerer Schluessel wuerde im
            // Woerterbuch spaeter als sinnlose Suche auflaufen.
            item.SourceRef = string.IsNullOrWhiteSpace(item.SourceRef) ? null : item.SourceRef.Trim();
            NormalizeEstimate(item, logger);
        }

        return result;
    }

    /// <summary>
    /// Letzte Notbremse gegen unmoegliche Schaetzwerte, bevor sie ueber die API in die Datenbank
    /// wandern. Der eigentliche Vertrag steht in <see cref="SystemInstruction"/> und im Schema;
    /// dies faengt nur ab, was physikalisch nicht sein kann — denn korrigieren laesst sich ein
    /// falscher Naehrwert spaeter nicht mehr: PUT /api/meals/{id} aendert nur Menge, Mahlzeit und
    /// Zeit, und ueber FindReusableFoodItemAsync entstuende ein globaler FoodItem mit dem Unsinn.
    /// Bewusst nur die unmoeglichen Bereiche: ein Wert, der bloss ungewoehnlich ist, bleibt stehen.
    /// </summary>
    private static void NormalizeEstimate(GeminiItem item, ILogger logger)
    {
        var estimate = item.Estimate;

        // Reines Fett hat rund 900 kcal je 100 g; mehr kann kein Lebensmittel haben.
        estimate.Calories = Clamp(estimate.Calories, 900m);

        // Ein Naehrstoff kann nicht mehr als 100 g je 100 g ausmachen.
        estimate.Protein = Clamp(estimate.Protein, 100m);
        estimate.Carbohydrates = Clamp(estimate.Carbohydrates, 100m);
        estimate.Fat = Clamp(estimate.Fat, 100m);
        estimate.Fiber = Clamp(estimate.Fiber, 100m);
        estimate.Sugar = Clamp(estimate.Sugar, 100m);
        estimate.SaturatedFat = Clamp(estimate.SaturatedFat, 100m);

        // Natrium fuehrt die Anwendung in GRAMM je 100 g. Selbst reines Kochsalz kommt auf nur
        // rund 39 g Natrium je 100 g — alles darueber ist mit Sicherheit ein in Milligramm
        // gedachter Wert (das Modell neigt trotz Anweisung dazu). Faktor 1000 statt Kappen:
        // Kappen machte aus 450 mg glaubwuerdige 40 g und damit einen unauffaelligen Unsinn.
        const decimal maxSodiumGramsPer100g = 40m;
        if (estimate.Sodium > maxSodiumGramsPer100g)
        {
            logger.LogWarning(
                "Natrium-Schaetzung {Value} je 100 g fuer {Label} ist als Gramm unmoeglich; " +
                "als Milligramm gewertet und durch 1000 geteilt.", estimate.Sodium, item.Label);
            estimate.Sodium /= 1000m;
        }

        estimate.Sodium = Clamp(estimate.Sodium, maxSodiumGramsPer100g);
    }

    private static decimal Clamp(decimal value, decimal max) => value < 0 ? 0m : Math.Min(value, max);

    private static decimal? Clamp(decimal? value, decimal max) => value is null ? null : Clamp(value.Value, max);

    /// <summary>
    /// Schaelt den JSON-Text aus Googles Antwortumschlag.
    /// Gemessen an der Interactions-API (Revision <see cref="ApiRevision"/>): die Ausgabe des
    /// Modells steckt in <c>steps[]</c> im Schritt mit <c>type == "model_output"</c>, dort im
    /// ersten <c>content[]</c>-Eintrag mit einem <c>text</c>-Feld. Zusaetzlich akzeptiert wird das
    /// Bequemfeld <c>output_text</c> auf der Wurzel, das die SDKs ausweisen.
    /// Rueckwaerts durch <c>steps</c>: vor der Modellantwort koennen Werkzeug-Schritte stehen
    /// (function_call, google_search_call ...), die eigene content-Blöcke mitbringen; der letzte
    /// model_output ist die eigentliche Antwort.
    /// Die gesamte Kenntnis ueber den Umschlag steckt hier: aendert Google das Format, ist dies
    /// die einzige Stelle, die nachzieht. Die Fehlermeldung nennt darum die tatsaechlich
    /// empfangenen Wurzel-Feldnamen — ein Formatwechsel zeigt so sofort, wo der Text nun steckt.
    /// </summary>
    private static string ExtractPayload(string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new GeminiMalformedResponseException("Antwort von Gemini ist kein JSON.");
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("output_text", out var outputText)
                    && outputText.ValueKind == JsonValueKind.String
                    && outputText.GetString() is { Length: > 0 } direct)
                {
                    return direct;
                }

                if (root.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array
                    && TextFromSteps(steps) is { } fromSteps)
                {
                    return fromSteps;
                }
            }

            throw new GeminiMalformedResponseException(
                "Unerwarteter Antwortumschlag; erwartet wurde steps[].content[].text " +
                $"(oder output_text). Tatsaechlich empfangen: {DescribeRoot(root)}");
        }
    }

    /// <summary>
    /// Der eine Weg nach draussen. Beide Aufgaben - Mahlzeiten zerlegen und einen Zielwunsch
    /// deuten - gehen hier durch, damit Kopfzeilen, Zeitverhalten, Fehlerabbildung und die
    /// Denkstufe nur an einer Stelle stehen und nicht auseinanderlaufen.
    /// </summary>
    private async Task<string> SendAsync(string systemInstruction, string input, object schema, CancellationToken ct)
    {
        var apiKey = configuration["Gemini:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw Unavailable("Gemini:ApiKey fehlt.");

        var model = configuration["Gemini:Model"] is { Length: > 0 } configured
            ? configured
            : "gemini-3.6-flash";

        // NICHT auf gemini-3.5-flash zurueckstellen. Das Modell steht zwar weiterhin in
        // /v1beta/models, ist ueber /v1beta/interactions aber tot: gemessen am 2026-09-15 vom
        // Betriebsrechner schickt Google darauf ueber 50 s KEIN EINZIGES BYTE - kein 404, kein
        // 400, nur Schweigen, bis der Zeitdeckel zuschlaegt. Derselbe Rumpf gegen
        // gemini-3.6-flash: 200 nach 2,9 s. Das sah wie ein zu knapper Deckel aus und kostete
        // zwei Erhoehungen (15 -> 25 -> 45 s), bevor jemand die Antwortzeit wirklich MASS.
        // 3.7 und 3.8 scheiden aus: sie lehnen thinking_level=minimal ab.

        // Gemessen am echten Dienst (gemini-3.5-flash, 2026-09-12, gleiche Eingabe):
        //   Standard  8-15 s, 859 Denk-Token, 1185 Token gesamt  (riss den Zeitdeckel)
        //   low        5,3 s, 637 Denk-Token,  843 Token gesamt
        //   minimal    3,0 s,   0 Denk-Token,  210 Token gesamt
        // Gleiche Qualitaet bei einem Fuenftel der Token - deshalb minimal. Konfigurierbar, weil
        // nicht jedes Modell dieselben Stufen kennt (minimal/low/medium/high).
        var thinkingLevel = configuration["Gemini:ThinkingLevel"] is { Length: > 0 } stufe ? stufe : "minimal";

        // system_instruction ist ein eigenes Feld der Interactions-API. Die Anweisung dort
        // unterzubringen statt sie dem Nutzertext voranzustellen, haelt beides sauber getrennt:
        // der Nutzer kann die Anweisung nicht mit eigenem Text ueberschreiben.
        var payload = new
        {
            model,
            input,
            system_instruction = systemInstruction,
            response_format = new
            {
                type = "text",
                mime_type = "application/json",
                schema
            },
            generation_config = new { thinking_level = thinkingLevel }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-goog-api-key", apiKey);
        request.Headers.Add("Api-Revision", ApiRevision);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Timeout des HttpClient, nicht Abbruch durch den Aufrufer.
            throw Unavailable("Gemini hat nicht rechtzeitig geantwortet.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw Unavailable("Gemini ist nicht erreichbar.", ex);
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw await ReadQuotaFailureAsync(response, ct);

        if (!response.IsSuccessStatusCode)
            throw Unavailable($"Gemini antwortete mit {(int)response.StatusCode}.");

        return ExtractPayload(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// Liest aus einem 429 heraus, WELCHE Grenze gerissen wurde. Google unterscheidet Minuten-
    /// und Tagesgrenze nur im Rumpf (<c>error.details[]</c>: <c>QuotaFailure.violations[].quotaId</c>
    /// und <c>RetryInfo.retryDelay</c>), nicht im Statuscode. Ohne diese Unterscheidung bekommt
    /// der Nutzer bei 27 Sekunden Wartezeit zu lesen, sein Kontingent sei fuer heute aufgebraucht.
    ///
    /// Ins Log gehen nur die ausgelesenen Felder, nie der Rumpf: was Google in eine Fehlermeldung
    /// schreibt, ist nicht unsere Entscheidung, und der Text der Mahlzeit hat im Log nichts verloren.
    /// </summary>
    private async Task<GeminiQuotaException> ReadQuotaFailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? quotaId = null;
        TimeSpan? retryAfter = null;

        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("details", out var details)
                    && details.ValueKind == JsonValueKind.Array)
                {
                    foreach (var detail in details.EnumerateArray())
                    {
                        if (!detail.TryGetProperty("@type", out var typ) || typ.ValueKind != JsonValueKind.String)
                            continue;

                        var typName = typ.GetString()!;

                        if (quotaId is null && typName.EndsWith("google.rpc.QuotaFailure", StringComparison.Ordinal)
                            && detail.TryGetProperty("violations", out var violations)
                            && violations.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var violation in violations.EnumerateArray())
                            {
                                if (violation.TryGetProperty("quotaId", out var id) && id.ValueKind == JsonValueKind.String)
                                {
                                    quotaId = id.GetString();
                                    break;
                                }
                            }
                        }

                        if (retryAfter is null && typName.EndsWith("google.rpc.RetryInfo", StringComparison.Ordinal)
                            && detail.TryGetProperty("retryDelay", out var delay) && delay.ValueKind == JsonValueKind.String)
                        {
                            retryAfter = ParseDuration(delay.GetString());
                        }
                    }
                }

                // Die flache Form der Interactions-API. Sie hat kein details[]: Metrik und
                // Wartezeit stehen im Fliesstext von message. Am 2026-09-13 im Betrieb gemessen -
                // bis dahin lief JEDER 429 als Unknown und ohne Wartezeit durch, weil oben nach
                // einem Feld gesucht wurde, das dieser Dienst gar nicht schickt.
                if ((quotaId is null || retryAfter is null)
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    var text = message.GetString()!;

                    if (quotaId is null && MetrikImText.Match(text) is { Success: true } m)
                        quotaId = m.Groups[1].Value;

                    if (retryAfter is null && WartezeitImText.Match(text) is { Success: true } w)
                        retryAfter = ParseDuration(w.Groups[1].Value + "s");
                }
            }
        }
        catch (JsonException)
        {
            // Ein 429 ohne lesbaren Rumpf bleibt ein 429 — nur eben ohne Begruendung.
        }

        // Der HTTP-Kopf ist die zweite Quelle: manche 429 tragen ihn statt der RetryInfo.
        retryAfter ??= response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date is { } date && date > timeProvider.GetUtcNow()
                ? date - timeProvider.GetUtcNow()
                : null);

        // Beide Umschlaege benennen dieselbe Sache anders: "GenerateRequestsPerDayPerProjectPerModel"
        // gegen "...generate_requests_per_model_per_day". Ohne die Trennzeichen zu entfernen,
        // faende Contains("PerDay") die zweite Schreibweise nie.
        var normalisiert = quotaId?.Replace("_", "").Replace("-", "");

        var scope = normalisiert switch
        {
            not null when normalisiert.Contains("PerDay", StringComparison.OrdinalIgnoreCase) => GeminiQuotaScope.PerDay,
            not null when normalisiert.Contains("PerMinute", StringComparison.OrdinalIgnoreCase) => GeminiQuotaScope.PerMinute,
            _ => GeminiQuotaScope.Unknown,
        };

        logger.LogWarning(
            "Gemini lehnte mit 429 ab. Grenze: {QuotaId} ({Scope}), genannte Wartezeit: {RetryAfter}.",
            quotaId ?? "unbenannt",
            scope,
            retryAfter?.ToString() ?? "keine");

        return new GeminiQuotaException($"Mengengrenze gerissen ({scope}).", scope, retryAfter);
    }

    /// <summary>"* Quota exceeded for metric: <c>&lt;name&gt;</c>, limit: 20, model: ..."</summary>
    private static readonly Regex MetrikImText =
        new(@"Quota exceeded for metric:\s*([^\s,]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>"Please retry in 39.826942774s."</summary>
    private static readonly Regex WartezeitImText =
        new(@"retry in\s*([0-9]+(?:\.[0-9]+)?)\s*s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Deutet die Dauer im Protokollpuffer-Format ("27s", "1.5s").</summary>
    private static TimeSpan? ParseDuration(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.EndsWith('s'))
            return null;

        return double.TryParse(
            value[..^1],
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var sekunden) && sekunden >= 0
            ? TimeSpan.FromSeconds(sekunden)
            : null;
    }

    private const string WishInstruction = """
        Du liest aus einem deutschsprachigen Satz heraus, welches Ernaehrungsziel jemand verfolgt.
        Du rechnest NICHTS aus - Kalorien und Makros bestimmt eine Formel, nicht du.
        Liefere:
          direction         "lose" (abnehmen), "hold" (Gewicht halten) oder "gain" (aufbauen)
          intensityPercent  gewuenschte Abweichung vom Erhaltungsbedarf in Prozent, falls der Satz
                            eine Geschwindigkeit nennt ("langsam" etwa 10, "zuegig" etwa 25,
                            ohne Angabe: weglassen)
          style             "lowCarb" bei ausdruecklichem Wunsch nach wenig Kohlenhydraten,
                            "highProtein" bei Muskelaufbau oder ausdruecklichem Proteinwunsch,
                            sonst "balanced"
          interpretation    EIN kurzer deutscher Satz, wie du den Wunsch verstanden hast. Der
                            Nutzer liest ihn zur Gegenkontrolle, bevor die Ziele uebernommen
                            werden - schreibe ihn so, dass ein Missverstaendnis auffaellt.
        Ist kein Ziel erkennbar, nimm "hold" und sage das in interpretation.
        """;

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
    };

    /// <summary>
    /// Deutet einen Zielwunsch. Es geht AUSSCHLIESSLICH der uebergebene Satz nach draussen -
    /// keine Koerperdaten. Das ist der Kern der Abmachung mit dem Nutzer: Google erfaehrt, dass
    /// jemand abnehmen will, aber nicht, wer wie viel wiegt.
    /// </summary>
    public async Task<GeminiWishResult> ParseWishAsync(string wish, CancellationToken ct)
    {
        var inner = await SendAsync(WishInstruction, $"Nutzer: {wish}", WishSchema, ct);

        try
        {
            var ergebnis = JsonSerializer.Deserialize<GeminiWishResult>(inner, JsonOptions)
                           ?? throw new GeminiMalformedResponseException("Leere Antwort.");

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
            logger.LogWarning(ex, "Gemini-Antwort zum Zielwunsch passt nicht zum Schema.");
            throw new GeminiMalformedResponseException("Antwort passt nicht zum Schema.");
        }
    }

    /// <summary>
    /// Bringt den Mahlzeitentyp auf einen der vier Enum-Werte.
    ///
    /// Zweiter Riegel hinter dem enum im Antwortschema: das Schema ist Googles Zusage, diese
    /// Methode die Absicherung dagegen, dass die Zusage bricht. Bei der Handprobe am 2026-09-12
    /// kam "Frühstück" zurueck — unser Prompt ist deutsch, also antwortet das Modell deutsch.
    /// Ohne Umsetzung lehnt MealEndpoints den Eintrag spaeter mit 400 ab, und der Nutzer haette
    /// eine Bestaetigungsmaske vor sich, die sich nicht uebernehmen laesst.
    ///
    /// Unbekanntes wird zu Snack statt zu einem Fehler: der Typ ist in der Maske ohnehin
    /// aenderbar, und eine ganze Mahlzeit an einer Vokabel scheitern zu lassen waere
    /// unverhaeltnismaessig.
    /// </summary>
    public static string NormalizeMealType(string? mealType)
    {
        var wert = (mealType ?? string.Empty).Trim();

        if (Enum.TryParse<MealType>(wert, ignoreCase: true, out var treffer) && Enum.IsDefined(treffer))
            return treffer.ToString();

        return wert.ToLowerInvariant() switch
        {
            "frühstück" or "fruehstueck" or "fruhstuck" or "morgens" or "breakfast" => "Breakfast",
            "mittagessen" or "mittag" or "mittags" or "lunch" => "Lunch",
            "abendessen" or "abendbrot" or "abend" or "abends" or "dinner" => "Dinner",
            _ => "Snack",
        };
    }

    private static string? TextFromSteps(JsonElement steps)
    {
        for (var index = steps.GetArrayLength() - 1; index >= 0; index--)
        {
            var step = steps[index];
            if (step.ValueKind != JsonValueKind.Object)
                continue;

            // Ein Schritt ohne type wird mitgenommen: fehlt die Angabe, ist der content-Block das
            // einzige, woran sich die Modellausgabe erkennen laesst.
            if (step.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && type.GetString() != "model_output")
            {
                continue;
            }

            if (!step.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object
                    && part.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String
                    && text.GetString() is { Length: > 0 } found)
                {
                    return found;
                }
            }
        }

        return null;
    }

    /// <summary>Nennt die Wurzel-Feldnamen der Antwort — bewusst nur die Namen, nie die Werte:
    /// im Fehlerfall landet das im Log, und der Text der Mahlzeit gehoert dort nicht hinein.</summary>
    private static string DescribeRoot(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
            ? $"Wurzelfelder [{string.Join(", ", root.EnumerateObject().Select(property => property.Name))}]"
            : $"Wurzelelement vom Typ {root.ValueKind}";

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
                        // Wertemenge gehoert ins Schema, nicht nur in den Anweisungstext. Die
                        // Handprobe am 2026-09-12 lieferte "Frühstück": das Modell antwortet in
                        // der Sprache des Prompts, und unser Prompt ist deutsch. Mit enum erzwingt
                        // Google die vier zulaessigen Werte schon beim Erzeugen.
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
                        // Die Einheit gehoert in das Schema, nicht nur in den Anweisungstext: das
                        // Schema reist bei jeder Anfrage unveraendert mit und ist die Stelle, an
                        // der das Modell die Felder zuordnet. Natrium in Gramm ist dabei der
                        // Punkt, an dem es ohne Ansage zuverlaessig danebengreift.
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
                            required = new[] { "calories", "protein", "carbohydrates", "fat" }
                        }
                    },
                    required = new[] { "searchTerm", "label", "quantityInGrams", "mealType", "productKind", "estimate" }
                }
            }
        },
        required = new[] { "items" }
    };
}
