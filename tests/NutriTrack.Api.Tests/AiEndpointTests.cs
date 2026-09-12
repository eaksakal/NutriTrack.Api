using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
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

    [Fact]
    public async Task ParseMeal_WithoutConfiguredKey_ReturnsServiceUnavailable()
    {
        // Eigene Factory-Instanz: die Standard-Factory setzt einen Gemini-Testschluessel, damit
        // alle uebrigen Tests den echten Pfad nehmen. Nur hier soll er fehlen.
        using var withoutKey = new WithoutGeminiKeyFactory();
        await withoutKey.ResetDatabaseAsync();

        var (client, _, _) = await withoutKey.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

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

        try
        {
            var (client, _, _) = await factory.CreateUserAsync();

            var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
            {
                messages = new[] { new { role = "user", text = "ein Apfel" } }
            });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var parsed = await response.Content.ReadFromJsonAsync<ParseMealResponseDto>();
            Assert.Equal("estimate", Assert.Single(parsed!.Items).Source);
        }
        finally
        {
            // Die Factory lebt fuer die ganze Testklasse. Bliebe der Ausfall stehen, haengte das
            // Ergebnis der uebrigen Tests an der Reihenfolge, in der xUnit sie aufruft.
            factory.OpenFoodFactsResponder = null;
        }
    }

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

    /// <summary>Startet dieselbe Anwendung, aber ohne Gemini:ApiKey — fuer den Nachweis, dass ein
    /// nicht eingerichtetes Zusatzfeature sauber 503 meldet, statt den Start zu verhindern.</summary>
    private sealed class WithoutGeminiKeyFactory : NutriTrackApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Gemini:ApiKey", null);
        }
    }
}
