# KI-Freitext-Erfassung Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eine Mahlzeit per Freitextsatz erfassen — Gemini zerlegt den Text in Posten, OpenFoodFacts liefert die Nährwerte, der Nutzer bestätigt.

**Architecture:** Ein neuer, schreibfreier Endpunkt `POST /api/ai/parse-meal` ruft Gemini (erzwungenes JSON-Schema) und schlägt für jeden erkannten Posten OpenFoodFacts nach. Der Gesprächsverlauf reist im Request mit, der Server bleibt zustandslos. Geschrieben wird ausschließlich über das bestehende `POST /api/meals`, nachdem der Nutzer die Vorschläge bestätigt hat.

**Tech Stack:** .NET 10 Minimal APIs, typisierte `HttpClient`, System.Text.Json, xUnit + `WebApplicationFactory`, React 19 + TypeScript + Vite, Docker Compose.

**Spec:** `docs/superpowers/specs/2026-09-12-ki-freitext-erfassung-design.md`

## Global Constraints

- Zielframework `net10.0`, `Nullable` und `ImplicitUsings` aktiv — wie in allen bestehenden Projekten.
- **Keine neue NuGet-Abhängigkeit und keine neue npm-Abhängigkeit.** Gemini wird über den vorhandenen typisierten `HttpClient` angesprochen, nicht über ein SDK.
- **Keine EF-Migration.** Die Funktion legt nichts in der Datenbank ab.
- Kommentare auf Deutsch, begründend („warum"), nur wo eine Entscheidung nicht selbsterklärend ist. Oberflächentexte deutsch und geduzt, wie im Rest der App.
- Der Prompt an Google enthält **ausschließlich** Esstext und Gesprächsverlauf. Keine E-Mail, keine Nutzer-ID, keine Ziele, keine Tagesbilanz. Grund steht in der Spec unter „Entscheidungen", Punkt 4.
- Kein Test geht ins Netz — weder zu Google noch zu OpenFoodFacts.
- Konfiguration: `Gemini:ApiKey`, `Gemini:Model` (Vorgabe `gemini-3.5-flash`), `Gemini:TimeoutSeconds` (Vorgabe 15). Über Umgebung erreichbar als `NUTRITRACK_Gemini__ApiKey` usw., passend zum bestehenden Präfix in `Program.cs`.
- Fehlt `Gemini:ApiKey`, **startet die Anwendung trotzdem**; nur `/api/ai/*` meldet 503. Anders als beim JWT-Schlüssel.
- Grenzen: 2000 Zeichen je Nachricht, 10 Nachrichten Verlauf, 30 Aufrufe je Stunde und Nutzer, höchstens 20 Posten je Antwort (gekappt, nicht abgelehnt).
- Nach jeder Aufgabe müssen `dotnet build NutriTrack.slnx` und `dotnet test NutriTrack.slnx` grün sein; bei Frontend-Aufgaben zusätzlich `npx tsc -b`, `npm run build` und `npm run lint`.

---

### Task 1: Verträge und Endpunktgerüst

Der Endpunkt existiert, verlangt einen Token und meldet ohne Schlüssel sauber 503. Noch kein Gemini-Aufruf.

**Files:**
- Create: `src/NutriTrack.Api/Contracts/Ai/ParseMealRequest.cs`
- Create: `src/NutriTrack.Api/Contracts/Ai/ParseMealResponse.cs`
- Create: `src/NutriTrack.Api/Endpoints/AiEndpoints.cs`
- Modify: `src/NutriTrack.Api/Program.cs` (Registrierung, direkt nach `app.MapGoalsEndpoints();`)
- Test: `tests/NutriTrack.Api.Tests/AiEndpointTests.cs`

**Interfaces:**
- Consumes: nichts aus früheren Aufgaben.
- Produces:
  - `NutriTrack.Api.Contracts.Ai.ParseMealRequest` mit `List<ChatMessage> Messages`
  - `NutriTrack.Api.Contracts.Ai.ChatMessage` mit `string Role`, `string Text`
  - `NutriTrack.Api.Contracts.Ai.ParseMealResponse` mit `string? Question`, `List<ParsedItem> Items`
  - `NutriTrack.Api.Contracts.Ai.ParsedItem` mit `string Label`, `decimal QuantityInGrams`, `string MealType`, `string Source`, `List<FoodSearchResponse> Candidates`, `NutrientEstimate? Estimate`
  - `NutriTrack.Api.Contracts.Ai.NutrientEstimate` mit `decimal Calories, Protein, Carbohydrates, Fat` und `decimal? Fiber, Sugar, SaturatedFat, Sodium`
  - `AiEndpoints.MapAiEndpoints(this WebApplication app)`

- [ ] **Step 1: Verträge anlegen**

`src/NutriTrack.Api/Contracts/Ai/ParseMealRequest.cs`:

```csharp
namespace NutriTrack.Api.Contracts.Ai;

public class ParseMealRequest
{
    public List<ChatMessage> Messages { get; set; } = [];
}

public class ChatMessage
{
    /// <summary>"user" oder "assistant". Der Verlauf ist die vollstaendige Wahrheit — der Server
    /// haelt keinen Gespraechszustand, damit es keine Sitzungen zum Aufraeumen gibt.</summary>
    public string Role { get; set; } = "user";

    public string Text { get; set; } = string.Empty;
}
```

`src/NutriTrack.Api/Contracts/Ai/ParseMealResponse.cs`:

```csharp
using NutriTrack.Api.Contracts.Food;

namespace NutriTrack.Api.Contracts.Ai;

public class ParseMealResponse
{
    /// <summary>Gesetzt, wenn die KI nachfragt. Dann ist Items leer — beides zugleich gibt es nicht.</summary>
    public string? Question { get; set; }

    public List<ParsedItem> Items { get; set; } = [];

    /// <summary>Gesetzt, wenn die Postenliste gekappt wurde. Die Oberflaeche benennt das sichtbar;
    /// stilles Abschneiden waere schlimmer als eine Ablehnung.</summary>
    public string? Notice { get; set; }
}

public class ParsedItem
{
    public string Label { get; set; } = string.Empty;
    public decimal QuantityInGrams { get; set; }
    public string MealType { get; set; } = "Snack";

    /// <summary>"openfoodfacts", wenn Candidates gefuellt ist, sonst "estimate".</summary>
    public string Source { get; set; } = "estimate";

    public List<FoodSearchResponse> Candidates { get; set; } = [];

    public NutrientEstimate? Estimate { get; set; }
}

public class NutrientEstimate
{
    public decimal Calories { get; set; }
    public decimal Protein { get; set; }
    public decimal Carbohydrates { get; set; }
    public decimal Fat { get; set; }
    public decimal? Fiber { get; set; }
    public decimal? Sugar { get; set; }
    public decimal? SaturatedFat { get; set; }
    public decimal? Sodium { get; set; }
}
```

- [ ] **Step 2: Den fehlschlagenden Test schreiben**

`tests/NutriTrack.Api.Tests/AiEndpointTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using NutriTrack.Api.Tests.Infrastructure;

namespace NutriTrack.Api.Tests;

public class AiEndpointTests(NutriTrackApiFactory factory) : IClassFixture<NutriTrackApiFactory>
{
    [Fact]
    public async Task ParseMeal_WithoutToken_ReturnsUnauthorized()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

- [ ] **Step 3: Test laufen lassen, Fehlschlag bestaetigen**

Run: `dotnet test NutriTrack.slnx --filter FullyQualifiedName~AiEndpointTests`
Expected: FAIL — 404 statt 401, weil der Endpunkt noch nicht existiert.

- [ ] **Step 4: Endpunkt anlegen**

`src/NutriTrack.Api/Endpoints/AiEndpoints.cs`:

```csharp
using Microsoft.Extensions.Options;
using NutriTrack.Api.Contracts.Ai;
using NutriTrack.Api.Services;

namespace NutriTrack.Api.Endpoints;

public static class AiEndpoints
{
    public static void MapAiEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/ai").WithTags("Ai").RequireAuthorization();

        group.MapPost("/parse-meal", (ParseMealRequest request, IConfiguration configuration) =>
        {
            // Fehlender Schluessel ist hier bewusst KEIN Startabbruch wie beim Jwt-Schluessel:
            // ein nicht eingerichtetes Zusatzfeature darf das Tagebuch nicht lahmlegen.
            if (string.IsNullOrWhiteSpace(configuration["Gemini:ApiKey"]))
                return Results.Json(
                    new { Error = "KI-Erfassung ist nicht eingerichtet (Gemini:ApiKey fehlt)." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            return Results.Ok(new ParseMealResponse());
        });
    }
}
```

In `src/NutriTrack.Api/Program.cs` direkt nach `app.MapGoalsEndpoints();` ergänzen:

```csharp
app.MapAiEndpoints();
```

- [ ] **Step 5: Test laufen lassen, Erfolg bestaetigen**

Run: `dotnet test NutriTrack.slnx --filter FullyQualifiedName~AiEndpointTests`
Expected: PASS

- [ ] **Step 6: Test fuer den fehlenden Schluessel ergaenzen**

In `AiEndpointTests.cs`:

```csharp
    [Fact]
    public async Task ParseMeal_WithoutConfiguredKey_ReturnsServiceUnavailable()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        // Die Standard-Factory setzt keinen Gemini-Schluessel. Sobald Task 2 die Factory um
        // einen Testschluessel erweitert, zieht dieser Test in eine eigene Factory-Instanz um.
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
```

- [ ] **Step 7: Bauen und alle Tests laufen lassen**

Run: `dotnet build NutriTrack.slnx && dotnet test NutriTrack.slnx`
Expected: Build 0 Fehler, alle Tests grün (bisherige 72 + 2 neue).

- [ ] **Step 8: Commit**

```bash
git add src/NutriTrack.Api/Contracts/Ai src/NutriTrack.Api/Endpoints/AiEndpoints.cs src/NutriTrack.Api/Program.cs tests/NutriTrack.Api.Tests/AiEndpointTests.cs
git commit -m "Add AI parse-meal endpoint skeleton with contracts"
```

---

### Task 2: Antwortform von Gemini festnageln und GeminiService bauen

Googles Doku zeigt die **Anfrage** (`/v1beta/interactions`, `response_format`), nicht die Form der **Antwort**. Die wird zuerst an der echten API gemessen, dann implementiert. Nichts hier wird geraten.

**Files:**
- Create: `src/NutriTrack.Api/Services/GeminiService.cs`
- Create: `tests/NutriTrack.Api.Tests/Infrastructure/StubGeminiHandler.cs`
- Modify: `tests/NutriTrack.Api.Tests/Infrastructure/NutriTrackApiFactory.cs`
- Modify: `src/NutriTrack.Api/Program.cs` (HttpClient-Registrierung)
- Test: `tests/NutriTrack.Api.Tests/GeminiServiceTests.cs`

**Interfaces:**
- Consumes: `ParseMealResponse`, `ParsedItem`, `NutrientEstimate`, `ChatMessage` aus Task 1.
- Produces:
  - `NutriTrack.Api.Services.GeminiService` mit `Task<GeminiParseResult> ParseAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)`
  - `NutriTrack.Api.Services.GeminiParseResult` mit `string? Question`, `List<GeminiItem> Items`
  - `NutriTrack.Api.Services.GeminiItem` mit `string SearchTerm`, `string Label`, `decimal QuantityInGrams`, `string MealType`, `NutrientEstimate Estimate`
  - `NutriTrack.Api.Services.GeminiUnavailableException`, `GeminiQuotaException`, `GeminiMalformedResponseException`
  - `NutriTrackApiFactory.GeminiResponder` (Property, `Func<HttpRequestMessage, HttpResponseMessage>`)

- [ ] **Step 1: Antwortform an der echten API messen**

Einmalig von Hand, mit dem Schlüssel aus Google AI Studio. **Nicht in einen Test gießen** — dieser Aufruf geht ins Netz und dient nur dazu, die Form zu sehen:

```bash
curl -s -X POST "https://generativelanguage.googleapis.com/v1beta/interactions" \
  -H "x-goog-api-key: $GEMINI_API_KEY" \
  -H 'Content-Type: application/json' \
  -d '{
    "model": "gemini-3.5-flash",
    "input": "Zerlege: 2 Broetchen mit Gouda. Antworte im vorgegebenen Schema.",
    "response_format": {
      "type": "text",
      "mime_type": "application/json",
      "schema": {
        "type": "object",
        "properties": {
          "question": {"type": "string"},
          "items": {"type": "array", "items": {
            "type": "object",
            "properties": {
              "searchTerm": {"type": "string"},
              "label": {"type": "string"},
              "quantityInGrams": {"type": "number"},
              "mealType": {"type": "string"}
            },
            "required": ["searchTerm", "label", "quantityInGrams", "mealType"]
          }}
        },
        "required": ["items"]
      }
    }
  }' | tee /tmp/gemini-probe.json
