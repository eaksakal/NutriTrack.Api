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
            CancellationToken.None);

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
            CancellationToken.None);

        Assert.Equal(
            "https://generativelanguage.googleapis.com/v1beta/interactions",
            seen!.RequestUri!.ToString());
        Assert.Equal("test-key", Assert.Single(seen.Headers.GetValues("x-goog-api-key")));
        Assert.Equal("2026-05-20", Assert.Single(seen.Headers.GetValues("Api-Revision")));

        using var sent = JsonDocument.Parse(body!);
        var root = sent.RootElement;
        Assert.Equal("gemini-3.5-flash", root.GetProperty("model").GetString());
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
            CancellationToken.None);

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
            CancellationToken.None);

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
                CancellationToken.None));

        Assert.Contains("steps[].content[].text", exception.Message);
        Assert.Contains("candidates", exception.Message);
        Assert.Contains("usageMetadata", exception.Message);
    }
}
