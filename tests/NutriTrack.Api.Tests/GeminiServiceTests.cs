using System.Net;
using System.Text;
using System.Text.Json;
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
            string.Empty, CancellationToken.None);

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
            string.Empty, CancellationToken.None);

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
            string.Empty, CancellationToken.None);

        Assert.NotNull(body);
        Assert.Contains("zwei Broetchen", body);
        // Kein Identitaetsmerkmal darf den Rechner verlassen — siehe Spec, Entscheidung 4.
        Assert.DoesNotContain("@", body);
        Assert.DoesNotContain("userId", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParseAsync_ReadsPayloadFromOutputTextField()
    {
        factory.GeminiResponder = _ => StubGeminiHandler.OutputTextPayload("""
        { "items": [ { "searchTerm": "Apfel", "label": "Apfel", "quantityInGrams": 150,
          "mealType": "Snack",
          "estimate": { "calories": 52, "protein": 0.3, "carbohydrates": 14, "fat": 0.2 } } ] }
        """);

        var result = await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }],
            string.Empty, CancellationToken.None);

        Assert.Equal("Apfel", Assert.Single(result.Items).Label);
    }

    [Fact]
    public async Task ParseAsync_PostsToInteractionsEndpointWithSchema()
    {
        // Haelt den Anfragevertrag fest, den die Doku der Interactions-API vorgibt. Ohne diese
        // Pruefung koennte der Pfad oder ein Feldname abdriften, ohne dass ein Test es merkte —
        // der Stub antwortet ja auf alles.
        HttpRequestMessage? seen = null;
        string? body = null;
        factory.GeminiResponder = request =>
        {
            seen = request;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubGeminiHandler.Payload("""{"items":[]}""");
        };

        await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }],
            string.Empty, CancellationToken.None);

        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/interactions",
            seen!.RequestUri!.ToString());
        Assert.Equal("test-key", Assert.Single(seen.Headers.GetValues("x-goog-api-key")));
        Assert.Equal("2026-05-20", Assert.Single(seen.Headers.GetValues("Api-Revision")));

        using var sent = JsonDocument.Parse(body!);
        var root = sent.RootElement;
        Assert.Equal("gemini-3.6-flash", root.GetProperty("model").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("input").GetString()));
        // Die Einheit von sodium muss mitreisen - ohne sie liefert das Modell Milligramm.
        Assert.Contains("NICHT Milligramm", root.GetProperty("system_instruction").GetString());
        var format = root.GetProperty("response_format");
        Assert.Equal("text", format.GetProperty("type").GetString());
        Assert.Equal("application/json", format.GetProperty("mime_type").GetString());
        Assert.Equal("object", format.GetProperty("schema").GetProperty("type").GetString());
    }

    [Fact]
    public async Task ParseAsync_WithSodiumInMilligrams_ConvertsToGramsPerHundredGrams()
    {
        // Das Modell nennt Natrium erfahrungsgemaess in mg; die Anwendung fuehrt es in Gramm je
        // 100 g. Ungeprueft stuende im Tagebuch das Tausendfache, und korrigieren liesse es sich
        // nur durch Loeschen des Eintrags.
        factory.GeminiResponder = _ => StubGeminiHandler.Payload("""
        { "items": [ { "searchTerm": "Broetchen", "label": "Broetchen", "quantityInGrams": 120,
          "mealType": "Breakfast",
          "estimate": { "calories": 265, "protein": 9, "carbohydrates": 49, "fat": 3.2,
                        "sodium": 450 } } ] }
        """);

        var result = await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Broetchen" }],
            string.Empty, CancellationToken.None);

        Assert.Equal(0.45m, Assert.Single(result.Items).Estimate.Sodium);
    }

    [Fact]
    public async Task ParseAsync_WithImpossibleMacros_ClampsToPhysicalMaximum()
    {
        factory.GeminiResponder = _ => StubGeminiHandler.Payload("""
        { "items": [ { "searchTerm": "Unfug", "label": "Unfug", "quantityInGrams": 100,
          "mealType": "Snack",
          "estimate": { "calories": 99999, "protein": 400, "carbohydrates": -5, "fat": 3 } } ] }
        """);

        var result = await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "Unfug" }],
            string.Empty, CancellationToken.None);

        var estimate = Assert.Single(result.Items).Estimate;
        Assert.Equal(900m, estimate.Calories);
        Assert.Equal(100m, estimate.Protein);
        Assert.Equal(0m, estimate.Carbohydrates);
        Assert.Equal(3m, estimate.Fat);
    }

    [Fact]
    public async Task ParseAsync_WithUnexpectedEnvelope_NamesReceivedRootFields()
    {
        // Hier kommt der Umschlag der alten generateContent-API an, nicht der der Interactions-API.
        // Die Meldung muss den erwarteten Pfad UND die tatsaechlich empfangenen Wurzelfelder
        // nennen: ein Formatwechsel bei Google zeigt damit sofort, wo der Text nun steckt.
        factory.GeminiResponder = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{ "candidates": [], "usageMetadata": {} }""", Encoding.UTF8, "application/json")
        };

        var exception = await Assert.ThrowsAsync<GeminiMalformedResponseException>(() =>
            Service().ParseAsync(
                [new ChatMessage { Role = "user", Text = "ein Apfel" }],
                string.Empty, CancellationToken.None));

        Assert.Contains("steps[].content[].text", exception.Message);
        Assert.Contains("candidates", exception.Message);
        Assert.Contains("usageMetadata", exception.Message);
    }

    [Theory]
    [InlineData("Frühstück", "Breakfast")]
    [InlineData("fruehstueck", "Breakfast")]
    [InlineData("Mittagessen", "Lunch")]
    [InlineData("Abendessen", "Dinner")]
    [InlineData("Abendbrot", "Dinner")]
    [InlineData("Zwischendurch", "Snack")]
    [InlineData("breakfast", "Breakfast")]
    [InlineData("", "Snack")]
    public void NormalizeMealType_BringtDeutscheAntwortenAufDasEnum(string eingabe, string erwartet)
        => Assert.Equal(erwartet, GeminiService.NormalizeMealType(eingabe));

    /// <summary>
    /// Der Fall aus der Handprobe gegen den echten Dienst am 2026-09-12: der Prompt ist deutsch,
    /// also antwortete das Modell mit "Frühstück" statt "Breakfast". Ungefiltert weitergereicht
    /// haette MealEndpoints den Eintrag spaeter mit 400 abgelehnt.
    /// </summary>
    [Fact]
    public async Task ParseAsync_MitDeutschemMahlzeitentyp_LiefertEnumWert()
    {
        factory.GeminiResponder = _ => StubGeminiHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Broetchen", "label": "Weizenbrötchen (2 Stück)",
              "quantityInGrams": 100, "mealType": "Frühstück",
              "estimate": { "calories": 272, "protein": 8.5, "carbohydrates": 53, "fat": 1.5,
                            "sodium": 0.55 } }
          ]
        }
        """);

        var result = await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "zwei Broetchen" }],
            string.Empty, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("Breakfast", item.MealType);
        // Natrium kam bei der Probe korrekt in Gramm; das darf die Normalisierung nicht verbiegen.
        Assert.Equal(0.55m, item.Estimate.Sodium);
    }

    /// <summary>
    /// Ebenfalls aus der Probe: vor der Modellantwort steht ein "thought"-Schritt. Wer blind
    /// steps[0] liest, bekommt eine Signatur statt des JSON.
    /// </summary>
    [Fact]
    public async Task ParseAsync_MitVorangehendemThoughtSchritt_FindetDieModellantwort()
    {
        factory.GeminiResponder = _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
              "status": "completed",
              "steps": [
                { "type": "thought", "signature": "EtESCs4SARFNMg+2PL0F6Ojk97zqxNzEdVJB" },
                { "type": "model_output", "content": [ { "type": "text", "text":
                  "{\"items\":[{\"searchTerm\":\"Apfel\",\"label\":\"Apfel\",\"quantityInGrams\":150,\"mealType\":\"Snack\",\"estimate\":{\"calories\":52,\"protein\":0.3,\"carbohydrates\":14,\"fat\":0.2}}]}"
                } ] }
              ]
            }
            """, System.Text.Encoding.UTF8, "application/json")
        };

        var result = await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }],
            string.Empty, CancellationToken.None);

        Assert.Equal("Apfel", Assert.Single(result.Items).Label);
    }

    /// <summary>
    /// Google schickt fuer die Minutengrenze denselben 429 wie fuer die Tagesgrenze. Wer nur den
    /// Statuscode liest, sagt dem Nutzer bei einer Wartezeit von 27 Sekunden, sein Kontingent sei
    /// aufgebraucht — deshalb muss die gerissene Grenze aus dem Rumpf kommen.
    /// </summary>
    [Theory]
    [InlineData("GenerateRequestsPerMinutePerProjectPerModel", GeminiQuotaScope.PerMinute)]
    [InlineData("GenerateRequestsPerDayPerProjectPerModel", GeminiQuotaScope.PerDay)]
    [InlineData("SomethingUnheardOf", GeminiQuotaScope.Unknown)]
    public async Task ParseAsync_BeiQuotaFehler_LiestDieGerisseneGrenzeAusDemRumpf(
        string quotaId, GeminiQuotaScope erwartet)
    {
        factory.GeminiResponder = _ => StubGeminiHandler.QuotaFailure(quotaId, "27s");

        var ex = await Assert.ThrowsAsync<GeminiQuotaException>(() => Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }],
            string.Empty, CancellationToken.None));

        Assert.Equal(erwartet, ex.Scope);
        Assert.Equal(TimeSpan.FromSeconds(27), ex.RetryAfter);
    }

    [Fact]
    public async Task ParseAsync_BeiQuotaFehlerOhneRetryInfo_LaesstDieWartezeitOffen()
    {
        factory.GeminiResponder = _ => StubGeminiHandler.QuotaFailure("GenerateRequestsPerDayPerProjectPerModel");

        var ex = await Assert.ThrowsAsync<GeminiQuotaException>(() => Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }],
            string.Empty, CancellationToken.None));

        Assert.Equal(GeminiQuotaScope.PerDay, ex.Scope);
        Assert.Null(ex.RetryAfter);
    }

    /// <summary>Ohne auswertbare Details bleibt der HTTP-Kopf als Quelle der Wartezeit.</summary>
    /// <summary>
    /// Der Umschlag, den die Interactions-API wirklich schickt. Die Wartezeit steht darin nur im
    /// Fliesstext ("Please retry in 39.826942774s.") - wird sie nicht gelesen, liest der Nutzer
    /// "versuche es gleich noch einmal", obwohl Google die Sekunden nennt.
    /// </summary>
    [Fact]
    public async Task ParseAsync_BeiFlachemQuotaFehler_LiestDieWartezeitAusDemText()
    {
        factory.GeminiResponder = _ => StubGeminiHandler.InteractionsQuotaFailure();

        var ex = await Assert.ThrowsAsync<GeminiQuotaException>(() => Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }],
            string.Empty, CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(39.826942774), ex.RetryAfter);
    }

    /// <summary>
    /// Auch im flachen Umschlag steht die Metrik. Nennt sie das Fenster, muss die Unterscheidung
    /// greifen - sonst haette das Nachziehen nur die halbe Arbeit getan.
    /// </summary>
    [Theory]
    [InlineData("generativelanguage.googleapis.com/generate_requests_per_model_per_day", GeminiQuotaScope.PerDay)]
    [InlineData("generativelanguage.googleapis.com/generate_requests_per_model_per_minute", GeminiQuotaScope.PerMinute)]
    [InlineData("generativelanguage.googleapis.com/generate_content_free_tier_requests", GeminiQuotaScope.Unknown)]
    public async Task ParseAsync_BeiFlachemQuotaFehler_DeutetDieMetrik(string metric, GeminiQuotaScope erwartet)
    {
        factory.GeminiResponder = _ => StubGeminiHandler.InteractionsQuotaFailure(metric);

        var ex = await Assert.ThrowsAsync<GeminiQuotaException>(() => Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }],
            string.Empty, CancellationToken.None));

        Assert.Equal(erwartet, ex.Scope);
    }

    /// <summary>
    /// Der Rumpf Zeichen fuer Zeichen so, wie ihn der Betriebsrechner am 2026-09-13 um 20:31 UTC
    /// von Google bekam. Der Test daneben baut denselben Umschlag aus Bausteinen zusammen und
    /// koennte dabei meine Annahme wiederholen statt Googles Wirklichkeit; dieser hier kann das
    /// nicht. Faellt er, hat Google das Format geaendert - und nicht wir.
    /// </summary>
    [Fact]
    public async Task ParseAsync_BeimEchtenAufgezeichnetenRumpf_LiestGrenzeUndWartezeit()
    {
        const string echterRumpf =
            """
            {"error":{"message":"You exceeded your current quota, please check your plan and billing details. For more information on this error, head to: https://ai.google.dev/gemini-api/docs/rate-limits. To monitor your current usage, head to: https://ai.dev/rate-limit. \n* Quota exceeded for metric: generativelanguage.googleapis.com/generate_content_free_tier_requests, limit: 20, model: gemini-3.5-flash\nPlease retry in 39.826942774s.","code":"too_many_requests"}}
            """;

        factory.GeminiResponder = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(echterRumpf, System.Text.Encoding.UTF8, "application/json")
        };

        var ex = await Assert.ThrowsAsync<GeminiQuotaException>(() => Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }],
            string.Empty, CancellationToken.None));

        Assert.Equal(TimeSpan.FromSeconds(39.826942774), ex.RetryAfter);

        // Diese Metrik nennt weder Minute noch Tag - dann wird auch keins von beidem behauptet.
        Assert.Equal(GeminiQuotaScope.Unknown, ex.Scope);
    }

    /// <summary>Nennt Google keine Wartezeit, wird auch keine erfunden.</summary>
    [Fact]
    public async Task ParseAsync_BeiFlachemQuotaFehlerOhneWartezeit_LaesstSieOffen()
    {
        factory.GeminiResponder = _ => StubGeminiHandler.InteractionsQuotaFailure(retryIn: null);

        var ex = await Assert.ThrowsAsync<GeminiQuotaException>(() => Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }],
            string.Empty, CancellationToken.None));

        Assert.Null(ex.RetryAfter);
        Assert.Equal(GeminiQuotaScope.Unknown, ex.Scope);
    }

    [Fact]
    public async Task ParseAsync_BeiQuotaFehlerOhneDetails_NimmtDenRetryAfterKopf()
    {
        factory.GeminiResponder = _ => StubGeminiHandler.TooManyRequestsWithRetryAfterHeader(42);

        var ex = await Assert.ThrowsAsync<GeminiQuotaException>(() => Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }],
            string.Empty, CancellationToken.None));

        Assert.Equal(GeminiQuotaScope.Unknown, ex.Scope);
        Assert.Equal(TimeSpan.FromSeconds(42), ex.RetryAfter);
    }

    [Fact]
    public async Task ParseAsync_WithHistory_PutsItInTheRequestBody()
    {
        string? body = null;
        factory.GeminiResponder = request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubGeminiHandler.Payload("""{"items":[]}""");
        };

        await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "den Rest vom Eis" }],
            "Bisher gegessen:\n[v1] gestern 21:30 Snack - Eis, Vanille (100 g)\n",
            CancellationToken.None);

        Assert.NotNull(body);
        Assert.Contains("Bisher gegessen", body);
        Assert.Contains("Eis, Vanille", body);
    }

    [Fact]
    public async Task ParseAsync_WithoutHistory_SendsNoHistoryBlock()
    {
        string? body = null;
        factory.GeminiResponder = request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubGeminiHandler.Payload("""{"items":[]}""");
        };

        await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }], string.Empty, CancellationToken.None);

        Assert.NotNull(body);
        // Nicht im gesamten Rumpf pruefen: die SystemInstruction nennt den Abschnittsnamen
        // "Bisher gegessen" IMMER, um das Format zu erklaeren, unabhaengig vom historyBlock. Die
        // eigentliche Behauptung des Tests gilt fuer das Gespraech (input), nicht fuer die feste
        // Anweisung.
        using var sent = JsonDocument.Parse(body!);
        Assert.DoesNotContain("Bisher gegessen", sent.RootElement.GetProperty("input").GetString());
    }

    [Fact]
    public async Task ParseAsync_WithSourceRef_ReadsIt()
    {
        factory.GeminiResponder = _ => StubGeminiHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Eis", "label": "Eis, Vanille", "sourceRef": " v2 ",
              "quantityInGrams": 100, "mealType": "Snack", "productKind": "generic",
              "estimate": { "calories": 200, "protein": 3, "carbohydrates": 25, "fat": 9 } }
          ]
        }
        """);

        var result = await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "den Rest vom Eis" }], string.Empty, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("v2", item.SourceRef);
    }

    [Fact]
    public async Task ParseAsync_WithoutSourceRef_LeavesItNull()
    {
        factory.GeminiResponder = _ => StubGeminiHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Apfel", "label": "Apfel", "sourceRef": "",
              "quantityInGrams": 150, "mealType": "Snack", "productKind": "generic",
              "estimate": { "calories": 52, "protein": 0.3, "carbohydrates": 14, "fat": 0.2 } }
          ]
        }
        """);

        var result = await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }], string.Empty, CancellationToken.None);

        Assert.Null(Assert.Single(result.Items).SourceRef);
    }
}