```

Notiere aus der Antwort: **wo genau der JSON-Text steckt** (Feldpfad), und ob das Modell im Freikontingent erlaubt ist. Schlägt der Aufruf mit 404 auf das Modell fehl, probiere `gemini-3.5-flash-lite` und `gemini-3.1-flash-lite` und nimm das erste, das antwortet — das ist der Wert für `Gemini:Model`.

Schreibe den gemessenen Feldpfad als Kommentar an `ExtractPayload` in Schritt 4. Die gesamte Kenntnis über Googles Antwortumschlag lebt in dieser einen Methode; ändert Google das Format, ist das die einzige Stelle.

- [ ] **Step 2: Stub-Handler und Factory-Haken anlegen**

`tests/NutriTrack.Api.Tests/Infrastructure/StubGeminiHandler.cs`:

```csharp
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
    /// <summary>Antwortumschlag wie von der echten API gemessen (siehe Plan, Task 2 Schritt 1).
    /// Der Platzhalter wird beim Implementieren durch die tatsaechliche Huelle ersetzt.</summary>
    public static HttpResponseMessage Payload(string innerJson)
    {
        var escaped = System.Text.Json.JsonSerializer.Serialize(innerJson);
        var envelope = $$"""
        { "output": [ { "content": [ { "type": "text", "text": {{escaped}} } ] } ] }
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
```

In `NutriTrackApiFactory.cs` ergänzen — Property plus Registrierung in `ConfigureTestServices`:

```csharp
    /// <summary>Antwortverhalten des Gemini-Stubs. Je Testklasse eine eigene Factory-Instanz,
    /// deshalb ist eine veraenderbare Property hier gefahrlos.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage> GeminiResponder { get; set; } =
        _ => StubGeminiHandler.Payload("""{"items":[]}""");
```

und in `ConfigureTestServices`, neben der bestehenden OpenFoodFacts-Zeile:

```csharp
            services.AddHttpClient<GeminiService>()
                .ConfigurePrimaryHttpMessageHandler(() => new StubGeminiHandler(request => GeminiResponder(request)));
```

sowie bei den übrigen `UseSetting`-Zeilen:

```csharp
        builder.UseSetting("Gemini:ApiKey", "test-key");
        builder.UseSetting("Gemini:Model", "gemini-3.5-flash");
```

- [ ] **Step 3: Den fehlschlagenden Test schreiben**

`tests/NutriTrack.Api.Tests/GeminiServiceTests.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;
using NutriTrack.Api.Contracts.Ai;
using NutriTrack.Api.Services;
using NutriTrack.Api.Tests.Infrastructure;

namespace NutriTrack.Api.Tests;

public class GeminiServiceTests(NutriTrackApiFactory factory) : IClassFixture<NutriTrackApiFactory>
{
    private GeminiService Service()
    {
        factory.CreateClient();          // erzwingt den Hochlauf des TestServers
        return factory.Services.GetRequiredService<GeminiService>();
    }

    [Fact]
    public async Task ParseAsync_WithItems_ReturnsParsedItems()
    {
        factory.GeminiResponder = _ => StubGeminiHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Weizenbroetchen", "label": "Weizenbroetchen",
              "quantityInGrams": 120, "mealType": "Breakfast",
              "estimate": { "calories": 265, "protein": 9, "carbohydrates": 49, "fat": 3.2 } }
          ]
        }
        """);

        var result = await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Broetchen" }],
            CancellationToken.None);

        Assert.Null(result.Question);
        var item = Assert.Single(result.Items);
        Assert.Equal("Weizenbroetchen", item.SearchTerm);
        Assert.Equal(120m, item.QuantityInGrams);
        Assert.Equal("Breakfast", item.MealType);
        Assert.Equal(265m, item.Estimate.Calories);
    }

    [Fact]
    public async Task ParseAsync_WithQuestion_ReturnsQuestionAndNoItems()
    {
        factory.GeminiResponder = _ => StubGeminiHandler.Payload("""
        { "question": "Wie gross war die Portion Reis?", "items": [] }
        """);

        var result = await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "Reis mit Haehnchen" }],
            CancellationToken.None);

        Assert.Equal("Wie gross war die Portion Reis?", result.Question);
        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task ParseAsync_SendsOnlyMealTextToGoogle()
    {
        string? body = null;
        factory.GeminiResponder = request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubGeminiHandler.Payload("""{"items":[]}""");
        };

        await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "zwei Broetchen" }],
            CancellationToken.None);

        Assert.NotNull(body);
        Assert.Contains("zwei Broetchen", body);
        // Kein Identitaetsmerkmal darf den Rechner verlassen — siehe Spec, Entscheidung 4.
        Assert.DoesNotContain("@", body);
        Assert.DoesNotContain("userId", body, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 4: Test laufen lassen, Fehlschlag bestaetigen**

Run: `dotnet test NutriTrack.slnx --filter FullyQualifiedName~GeminiServiceTests`
Expected: FAIL — `GeminiService` existiert nicht.

- [ ] **Step 5: GeminiService implementieren**

`src/NutriTrack.Api/Services/GeminiService.cs`:

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NutriTrack.Api.Contracts.Ai;

namespace NutriTrack.Api.Services;

/// <summary>Google ist erreichbar, aber nicht nutzbar (Timeout, Netz, 5xx).</summary>
public class GeminiUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Kontingent erschoepft (429).</summary>
public class GeminiQuotaException(string message) : Exception(message);

/// <summary>Antwort kam an, passt aber nicht zum erzwungenen Schema.</summary>
public class GeminiMalformedResponseException(string message) : Exception(message);

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

    [JsonPropertyName("quantityInGrams")]
    public decimal QuantityInGrams { get; set; }

    [JsonPropertyName("mealType")]
    public string MealType { get; set; } = "Snack";

    [JsonPropertyName("estimate")]
    public NutrientEstimate Estimate { get; set; } = new();
}

public class GeminiService(HttpClient httpClient, IConfiguration configuration, ILogger<GeminiService> logger)
{
    private const string Endpoint = "https://generativelanguage.googleapis.com/v1beta/interactions";

    // Die Grenze zwischen Nachfragen und Annehmen entscheidet, ob das Feature im Alltag taugt:
    // zu viele Rueckfragen sind laestiger als die bestehende Suche.
    private const string SystemInstruction = """
        Du zerlegst deutschsprachige Beschreibungen von Mahlzeiten in einzelne Posten.
        Fuer jeden Posten lieferst du: searchTerm (kurzer Suchbegriff fuer eine
        Lebensmitteldatenbank, ohne Mengenangabe), label (lesbarer Name), quantityInGrams
        (Menge in Gramm, Fluessigkeiten in Milliliter gleich Gramm), mealType (genau einer von
        Breakfast, Lunch, Dinner, Snack) und estimate (geschaetzte Naehrwerte je 100 g).
        Rechne Haushaltsmasse um: eine Scheibe Kaese etwa 30 g, eine Tasse Kaffee etwa 200 ml,
        ein Broetchen etwa 60 g.
        Fehlt eine Angabe, die den Naehrwert deutlich veraendert, stelle GENAU EINE kurze
        Rueckfrage im Feld question und lasse items leer. Bei Kleinigkeiten nimm den ueblichen
        Wert an, statt nachzufragen.
        Antworte ausschliesslich im vorgegebenen Schema.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<GeminiParseResult> ParseAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var apiKey = configuration["Gemini:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new GeminiUnavailableException("Gemini:ApiKey fehlt.");

        var model = configuration["Gemini:Model"] is { Length: > 0 } configured
            ? configured
            : "gemini-3.5-flash";

        var transcript = new StringBuilder(SystemInstruction).AppendLine().AppendLine();
        foreach (var message in messages)
            transcript.AppendLine($"{(message.Role == "assistant" ? "Rueckfrage" : "Nutzer")}: {message.Text}");

        var payload = new
        {
            model,
            input = transcript.ToString(),
            response_format = new
            {
                type = "text",
                mime_type = "application/json",
                schema = ResponseSchema
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-goog-api-key", apiKey);

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(request, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Timeout des HttpClient, nicht Abbruch durch den Aufrufer.
            throw new GeminiUnavailableException("Gemini hat nicht rechtzeitig geantwortet.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new GeminiUnavailableException("Gemini ist nicht erreichbar.", ex);
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new GeminiQuotaException("Kontingent erschoepft.");

        if (!response.IsSuccessStatusCode)
            throw new GeminiUnavailableException($"Gemini antwortete mit {(int)response.StatusCode}.");

        var body = await response.Content.ReadAsStringAsync(ct);
        var inner = ExtractPayload(body);

        try
        {
            return JsonSerializer.Deserialize<GeminiParseResult>(inner, JsonOptions)
                   ?? throw new GeminiMalformedResponseException("Leere Antwort.");
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Gemini-Antwort passt nicht zum Schema.");
            throw new GeminiMalformedResponseException("Antwort passt nicht zum Schema.");
        }
    }

    /// <summary>
    /// Schaelt den JSON-Text aus Googles Antwortumschlag. DER FELDPFAD IST AN DER ECHTEN API
    /// GEMESSEN (siehe Plan, Task 2 Schritt 1) — beim Implementieren gegen die Probe abgleichen.
    /// Die gesamte Kenntnis ueber den Umschlag steckt hier: aendert Google das Format, ist dies
    /// die einzige Stelle, die nachzieht.
    /// </summary>
    private static string ExtractPayload(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement
                .GetProperty("output")[0]
                .GetProperty("content")[0]
                .GetProperty("text")
                .GetString() ?? throw new GeminiMalformedResponseException("Kein Textteil in der Antwort.");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or IndexOutOfRangeException or InvalidOperationException)
        {
            throw new GeminiMalformedResponseException("Unerwarteter Antwortumschlag.");
        }
    }

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
                        quantityInGrams = new { type = "number" },
                        mealType = new { type = "string" },
                        estimate = new
                        {
                            type = "object",
                            properties = new
                            {
                                calories = new { type = "number" },
                                protein = new { type = "number" },
                                carbohydrates = new { type = "number" },
                                fat = new { type = "number" },
                                fiber = new { type = "number" },
                                sugar = new { type = "number" },
                                saturatedFat = new { type = "number" },
                                sodium = new { type = "number" }
                            },
                            required = new[] { "calories", "protein", "carbohydrates", "fat" }
                        }
                    },
                    required = new[] { "searchTerm", "label", "quantityInGrams", "mealType", "estimate" }
                }
            }
        },
        required = new[] { "items" }
    };
}
```

In `Program.cs` neben der bestehenden `AddHttpClient<OpenFoodFactsService>`-Registrierung:

```csharp
builder.Services.AddHttpClient<GeminiService>(client =>
{
    // Ohne Deckel wartet der Nutzer im Zweifel 100 Sekunden auf eine Suche, die schon tot ist.
    var seconds = builder.Configuration.GetValue("Gemini:TimeoutSeconds", 15);
    client.Timeout = TimeSpan.FromSeconds(seconds);
});
```

- [ ] **Step 6: Tests laufen lassen**

Run: `dotnet test NutriTrack.slnx --filter FullyQualifiedName~GeminiServiceTests`
Expected: PASS (3 Tests)

- [ ] **Step 7: Commit**

```bash
git add src/NutriTrack.Api/Services/GeminiService.cs src/NutriTrack.Api/Program.cs tests/NutriTrack.Api.Tests/GeminiServiceTests.cs tests/NutriTrack.Api.Tests/Infrastructure/
git commit -m "Add GeminiService with enforced JSON schema"
```

---

### Task 3: AiMealAssistant — Posten mit OpenFoodFacts-Kandidaten

**Files:**
- Create: `src/NutriTrack.Api/Services/AiMealAssistant.cs`
- Modify: `src/NutriTrack.Api/Endpoints/AiEndpoints.cs`
- Modify: `src/NutriTrack.Api/Program.cs` (`AddScoped<AiMealAssistant>()`)
- Test: `tests/NutriTrack.Api.Tests/AiEndpointTests.cs`

**Interfaces:**
- Consumes: `GeminiService.ParseAsync`, `GeminiItem`, `OpenFoodFactsService.SearchAsync`, `ParseMealResponse`, `ParsedItem`.
- Produces: `AiMealAssistant` mit `Task<ParseMealResponse> ParseAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)`.

- [ ] **Step 1: Den fehlschlagenden Test schreiben**

In `AiEndpointTests.cs` ergänzen. Die Klasse braucht dafür eine eigene Factory mit Gemini-Schlüssel — ersetze die Klassendeklaration und den bestehenden 503-Test entsprechend:

```csharp
    [Fact]
    public async Task ParseMeal_WithKnownFood_ReturnsOpenFoodFactsCandidates()
    {
        factory.GeminiResponder = _ => StubGeminiHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Haferflocken", "label": "Haferflocken",
              "quantityInGrams": 80, "mealType": "Breakfast",
              "estimate": { "calories": 350, "protein": 12, "carbohydrates": 60, "fat": 6 } }
          ]
        }
        """);

        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "80 g Haferflocken" } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var parsed = await response.Content.ReadFromJsonAsync<ParseMealResponseDto>();

        Assert.NotNull(parsed);
        Assert.Null(parsed!.Question);
        var item = Assert.Single(parsed.Items);
        Assert.Equal("openfoodfacts", item.Source);
        Assert.Equal(80m, item.QuantityInGrams);
        Assert.Equal("Breakfast", item.MealType);
        // Der Stub liefert zwei Produkte, eines ohne Namen — das wird wie in FoodEndpoints verworfen.
        Assert.Single(item.Candidates);
        Assert.Equal(StubOpenFoodFactsHandler.SearchProductName, item.Candidates[0].Name);
    }
```

Dazu in `tests/NutriTrack.Api.Tests/Infrastructure/TestContracts.cs` die Lese-DTOs ergänzen:

```csharp
public class ParseMealResponseDto
{
    public string? Question { get; set; }
    public string? Notice { get; set; }
    public List<ParsedItemDto> Items { get; set; } = [];
}

public class ParsedItemDto
{
    public string Label { get; set; } = string.Empty;
    public decimal QuantityInGrams { get; set; }
    public string MealType { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public List<FoodSearchDto> Candidates { get; set; } = [];
    public NutrientEstimateDto? Estimate { get; set; }
}

public class NutrientEstimateDto
{
    public decimal Calories { get; set; }
    public decimal Protein { get; set; }
    public decimal Carbohydrates { get; set; }
    public decimal Fat { get; set; }
}
```

`FoodSearchDto` existiert bereits in `TestContracts.cs` — falls der Name dort abweicht, den vorhandenen verwenden statt einen zweiten anzulegen.

- [ ] **Step 2: Test laufen lassen, Fehlschlag bestaetigen**

Run: `dotnet test NutriTrack.slnx --filter FullyQualifiedName~ParseMeal_WithKnownFood`
Expected: FAIL — der Endpunkt liefert noch eine leere Antwort.

- [ ] **Step 3: AiMealAssistant implementieren**

`src/NutriTrack.Api/Services/AiMealAssistant.cs`:

```csharp
using NutriTrack.Api.Contracts.Ai;
using NutriTrack.Api.Contracts.Food;

namespace NutriTrack.Api.Services;

public class AiMealAssistant(GeminiService gemini, OpenFoodFactsService openFoodFacts, ILogger<AiMealAssistant> logger)
{
    private const int MaxItems = 20;
    private const int CandidatesPerItem = 3;

    public async Task<ParseMealResponse> ParseAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        var parsed = await gemini.ParseAsync(messages, ct);

        if (!string.IsNullOrWhiteSpace(parsed.Question))
            return new ParseMealResponse { Question = parsed.Question };

        var response = new ParseMealResponse();

        // Liefert das Modell mehr Posten als erlaubt, ist das SEIN Fehler und nicht der des
        // Nutzers — ein 400er waere hier die falsche Antwort. Gekappt wird sichtbar, nicht still.
        var items = parsed.Items;
        if (items.Count > MaxItems)
        {
            logger.LogWarning("Gemini lieferte {Count} Posten, gekappt auf {Max}.", items.Count, MaxItems);
            response.Notice = $"Nur die ersten {MaxItems} Posten übernommen.";
            items = items.Take(MaxItems).ToList();
        }

        foreach (var item in items)
        {
            var candidates = await SearchCandidatesAsync(item.SearchTerm, ct);

            response.Items.Add(new ParsedItem
            {
                Label = item.Label,
                QuantityInGrams = item.QuantityInGrams,
                MealType = item.MealType,
                Source = candidates.Count > 0 ? "openfoodfacts" : "estimate",
                Candidates = candidates,
                Estimate = item.Estimate
            });
        }

        return response;
    }

    private async Task<List<FoodSearchResponse>> SearchCandidatesAsync(string searchTerm, CancellationToken ct)
    {
        var products = await openFoodFacts.SearchAsync(searchTerm, page: 1, pageSize: CandidatesPerItem * 2);

        return products
            .Where(p => p.ProductName is not null)
            .Take(CandidatesPerItem)
            .Select(p => new FoodSearchResponse
            {
                Name = p.ProductName!,
                Brand = p.Brands,
                Barcode = p.Code,
                Calories = p.Nutriments?.EnergyKcal100g ?? 0,
                Protein = p.Nutriments?.Proteins100g ?? 0,
                Carbohydrates = p.Nutriments?.Carbohydrates100g ?? 0,
                Fat = p.Nutriments?.Fat100g ?? 0,
                Fiber = p.Nutriments?.Fiber100g,
                Sugar = p.Nutriments?.Sugars100g,
                SaturatedFat = p.Nutriments?.SaturatedFat100g,
                Sodium = p.Nutriments?.Sodium100g,
                VitaminA = p.Nutriments?.VitaminA100g,
                VitaminC = p.Nutriments?.VitaminC100g,
                VitaminD = p.Nutriments?.VitaminD100g,
                Calcium = p.Nutriments?.Calcium100g,
                Iron = p.Nutriments?.Iron100g,
                Potassium = p.Nutriments?.Potassium100g
            })
            .ToList();
    }
}
```

In `Program.cs` bei den übrigen `AddScoped`-Zeilen:

```csharp
builder.Services.AddScoped<AiMealAssistant>();
```

In `AiEndpoints.cs` den Rumpf ersetzen:

```csharp
        group.MapPost("/parse-meal", async (
            ParseMealRequest request,
            IConfiguration configuration,
            AiMealAssistant assistant,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(configuration["Gemini:ApiKey"]))
                return Results.Json(
                    new { Error = "KI-Erfassung ist nicht eingerichtet (Gemini:ApiKey fehlt)." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            var result = await assistant.ParseAsync(request.Messages, ct);
            return Results.Ok(result);
        });
```

- [ ] **Step 4: Tests laufen lassen**

Run: `dotnet test NutriTrack.slnx --filter FullyQualifiedName~AiEndpointTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/NutriTrack.Api tests/NutriTrack.Api.Tests
git commit -m "Add AiMealAssistant resolving items against OpenFoodFacts"
```

---

### Task 4: Schätzungs-Rückfall und Rückfrage-Pfad

**Files:**
- Test: `tests/NutriTrack.Api.Tests/AiEndpointTests.cs`
- Modify: `tests/NutriTrack.Api.Tests/Infrastructure/StubOpenFoodFactsHandler.cs` (leere Trefferliste ermöglichen)

**Interfaces:**
- Consumes: alles aus Task 3.
- Produces: keine neuen Typen — dieser Task sichert Verhalten ab, das Task 3 angelegt hat, und deckt die Lücken auf.

- [ ] **Step 1: Leere Suche im Stub ermoeglichen**

In `StubOpenFoodFactsHandler.cs` eine Konstante und einen Zweig ergänzen:

```csharp
    /// <summary>Suchbegriff, zu dem der Stub bewusst nichts findet — fuer den Schaetzungs-Rueckfall.</summary>
    public const string UnknownSearchTerm = "hausmannskost-ohne-treffer";

    private const string EmptySearchJson = """
    { "count": 0, "products": [] }
    """;
```

und in `SendAsync` vor der bestehenden Suchzweig-Auswertung:

```csharp
        if (url.Contains("/cgi/search.pl", StringComparison.Ordinal)
            && url.Contains(Uri.EscapeDataString(UnknownSearchTerm), StringComparison.Ordinal))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(EmptySearchJson, Encoding.UTF8, "application/json")
            });
        }
```

- [ ] **Step 2: Die fehlschlagenden Tests schreiben**

```csharp
    [Fact]
    public async Task ParseMeal_WhenSearchFindsNothing_FallsBackToEstimate()
    {
        factory.GeminiResponder = _ => StubGeminiHandler.Payload($$"""
        {
          "items": [
            { "searchTerm": "{{StubOpenFoodFactsHandler.UnknownSearchTerm}}",
              "label": "Omas Linsensuppe", "quantityInGrams": 350, "mealType": "Lunch",
              "estimate": { "calories": 92, "protein": 5.4, "carbohydrates": 12.1, "fat": 2.3 } }
          ]
        }
        """);

        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Teller Omas Linsensuppe" } }
        });

        var parsed = await response.Content.ReadFromJsonAsync<ParseMealResponseDto>();
        var item = Assert.Single(parsed!.Items);

        Assert.Equal("estimate", item.Source);
        Assert.Empty(item.Candidates);
        Assert.NotNull(item.Estimate);
        Assert.Equal(92m, item.Estimate!.Calories);
    }

    [Fact]
    public async Task ParseMeal_WithQuestion_ReturnsQuestionAndNoItems()
    {
        factory.GeminiResponder = _ => StubGeminiHandler.Payload("""
        { "question": "Wie gross war die Portion Reis?", "items": [] }
        """);

        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "Reis mit Haehnchen" } }
        });

        var parsed = await response.Content.ReadFromJsonAsync<ParseMealResponseDto>();

        Assert.Equal("Wie gross war die Portion Reis?", parsed!.Question);
        Assert.Empty(parsed.Items);
    }

    [Fact]
    public async Task ParseMeal_WithMoreThanTwentyItems_CapsAndSaysSo()
    {
        var items = string.Join(",", Enumerable.Range(0, 25).Select(i => $$"""
        { "searchTerm": "{{StubOpenFoodFactsHandler.UnknownSearchTerm}}", "label": "Posten {{i}}",
          "quantityInGrams": 10, "mealType": "Snack",
          "estimate": { "calories": 10, "protein": 1, "carbohydrates": 1, "fat": 1 } }
        """));

        factory.GeminiResponder = _ => StubGeminiHandler.Payload($$"""{ "items": [{{items}}] }""");

        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein sehr langes Buffet" } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var parsed = await response.Content.ReadFromJsonAsync<ParseMealResponseDto>();

        Assert.Equal(20, parsed!.Items.Count);
        Assert.NotNull(parsed.Notice);
    }
```

- [ ] **Step 3: Tests laufen lassen**

Run: `dotnet test NutriTrack.slnx --filter FullyQualifiedName~AiEndpointTests`
Expected: PASS, ohne Änderung am Produktivcode — Task 3 hat diese Pfade bereits gebaut. Schlägt einer fehl, ist der Produktivcode falsch, nicht der Test: beheben, nicht abschwächen.

- [ ] **Step 4: Commit**

```bash
git add tests/NutriTrack.Api.Tests
git commit -m "Cover estimate fallback, question path and item cap"
```

---

### Task 5: Fehlerabbildung auf Statuscodes

**Files:**
- Modify: `src/NutriTrack.Api/Endpoints/AiEndpoints.cs`
- Test: `tests/NutriTrack.Api.Tests/AiEndpointTests.cs`

**Interfaces:**
- Consumes: `GeminiUnavailableException`, `GeminiQuotaException`, `GeminiMalformedResponseException` aus Task 2.
- Produces: keine neuen Typen.

- [ ] **Step 1: Die fehlschlagenden Tests schreiben**

```csharp
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable)]
    public async Task ParseMeal_WhenGoogleFails_MapsToExpectedStatus(HttpStatusCode googleStatus, HttpStatusCode expected)
    {
        factory.GeminiResponder = _ => StubGeminiHandler.Status(googleStatus);

        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task ParseMeal_WithMalformedJson_ReturnsBadGateway()
    {
        factory.GeminiResponder = _ => StubGeminiHandler.Payload("das ist kein JSON");

        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }
```

- [ ] **Step 2: Tests laufen lassen, Fehlschlag bestaetigen**

Run: `dotnet test NutriTrack.slnx --filter FullyQualifiedName~ParseMeal_When`
Expected: FAIL — unbehandelte Exception ergibt 500.

- [ ] **Step 3: Fehlerabbildung einbauen**

In `AiEndpoints.cs` den Aufruf des Assistenten umschließen:

```csharp
            try
            {
                var result = await assistant.ParseAsync(request.Messages, ct);
                return Results.Ok(result);
            }
            catch (GeminiQuotaException)
            {
                return Results.Json(
                    new { Error = "Das KI-Kontingent ist erschöpft. Versuche es später noch einmal." },
                    statusCode: StatusCodes.Status429TooManyRequests);
            }
            catch (GeminiMalformedResponseException)
            {
                // Ein Wiederholungsversuch steckt bereits im Assistenten; kommt es hier an,
                // hat auch der zweite Anlauf Unsinn geliefert.
                return Results.Json(
                    new { Error = "Die KI hat unverständlich geantwortet. Formuliere es bitte anders." },
                    statusCode: StatusCodes.Status502BadGateway);
            }
            catch (GeminiUnavailableException)
            {
                return Results.Json(
                    new { Error = "Die KI ist gerade nicht erreichbar." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
```

- [ ] **Step 4: Wiederholungsversuch bei kaputtem JSON einbauen**

In `AiMealAssistant.ParseAsync` den Gemini-Aufruf ersetzen:

```csharp
        GeminiParseResult parsed;
        try
        {
            parsed = await gemini.ParseAsync(messages, ct);
        }
        catch (GeminiMalformedResponseException)
        {
            // Genau ein zweiter Anlauf: Modelle straucheln gelegentlich einmalig am Schema.
            // Mehr Versuche kosten Kontingent und Wartezeit, ohne die Trefferquote zu heben.
            logger.LogWarning("Gemini-Antwort unbrauchbar, ein Wiederholungsversuch.");
            parsed = await gemini.ParseAsync(messages, ct);
        }
```

- [ ] **Step 5: Tests laufen lassen**

Run: `dotnet test NutriTrack.slnx --filter FullyQualifiedName~AiEndpointTests`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/NutriTrack.Api tests/NutriTrack.Api.Tests
git commit -m "Map Gemini failures to 429, 502 and 503"
```

---

### Task 6: Eingabegrenzen und Aufrufgrenze

**Files:**
- Create: `src/NutriTrack.Api/Services/AiRateLimiter.cs`
- Modify: `src/NutriTrack.Api/Endpoints/AiEndpoints.cs`
- Modify: `src/NutriTrack.Api/Program.cs` (`AddSingleton<AiRateLimiter>()`)
- Test: `tests/NutriTrack.Api.Tests/AiEndpointTests.cs`

**Interfaces:**
- Consumes: nichts Neues.
- Produces: `AiRateLimiter` mit `bool TryAcquire(string userId)`.

- [ ] **Step 1: Die fehlschlagenden Tests schreiben**

```csharp
    [Fact]
    public async Task ParseMeal_WithOverlongMessage_ReturnsBadRequest()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = new string('a', 2001) } }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ParseMeal_WithTooManyMessages_ReturnsBadRequest()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var messages = Enumerable.Range(0, 11).Select(i => new { role = "user", text = $"Nachricht {i}" }).ToArray();
        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new { messages });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ParseMeal_WithEmptyMessages_ReturnsBadRequest()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new { messages = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
```

- [ ] **Step 2: Tests laufen lassen, Fehlschlag bestaetigen**

Run: `dotnet test NutriTrack.slnx --filter FullyQualifiedName~ParseMeal_With`
Expected: FAIL — die Grenzen greifen noch nicht.

- [ ] **Step 3: Grenzen und Begrenzer implementieren**

`src/NutriTrack.Api/Services/AiRateLimiter.cs`:

```csharp
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
```

In `Program.cs`:

```csharp
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AiRateLimiter>();
```

In `AiEndpoints.cs` vor dem Assistenten-Aufruf, mit `ClaimsPrincipal user` und `AiRateLimiter limiter` in der Signatur:

```csharp
            const int MaxMessages = 10;
            const int MaxMessageLength = 2000;

            if (request.Messages.Count is 0 or > MaxMessages)
                return Results.BadRequest(new { Error = $"Bitte 1 bis {MaxMessages} Nachrichten senden." });

            if (request.Messages.Any(m => string.IsNullOrWhiteSpace(m.Text) || m.Text.Length > MaxMessageLength))
                return Results.BadRequest(new { Error = $"Jede Nachricht muss 1 bis {MaxMessageLength} Zeichen haben." });

            var userId = user.FindFirst(ClaimTypes.NameIdentifier)!.Value;
            if (!limiter.TryAcquire(userId))
                return Results.Json(
                    new { Error = "Zu viele KI-Anfragen in der letzten Stunde. Versuche es später noch einmal." },
                    statusCode: StatusCodes.Status429TooManyRequests);
```

Dazu `using System.Security.Claims;` ergänzen.

- [ ] **Step 4: Tests laufen lassen**

Run: `dotnet test NutriTrack.slnx --filter FullyQualifiedName~AiEndpointTests`
Expected: PASS

- [ ] **Step 5: Aufrufgrenze absichern**

```csharp
    [Fact]
    public async Task ParseMeal_BeyondHourlyLimit_ReturnsTooManyRequests()
    {
        factory.GeminiResponder = _ => StubGeminiHandler.Payload("""{"items":[]}""");
        var (client, _, _) = await factory.CreateUserAsync();

        object Body() => new { messages = new[] { new { role = "user", text = "ein Apfel" } } };

        for (var i = 0; i < 30; i++)
        {
            var ok = await client.PostAsJsonAsync("/api/ai/parse-meal", Body());
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var blocked = await client.PostAsJsonAsync("/api/ai/parse-meal", Body());
        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
    }
```

- [ ] **Step 6: Test laufen lassen und committen**

Run: `dotnet build NutriTrack.slnx && dotnet test NutriTrack.slnx`
Expected: alles grün.

```bash
git add src/NutriTrack.Api tests/NutriTrack.Api.Tests
git commit -m "Add input limits and hourly rate limit for AI endpoint"
```

---

### Task 7: OpenFoodFactsService härten

Der Dienst hat kein try/catch. Ein Ausfall erreicht den Nutzer heute als 500 mit leerem Body — beim ersten Testlauf der ausgelieferten Anwendung genau so passiert. Der KI-Pfad hängt zusätzlich daran, dass „nichts gefunden" von „kaputt" unterscheidbar bleibt.

**Files:**
- Modify: `src/NutriTrack.Api/Services/OpenFoodFactsService.cs`
- Modify: `src/NutriTrack.Api/Endpoints/FoodEndpoints.cs`
- Test: `tests/NutriTrack.Api.Tests/FoodEndpointTests.cs`

**Interfaces:**
- Consumes: nichts Neues.
- Produces: `OpenFoodFactsUnavailableException`; `SearchAsync` und `GetByBarcodeAsync` werfen sie statt roher `HttpRequestException`.

- [ ] **Step 1: Ausfall im Stub ermoeglichen**

`StubOpenFoodFactsHandler` bekommt denselben Haken wie der Gemini-Stub — ein `Func`, den die Factory setzt. Ergänze in `NutriTrackApiFactory`:

```csharp
    /// <summary>Null bedeutet: normales Stub-Verhalten. Gesetzt: diese Antwort fuer jede Anfrage.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage>? OpenFoodFactsResponder { get; set; }
```

und reiche sie in den Handler-Konstruktor durch.

- [ ] **Step 2: Den fehlschlagenden Test schreiben**

```csharp
    [Fact]
    public async Task Search_WhenOpenFoodFactsIsDown_ReturnsServiceUnavailable()
    {
        factory.OpenFoodFactsResponder = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);

        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.GetAsync("/api/food/search?query=hafer");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
```

- [ ] **Step 3: Test laufen lassen, Fehlschlag bestaetigen**

Run: `dotnet test NutriTrack.slnx --filter FullyQualifiedName~Search_WhenOpenFoodFactsIsDown`
Expected: FAIL — 500 statt 503.

- [ ] **Step 4: Dienst und Endpunkte haerten**

In `OpenFoodFactsService.cs`:

```csharp
public class OpenFoodFactsUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
```

und beide Methoden umschließen, hier `SearchAsync`:

```csharp
        try
        {
            var response = await httpClient.GetFromJsonAsync<OpenFoodFactsSearchResponse>(url);
            return response?.Products ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Ohne diesen Griff erreicht der Ausfall den Nutzer als 500 mit leerem Body. Wichtiger
            // noch: der KI-Pfad muss "nichts gefunden" von "Dienst kaputt" unterscheiden koennen,
            // sonst schaetzt er Naehrwerte, obwohl es echte Daten gaebe.
            logger.LogWarning(ex, "OpenFoodFacts-Suche fehlgeschlagen.");
            throw new OpenFoodFactsUnavailableException("OpenFoodFacts ist nicht erreichbar.", ex);
        }
```

Dazu `ILogger<OpenFoodFactsService> logger` in den Primärkonstruktor aufnehmen.

In `FoodEndpoints.cs` beide Endpunkte umschließen:

```csharp
            catch (OpenFoodFactsUnavailableException)
            {
                return Results.Json(
                    new { Error = "Die Lebensmitteldatenbank ist gerade nicht erreichbar." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
```

- [ ] **Step 5: Rueckfall im Assistenten absichern**

In `AiMealAssistant.SearchCandidatesAsync`:

```csharp
        try
        {
            var products = await openFoodFacts.SearchAsync(searchTerm, page: 1, pageSize: CandidatesPerItem * 2);
            // ... bestehende Projektion ...
        }
        catch (OpenFoodFactsUnavailableException ex)
        {
            // Der KI-Pfad soll nicht scheitern, nur weil die Fremddatenbank streikt — der Posten
            // faellt auf die Schaetzung zurueck und ist als solche gekennzeichnet.
            logger.LogWarning(ex, "Kandidatensuche fuer {SearchTerm} fehlgeschlagen, nutze Schaetzung.", searchTerm);
            return [];
        }
```

Dazu ein Test:

```csharp
    [Fact]
    public async Task ParseMeal_WhenOpenFoodFactsIsDown_StillReturnsEstimatedItems()
    {
        factory.OpenFoodFactsResponder = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);
        factory.GeminiResponder = _ => StubGeminiHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Apfel", "label": "Apfel", "quantityInGrams": 150, "mealType": "Snack",
              "estimate": { "calories": 52, "protein": 0.3, "carbohydrates": 14, "fat": 0.2 } }
          ]
        }
        """);

        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var parsed = await response.Content.ReadFromJsonAsync<ParseMealResponseDto>();
        Assert.Equal("estimate", Assert.Single(parsed!.Items).Source);
    }
```

- [ ] **Step 6: Alles laufen lassen und committen**

Run: `dotnet build NutriTrack.slnx && dotnet test NutriTrack.slnx`
Expected: alles grün.

```bash
git add src/NutriTrack.Api tests/NutriTrack.Api.Tests
git commit -m "Harden OpenFoodFactsService against outages"
```

---

### Task 8: Frontend — Client und Gesprächsseite

**Files:**
- Create: `NutriTrack.Web/src/api/ai.ts`
- Create: `NutriTrack.Web/src/pages/AiEntryPage.tsx`
- Modify: `NutriTrack.Web/src/App.tsx` (Route `/ai`)
- Modify: `NutriTrack.Web/src/components/Layout.tsx` (Navigationslink)
- Modify: `NutriTrack.Web/src/App.css` (Klassen für Verlauf und Maske)

**Interfaces:**
- Consumes: `POST /api/ai/parse-meal` aus Task 1–6; `apiErrorMessage` aus `src/api/errors.ts`; `mealsApi.create` aus `src/api/meals.ts`.
- Produces: `aiApi.parseMeal(messages)`, Typen `ChatMessage`, `ParsedItem`, `ParseMealResponse`.

- [ ] **Step 1: API-Client anlegen**

`NutriTrack.Web/src/api/ai.ts`:

```typescript
import client from './client';
import type { FoodItem } from './food';

export interface ChatMessage {
  role: 'user' | 'assistant';
  text: string;
}

export interface NutrientEstimate {
  calories: number;
  protein: number;
  carbohydrates: number;
  fat: number;
  fiber?: number | null;
  sugar?: number | null;
  saturatedFat?: number | null;
  sodium?: number | null;
}

export interface ParsedItem {
  label: string;
  quantityInGrams: number;
  mealType: string;
  source: 'openfoodfacts' | 'estimate';
  candidates: FoodItem[];
  estimate: NutrientEstimate | null;
}

export interface ParseMealResponse {
  question: string | null;
  notice: string | null;
  items: ParsedItem[];
}

export const aiApi = {
  parseMeal: (messages: ChatMessage[]) =>
    client.post<ParseMealResponse>('/api/ai/parse-meal', { messages }),
};
```

Weicht der Name des Lebensmittel-Typs in `src/api/food.ts` von `FoodItem` ab, den dort vorhandenen importieren statt einen zweiten anzulegen.

- [ ] **Step 2: Seite anlegen**

`NutriTrack.Web/src/pages/AiEntryPage.tsx` — Gesprächsverlauf, Eingabefeld, Rückfragebehandlung. Die Bestätigungsmaske folgt in Task 9; bis dahin werden die Posten als Liste angezeigt.

```tsx
import { useState } from 'react';
import { aiApi, type ChatMessage, type ParsedItem } from '../api/ai';
import { apiErrorMessage } from '../api/errors';

export default function AiEntryPage() {
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [input, setInput] = useState('');
  const [items, setItems] = useState<ParsedItem[]>([]);
  const [notice, setNotice] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);

  const send = async (event: React.FormEvent) => {
    event.preventDefault();
    const text = input.trim();
    if (!text || loading) return;

    const history: ChatMessage[] = [...messages, { role: 'user', text }];
    setMessages(history);
    setInput('');
    setError(null);
    setLoading(true);

    try {
      const res = await aiApi.parseMeal(history);
      setNotice(res.data.notice);

      if (res.data.question) {
        setMessages([...history, { role: 'assistant', text: res.data.question }]);
        setItems([]);
      } else {
        setItems(res.data.items);
      }
    } catch (err) {
      setError(apiErrorMessage(err));
    } finally {
      setLoading(false);
    }
  };

  return (
    <div className="ai-entry">
      <p className="ai-hint">
        Schreib einfach, was du gegessen hast &mdash; zum Beispiel &bdquo;2 Br&ouml;tchen mit Gouda und ein Kaffee&ldquo;.
      </p>

      <div className="ai-thread">
        {messages.map((message, index) => (
          <div key={index} className={`ai-message ai-message-${message.role}`}>
            {message.text}
          </div>
        ))}
        {loading && <div className="ai-message ai-message-assistant">Denkt nach&hellip;</div>}
      </div>

      {error && <div className="error">{error}</div>}
      {notice && <div className="ai-notice">{notice}</div>}

      <form onSubmit={send} className="ai-input">
        <input
          type="text"
          value={input}
          maxLength={2000}
          placeholder="Was hast du gegessen?"
          onChange={e => setInput(e.target.value)}
          disabled={loading}
        />
        <button type="submit" disabled={loading || !input.trim()}>Senden</button>
      </form>

      {items.length > 0 && (
        <ul className="ai-items">
          {items.map((item, index) => (
            <li key={index}>
              {item.label} &middot; {item.quantityInGrams} g
              {item.source === 'estimate' && <span className="ai-badge">gesch&auml;tzt</span>}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
```

- [ ] **Step 3: Route und Navigation verdrahten**

In `App.tsx` innerhalb der geschützten Layout-Route:

```tsx
            <Route path="/ai" element={<AiEntryPage />} />
```

In `Layout.tsx` neben den bestehenden Links:

```tsx
            <NavLink to="/ai">Per Text</NavLink>
```

Den tatsächlichen Link-Aufbau aus der Datei übernehmen — sie nutzt möglicherweise `Link` statt `NavLink`.

- [ ] **Step 4: Stile ergaenzen**

In `App.css` im Stil der vorhandenen Klassen ergänzen: `.ai-entry`, `.ai-thread`, `.ai-message`, `.ai-message-user`, `.ai-message-assistant`, `.ai-input`, `.ai-items`, `.ai-badge`, `.ai-notice`, `.ai-hint`. Dieselben CSS-Variablen wie der Rest der Datei, keine neuen Farben erfinden.

- [ ] **Step 5: Bauen und pruefen**

Run: `npx tsc -b && npm run build && npm run lint`
Expected: alles fehlerfrei.

- [ ] **Step 6: Commit**

```bash
git add src/api/ai.ts src/pages/AiEntryPage.tsx src/App.tsx src/components/Layout.tsx src/App.css
git commit -m "Add AI free-text entry page with conversation thread"
```

---

### Task 9: Bestätigungsmaske und Übernehmen

**Files:**
- Modify: `NutriTrack.Web/src/pages/AiEntryPage.tsx`
- Modify: `NutriTrack.Web/src/App.css`

**Interfaces:**
- Consumes: `ParsedItem` aus Task 8, `mealsApi.create` aus `src/api/meals.ts`.
- Produces: keine neuen exportierten Typen.

- [ ] **Step 1: Auswahlzustand einfuehren**

Die Posten bekommen einen lokalen Zustand: ausgewählt ja/nein, Menge, Mahlzeitentyp, gewählter Kandidatenindex. Beim Eintreffen neuer Posten wird er aus den Vorschlägen vorbelegt — Haken gesetzt, erster Kandidat gewählt.

```tsx
interface Draft {
  selected: boolean;
  quantityInGrams: number;
  mealType: string;
  candidateIndex: number;
}

const [drafts, setDrafts] = useState<Draft[]>([]);
```

Beim Setzen der Posten:

```tsx
        setItems(res.data.items);
        setDrafts(res.data.items.map(item => ({
          selected: true,
          quantityInGrams: item.quantityInGrams,
          mealType: item.mealType,
          candidateIndex: 0,
        })));
```

- [ ] **Step 2: Maske rendern**

Tabelle mit Haken, editierbarer Menge, Mahlzeitentyp-Auswahl (die Werte aus `src/constants/mealTypes.ts` verwenden, die dort bereits für das Dashboard liegen), Kandidaten-Dropdown falls `candidates.length > 1`, Badge „geschätzt" falls `source === 'estimate'`, und eine Summenzeile. Die kcal je Zeile werden aus dem gewählten Kandidaten beziehungsweise der Schätzung mal Menge durch 100 gerechnet.

- [ ] **Step 3: Uebernehmen implementieren**

```tsx
  const submit = async () => {
    setSaving(true);
    setError(null);
    const failed: string[] = [];

    for (let i = 0; i < items.length; i++) {
      const draft = drafts[i];
      if (!draft.selected) continue;

      const item = items[i];
      const source = item.candidates[draft.candidateIndex] ?? null;
      const nutrients = source ?? item.estimate;
      if (!nutrients) { failed.push(item.label); continue; }

      try {
        await mealsApi.create({
          foodName: source?.name ?? item.label,
          brand: source?.brand ?? null,
          barcode: source?.barcode ?? null,
          calories: nutrients.calories,
          protein: nutrients.protein,
          carbohydrates: nutrients.carbohydrates,
          fat: nutrients.fat,
          quantityInGrams: draft.quantityInGrams,
          mealType: draft.mealType,
        });
      } catch {
        // Ein fehlgeschlagener Posten darf die uebrigen nicht mitreissen: der Nutzer hat
        // moeglicherweise zehn Zeilen bestaetigt, und neun davon sind sauber durchgelaufen.
        failed.push(item.label);
      }
    }

    setSaving(false);

    if (failed.length > 0) {
      setError(`Nicht übernommen: ${failed.join(', ')}`);
      return;
    }

    navigate('/');
  };
```

Die genauen Feldnamen von `mealsApi.create` aus `src/api/meals.ts` übernehmen — die Mikronährstoffe des gewählten Kandidaten mitschicken, soweit vorhanden.

- [ ] **Step 4: Bauen und pruefen**

Run: `npx tsc -b && npm run build && npm run lint`
Expected: alles fehlerfrei.

- [ ] **Step 5: Handprobe**

Dev-Server starten, gegen die laufende Instanz anmelden, „2 Brötchen mit Gouda und ein Kaffee" eingeben und prüfen: Posten erscheinen, Mengen plausibel, geschätzte Posten markiert, Übernehmen legt die Einträge an, Dashboard zeigt sie.

- [ ] **Step 6: Commit**

```bash
git add src/pages/AiEntryPage.tsx src/App.css
git commit -m "Add confirmation table before writing AI-parsed meals"
```

---

### Task 10: Deployment-Anbindung

**Files:**
- Modify: `docker-compose.yml`
- Modify: `.env.example`
- Modify: `docs/superpowers/specs/2026-09-12-ki-freitext-erfassung-design.md` (nur falls sich beim Bauen etwas an Modell oder Antwortumschlag geändert hat)

**Interfaces:**
- Consumes: die Konfigurationsschlüssel `Gemini:ApiKey`, `Gemini:Model`, `Gemini:TimeoutSeconds`.
- Produces: keine.

- [ ] **Step 1: compose ergaenzen**

Im `environment`-Block des Dienstes `nutritrack`, bei den übrigen `NUTRITRACK_`-Werten:

```yaml
      # Ohne Schluessel laeuft die App normal weiter, nur /api/ai meldet 503 — anders als beim
      # Jwt-Schluessel, wo der Start bewusst abbricht. Ein nicht eingerichtetes Zusatzfeature
      # darf das Tagebuch nicht lahmlegen, deshalb hier KEIN `:?`.
      - Gemini__ApiKey=${NUTRITRACK_GEMINI_KEY:-}
      - Gemini__Model=${NUTRITRACK_GEMINI_MODEL:-gemini-3.5-flash}
```

- [ ] **Step 2: .env.example ergaenzen**

```bash
# Schluessel aus Google AI Studio. Fehlt er, laeuft alles ausser der KI-Erfassung.
# ACHTUNG: im kostenlosen Kontingent nutzt Google die Inhalte zur Produktverbesserung und
# menschliche Pruefer duerfen mitlesen. Deshalb schickt NutriTrack ausschliesslich den Esstext
# an Google - keine Kennung, keine Bilanz, keine Ziele.
NUTRITRACK_GEMINI_KEY=

# Optional. Welche Modelle im Freikontingent liegen, sagt Googles Doku nicht - passt das
# voreingestellte nicht zum Schluessel, hier ein anderes eintragen.
#NUTRITRACK_GEMINI_MODEL=gemini-3.5-flash
```

- [ ] **Step 3: Schluessel auf dem Server eintragen**

```bash
ssh eaksakal@192.168.188.96 "printf 'NUTRITRACK_GEMINI_KEY=%s\n' '<Schluessel>' >> /home/eaksakal/nutritrack/NutriTrack.Api/.env"
```

Die Datei nicht überschreiben — sie trägt den JWT-Schlüssel.

- [ ] **Step 4: Ausliefern und nachsehen**

```bash
bash scripts/deploy.sh
curl -s http://192.168.188.96:8082/api/health
```

Danach in der Oberfläche unter `/ai` einen Satz eingeben und prüfen, dass Posten zurückkommen.

- [ ] **Step 5: Commit**

```bash
git add docker-compose.yml .env.example
git commit -m "Wire Gemini key through compose and env template"
```

---

## Selbstprüfung des Plans

**Spec-Abdeckung.** Jede Festlegung der Spec hat eine Aufgabe: OpenFoodFacts vor Schätzung → Task 3 und 4; zustandsloser Verlauf → Task 1 (Vertrag) und 8 (Client hält den Faden); KI schreibt nicht → Task 9 nutzt `mealsApi.create`; minimaler Prompt → Task 2, Schritt 3, dritter Test prüft es ausdrücklich; Fehlertabelle → Task 5 und 7; Grenzen → Task 6; Oberfläche → Task 8 und 9; Betrieb → Task 10; OpenFoodFacts härten → Task 7.

**Bekannte Unschärfe.** Der Antwortumschlag von Google ist nicht dokumentiert; Task 2 misst ihn zuerst und hält die gesamte Kenntnis darüber in `ExtractPayload`. Der Stub in Schritt 2 trägt eine Platzhalterhülle, die nach der Messung anzupassen ist — das ist der einzige Punkt im Plan, der beim Bauen nachgezogen werden muss, und er ist bewusst auf eine Methode und eine Testhilfe eingegrenzt.

**Namensabgleich.** `ParseMealRequest.Messages`, `ChatMessage.Role/Text`, `ParsedItem.Source/Candidates/Estimate`, `GeminiItem.SearchTerm`, `AiRateLimiter.TryAcquire`, `aiApi.parseMeal` werden in allen Aufgaben gleich geschrieben. Frontend-seitig sind `FoodItem` aus `food.ts`, `apiErrorMessage` aus `errors.ts` und die Mahlzeitentypen aus `constants/mealTypes.ts` als vorhanden vorausgesetzt — jede Aufgabe, die sie nutzt, weist darauf hin, den tatsächlichen Namen aus der Datei zu übernehmen.
