using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NutriTrack.Api.Tests.Infrastructure;
using NutriTrack.Infrastructure.Data;

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
              "productKind": "branded",
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
              "productKind": "branded",
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
          "productKind": "branded",
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

    /// <summary>
    /// Die freundliche Meldung sagt dem Nutzer, was zu tun ist; sie sagt ihm nicht, WARUM. Ein
    /// Zeitdeckel, ein 500 von Google und ein fehlender Schluessel sehen von aussen gleich aus,
    /// verlangen aber voellig verschiedene Reaktionen. Der Grund muss also mit raus.
    /// </summary>
    [Fact]
    public async Task ParseMeal_WhenGoogleFails_NamesTheReasonInDetail()
    {
        factory.GeminiResponder = _ => StubGeminiHandler.Status(HttpStatusCode.InternalServerError);

        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var koerper = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("500", koerper.GetProperty("detail").GetString());
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
              "productKind": "branded",
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

    [Fact]
    public async Task ParseMeal_WithRepeatedSearchTerm_QueriesOpenFoodFactsOnce()
    {
        // Eigene Instanz: Bremse und Zwischenspeicher sind Singletons der Anwendung, und dieser
        // Test zaehlt Anfragen. In der geteilten Factory wuerde jeder frueher gelaufene Test
        // mitzaehlen.
        using var isolated = new NutriTrackApiFactory();
        await isolated.ResetDatabaseAsync();

        var suchen = 0;
        isolated.OpenFoodFactsResponder = request =>
        {
            if (request.RequestUri!.ToString().Contains("/cgi/search.pl", StringComparison.Ordinal))
                Interlocked.Increment(ref suchen);

            return null;   // null heisst: normales Stub-Verhalten
        };

        isolated.GeminiResponder = _ => StubGeminiHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Broetchen", "label": "Broetchen", "quantityInGrams": 60,
              "mealType": "Breakfast",
              "productKind": "branded",
              "estimate": { "calories": 265, "protein": 9, "carbohydrates": 49, "fat": 3.2 } },
            { "searchTerm": "broetchen", "label": "Noch ein Broetchen", "quantityInGrams": 60,
              "mealType": "Breakfast",
              "productKind": "branded",
              "estimate": { "calories": 265, "protein": 9, "carbohydrates": 49, "fat": 3.2 } },
            { "searchTerm": "Butter", "label": "Butter", "quantityInGrams": 10,
              "mealType": "Breakfast",
              "productKind": "branded",
              "estimate": { "calories": 717, "protein": 0.9, "carbohydrates": 0.1, "fat": 81 } }
          ]
        }
        """);

        var (client, _, _) = await isolated.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "zwei Broetchen mit Butter" } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var parsed = await response.Content.ReadFromJsonAsync<ParseMealResponseDto>();

        // Drei Posten, aber nur ZWEI verschiedene Begriffe - und "Broetchen"/"broetchen" zaehlt
        // als einer. OpenFoodFacts erlaubt nur 10 Suchen je Minute; jede gesparte zaehlt.
        Assert.Equal(3, parsed!.Items.Count);
        Assert.Equal(2, suchen);

        // Beide Broetchen-Posten haben trotzdem dieselben Kandidaten bekommen.
        Assert.Equal("openfoodfacts", parsed.Items[0].Source);
        Assert.Equal("openfoodfacts", parsed.Items[1].Source);
        Assert.Equal(parsed.Items[0].Candidates[0].Name, parsed.Items[1].Candidates[0].Name);
    }

    [Fact]
    public async Task ParseMeal_WhenSearchQuotaIsSpent_FallsBackToEstimateAndSaysSo()
    {
        using var isolated = new NutriTrackApiFactory();
        await isolated.ResetDatabaseAsync();

        var (client, _, _) = await isolated.CreateUserAsync();

        // Acht verschiedene Begriffe verbrauchen das Kontingent genau auf; der neunte findet
        // keines mehr vor und faellt auf die Schaetzung zurueck.
        string Posten(string term) => $$"""
            { "searchTerm": "{{term}}", "label": "{{term}}", "quantityInGrams": 50,
              "mealType": "Snack",
              "productKind": "branded",
              "estimate": { "calories": 100, "protein": 1, "carbohydrates": 2, "fat": 3 } }
            """;

        var ersteAcht = string.Join(",", Enumerable.Range(1, 8).Select(i => Posten($"begriff-{i}")));
        isolated.GeminiResponder = _ => StubGeminiHandler.Payload($$"""{ "items": [{{ersteAcht}}] }""");

        var aufbrauchen = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein grosses Buffet" } }
        });
        Assert.Equal(HttpStatusCode.OK, aufbrauchen.StatusCode);

        isolated.GeminiResponder = _ => StubGeminiHandler.Payload($$"""{ "items": [{{Posten("begriff-neun")}}] }""");

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "und noch ein Nachschlag" } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var parsed = await response.Content.ReadFromJsonAsync<ParseMealResponseDto>();

        var item = Assert.Single(parsed!.Items);
        Assert.Equal("estimate", item.Source);
        Assert.Empty(item.Candidates);
        Assert.NotNull(item.Estimate);

        // Der Nutzer muss erfahren, WARUM nur geschaetzt wurde - sonst wirkt es wie eine Luecke
        // in der Lebensmitteldatenbank statt wie eine Bremse bei uns.
        Assert.NotNull(parsed.Notice);
        Assert.Contains("Suchkontingent", parsed.Notice);
    }

    [Fact]
    public async Task ParseMeal_RepeatedAcrossRequests_UsesCacheInsteadOfQuota()
    {
        using var isolated = new NutriTrackApiFactory();
        await isolated.ResetDatabaseAsync();

        var suchen = 0;
        isolated.OpenFoodFactsResponder = request =>
        {
            if (request.RequestUri!.ToString().Contains("/cgi/search.pl", StringComparison.Ordinal))
                Interlocked.Increment(ref suchen);

            return null;
        };

        isolated.GeminiResponder = _ => StubGeminiHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Kaffee", "label": "Kaffee", "quantityInGrams": 200,
              "mealType": "Breakfast",
              "productKind": "branded",
              "estimate": { "calories": 2, "protein": 0.1, "carbohydrates": 0, "fat": 0 } }
          ]
        }
        """);

        var (client, _, _) = await isolated.CreateUserAsync();

        object Rumpf() => new { messages = new[] { new { role = "user", text = "ein Kaffee" } } };

        for (var i = 0; i < 5; i++)
        {
            var response = await client.PostAsJsonAsync("/api/ai/parse-meal", Rumpf());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var parsed = await response.Content.ReadFromJsonAsync<ParseMealResponseDto>();
            Assert.Equal("openfoodfacts", Assert.Single(parsed!.Items).Source);
        }

        // Fuenf Anfragen, EIN Aufruf beim Fremddienst. Ohne Zwischenspeicher waeren nach sechs
        // Eingaben dieser Art bereits mehr als die Haelfte des Minutenkontingents verbraucht.
        Assert.Equal(1, suchen);
    }

    /// <summary>
    /// Der Fall, der die Umstellung ausgeloest hat (gemessen am 2026-09-12): eine Produktdatenbank
    /// kennt zu "Spaghetti" nur TROCKENE Nudeln mit rund 360 kcal je 100 g. Gekochte haben etwa
    /// 150. Ein Treffer daraus haette den Tageswert still verdoppelt - also wird fuer
    /// Selbstgekochtes gar nicht erst gefragt.
    /// </summary>
    [Fact]
    public async Task ParseMeal_WithHomeCookedDish_NeverQueriesTheProductDatabase()
    {
        using var isolated = new NutriTrackApiFactory();
        await isolated.ResetDatabaseAsync();

        var suchen = 0;
        isolated.OpenFoodFactsResponder = request =>
        {
            if (request.RequestUri!.ToString().Contains("/cgi/search.pl", StringComparison.Ordinal))
                Interlocked.Increment(ref suchen);

            return null;
        };

        isolated.GeminiResponder = _ => StubGeminiHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Spaghetti gekocht", "label": "Spaghetti (gekocht)",
              "quantityInGrams": 250, "mealType": "Dinner", "productKind": "generic",
              "estimate": { "calories": 150, "protein": 5.5, "carbohydrates": 30, "fat": 0.9 } },
            { "searchTerm": "Bolognese Sauce", "label": "Bolognese Sauce",
              "quantityInGrams": 200, "mealType": "Dinner", "productKind": "generic",
              "estimate": { "calories": 120, "protein": 7, "carbohydrates": 6, "fat": 7.5 } }
          ]
        }
        """);

        var (client, _, _) = await isolated.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "Spaghetti Bolognese" } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var parsed = await response.Content.ReadFromJsonAsync<ParseMealResponseDto>();

        Assert.Equal(0, suchen);
        Assert.Equal(2, parsed!.Items.Count);
        Assert.All(parsed.Items, item =>
        {
            Assert.Equal("generic", item.Source);
            Assert.Empty(item.Candidates);
            Assert.NotNull(item.Estimate);
        });
        // 150 kcal je 100 g fuer gekochte Nudeln - nicht der Trockenwert von rund 360.
        Assert.Equal(150m, parsed.Items[0].Estimate!.Calories);
    }

    [Fact]
    public async Task ParseMeal_WithBrandedProduct_QueriesTheProductDatabase()
    {
        using var isolated = new NutriTrackApiFactory();
        await isolated.ResetDatabaseAsync();

        isolated.GeminiResponder = _ => StubGeminiHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Haferflocken", "label": "Kölln Zarte Haferflocken",
              "quantityInGrams": 80, "mealType": "Breakfast", "productKind": "branded",
              "estimate": { "calories": 350, "protein": 12, "carbohydrates": 60, "fat": 6 } }
          ]
        }
        """);

        var (client, _, _) = await isolated.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "80 g Kölln Zarte Haferflocken" } }
        });

        var parsed = await response.Content.ReadFromJsonAsync<ParseMealResponseDto>();
        var item = Assert.Single(parsed!.Items);

        Assert.Equal("openfoodfacts", item.Source);
        Assert.NotEmpty(item.Candidates);
    }

    /// <summary>
    /// Markenprodukt gefragt, Datenbank hat nichts: DAS ist ein Rueckfall und heisst deshalb
    /// "estimate" - im Unterschied zu "generic", wo wir bewusst gar nicht erst fragen.
    /// </summary>
    [Fact]
    public async Task ParseMeal_WithBrandedProductAndNoHit_ReportsEstimateNotGeneric()
    {
        using var isolated = new NutriTrackApiFactory();
        await isolated.ResetDatabaseAsync();

        isolated.GeminiResponder = _ => StubGeminiHandler.Payload($$"""
        {
          "items": [
            { "searchTerm": "{{StubOpenFoodFactsHandler.UnknownSearchTerm}}",
              "label": "Sehr seltene Marke", "quantityInGrams": 100, "mealType": "Snack",
              "productKind": "branded",
              "estimate": { "calories": 200, "protein": 5, "carbohydrates": 20, "fat": 10 } }
          ]
        }
        """);

        var (client, _, _) = await isolated.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "irgendein Nischenprodukt" } }
        });

        var parsed = await response.Content.ReadFromJsonAsync<ParseMealResponseDto>();
        Assert.Equal("estimate", Assert.Single(parsed!.Items).Source);
    }

    /// <summary>Fehlt oder spinnt productKind, gilt "generic" - der kleinere Fehler.</summary>
    [Fact]
    public async Task ParseMeal_WithUnknownProductKind_TreatsItemAsGeneric()
    {
        using var isolated = new NutriTrackApiFactory();
        await isolated.ResetDatabaseAsync();

        var suchen = 0;
        isolated.OpenFoodFactsResponder = request =>
        {
            if (request.RequestUri!.ToString().Contains("/cgi/search.pl", StringComparison.Ordinal))
                Interlocked.Increment(ref suchen);

            return null;
        };

        isolated.GeminiResponder = _ => StubGeminiHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Haferflocken", "label": "Haferflocken", "quantityInGrams": 80,
              "mealType": "Breakfast", "productKind": "voellig-unbekannt",
              "estimate": { "calories": 372, "protein": 13, "carbohydrates": 59, "fat": 7 } }
          ]
        }
        """);

        var (client, _, _) = await isolated.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "80 g Haferflocken" } }
        });

        var parsed = await response.Content.ReadFromJsonAsync<ParseMealResponseDto>();
        Assert.Equal("generic", Assert.Single(parsed!.Items).Source);
        Assert.Equal(0, suchen);
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

    /// <summary>
    /// Abschluss-Review Befund 1: eine Instanz, die NUR NUTRITRACK_OPENROUTER_KEY gesetzt hat -
    /// .env.example bietet ihn als gleichrangige Einstellung an. Vor dem Fix verlangte
    /// AiEndpoints unbedingt Gemini:ApiKey und lieferte hier bei JEDER Erfassung 503, obwohl
    /// Handprobe und Zielvorschlag unter demselben Anbieter liefen.
    /// </summary>
    private sealed class OpenRouterOnlyFactory : NutriTrackApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Gemini:ApiKey", null);
            builder.UseSetting("Ai:Provider", "openrouter");
        }
    }

    /// <summary>
    /// Der Nachweis zu Befund 1: nicht nur "kein 503 mehr", sondern eine ECHTE Erfassung Ende zu
    /// Ende ueber den einzig konfigurierten Anbieter - die Kernfunktion, fuer die dieser ganze
    /// Branch existiert.
    /// </summary>
    [Fact]
    public async Task ParseMeal_WithOnlyOpenRouterKeyConfigured_StillParsesAMeal()
    {
        using var openRouterOnly = new OpenRouterOnlyFactory();
        await openRouterOnly.ResetDatabaseAsync();

        openRouterOnly.OpenRouterResponder = _ => StubOpenRouterHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Apfel", "label": "Apfel", "quantityInGrams": 150, "mealType": "Snack",
              "productKind": "generic",
              "estimate": { "calories": 52, "protein": 0.3, "carbohydrates": 14, "fat": 0.2 } }
          ]
        }
        """);

        var (client, _, _) = await openRouterOnly.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var parsed = await response.Content.ReadFromJsonAsync<ParseMealResponseDto>();
        Assert.Equal("generic", Assert.Single(parsed!.Items).Source);
    }

    /// <summary>Auch der Mahlzeiten-Weg darf die Minutengrenze nicht als Tagesende ausgeben.</summary>
    [Fact]
    public async Task ParseMeal_BeiMinutengrenze_SetztRetryAfterUndNenntDieWartezeit()
    {
        factory.GeminiResponder = _ =>
            StubGeminiHandler.QuotaFailure("GenerateRequestsPerMinutePerProjectPerModel", "12s");

        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("12", Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.Contains("12 Sekunden", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Die ganze Kette am echten Umschlag: Googles Fliesstext -> Wartezeit -> der Satz, den der
    /// Nutzer liest. Vorher stand dort "Versuche es gleich noch einmal", obwohl Google die
    /// Sekunden mitgeschickt hatte - "gleich" laesst offen, ob man 5 Sekunden oder 5 Minuten
    /// wartet, und wer zu frueh wiederkommt, reisst die Grenze gleich noch einmal.
    /// </summary>
    [Fact]
    public async Task ParseMeal_BeiFlachemQuotaFehler_NenntDieSekundenUndSetztRetryAfter()
    {
        factory.GeminiResponder = _ => StubGeminiHandler.InteractionsQuotaFailure(retryIn: "39.826942774s");

        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);

        // Aufgerundet: wer die Nachkommastellen abschnitte, kaeme eine Zehntelsekunde zu frueh.
        Assert.Equal("40", Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.Contains("40 Sekunden", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Legt einen Eintrag ueber den echten Schreibweg an und gibt dessen Id zurueck. Ueber die API
    /// und nicht per DbContext: so steht in der Datenbank genau das, was im Betrieb dort stuende.
    /// </summary>
    private static async Task<Guid> AnlegenAsync(
        HttpClient client, string name, decimal calories, decimal quantity, DateOnly date)
    {
        var response = await client.PostAsJsonAsync("/api/meals", new
        {
            foodName = name,
            calories,
            protein = 3m,
            carbohydrates = 25m,
            fat = 9m,
            quantityInGrams = quantity,
            mealType = "Snack",
            date
        });

        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task ParseMeal_WithSourceRef_UsesTheOriginalsNutrients()
    {
        var (client, _, _) = await factory.CreateUserAsync();
        var gestern = DateOnly.FromDateTime(DateTime.Now).AddDays(-1);
        var eisId = await AnlegenAsync(client, "Eis, Vanille", calories: 207m, quantity: 100m, date: gestern);

        // Das Modell bezieht sich auf die Zeile - und schaetzt daneben. Die Schaetzung darf nicht
        // gewinnen, sonst stuende derselbe Becher mit zwei Werten im Tagebuch.
        factory.GeminiResponder = _ => StubGeminiHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Eis", "label": "Eis", "sourceRef": "v1",
              "quantityInGrams": 100, "mealType": "Snack", "productKind": "generic",
              "estimate": { "calories": 999, "protein": 1, "carbohydrates": 1, "fat": 1 } }
          ]
        }
        """);

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "den Rest vom Eis von gestern" } }
        });

        response.EnsureSuccessStatusCode();
        var parsed = await response.Content.ReadFromJsonAsync<JsonElement>();
        var item = parsed.GetProperty("items")[0];

        Assert.Equal("history", item.GetProperty("source").GetString());
        Assert.Equal(eisId, item.GetProperty("sourceEntryId").GetGuid());
        Assert.Equal("Eis, Vanille", item.GetProperty("label").GetString());
        Assert.Equal(207m, item.GetProperty("estimate").GetProperty("calories").GetDecimal());
        Assert.Equal("gestern", item.GetProperty("sourceHint").GetString()![..7]);
    }

    [Fact]
    public async Task ParseMeal_WithInventedSourceRef_FallsBackToEstimate()
    {
        var (client, _, _) = await factory.CreateUserAsync();
        await AnlegenAsync(client, "Eis, Vanille", 207m, 100m, DateOnly.FromDateTime(DateTime.Now));

        factory.GeminiResponder = _ => StubGeminiHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Apfel", "label": "Apfel", "sourceRef": "v99",
              "quantityInGrams": 150, "mealType": "Snack", "productKind": "generic",
              "estimate": { "calories": 52, "protein": 0.3, "carbohydrates": 14, "fat": 0.2 } }
          ]
        }
        """);

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        response.EnsureSuccessStatusCode();
        var item = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0];

        Assert.Equal("generic", item.GetProperty("source").GetString());
        Assert.Equal(52m, item.GetProperty("estimate").GetProperty("calories").GetDecimal());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("sourceEntryId").ValueKind);
    }

    [Fact]
    public async Task ParseMeal_WithSourceRef_NeverReachesAnotherUsersEntry()
    {
        // Der fremde Nutzer isst zuerst - seine Eintraege sind die einzigen, die "v1" treffen
        // koennte, wenn die Aufloesung nicht am Besitzer haengt.
        var (fremd, _, _) = await factory.CreateUserAsync();
        await AnlegenAsync(fremd, "Fremdes Eis", 999m, 100m, DateOnly.FromDateTime(DateTime.Now));

        var (client, _, _) = await factory.CreateUserAsync();

        factory.GeminiResponder = _ => StubGeminiHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Eis", "label": "Eis", "sourceRef": "v1",
              "quantityInGrams": 100, "mealType": "Snack", "productKind": "generic",
              "estimate": { "calories": 200, "protein": 3, "carbohydrates": 25, "fat": 9 } }
          ]
        }
        """);

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "das Eis von gestern" } }
        });

        response.EnsureSuccessStatusCode();
        var item = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0];

        Assert.NotEqual("history", item.GetProperty("source").GetString());
        Assert.Equal(200m, item.GetProperty("estimate").GetProperty("calories").GetDecimal());
    }

    [Fact]
    public async Task ParseMeal_SendsHistoryButNeverTheUsersIdentity()
    {
        var (client, email, userId) = await factory.CreateUserAsync();
        await AnlegenAsync(client, "Eis, Vanille", 207m, 100m, DateOnly.FromDateTime(DateTime.Now));

        string? body = null;
        factory.GeminiResponder = request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubGeminiHandler.Payload("""{"items":[]}""");
        };

        await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "was habe ich gegessen" } }
        });

        Assert.NotNull(body);
        Assert.Contains("Eis, Vanille", body);
        Assert.DoesNotContain(email, body);
        Assert.DoesNotContain(userId, body);
    }

    [Fact]
    public async Task ParseMeal_WithoutHistory_BehavesAsBefore()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        string? body = null;
        factory.GeminiResponder = request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubGeminiHandler.Payload("""
            {
              "items": [
                { "searchTerm": "Apfel", "label": "Apfel", "quantityInGrams": 150,
                  "mealType": "Snack", "productKind": "generic",
                  "estimate": { "calories": 52, "protein": 0.3, "carbohydrates": 14, "fat": 0.2 } }
              ]
            }
            """);
        };

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        response.EnsureSuccessStatusCode();
        Assert.NotNull(body);
        // Die Systemanweisung nennt den Abschnittsnamen "Bisher gegessen" immer, um ihr Format zu
        // erklaeren - der ganze Rumpf enthaelt ihn also auch ohne Verlauf. Entscheidend ist allein
        // "input" (Verlaufsblock plus Gespraechsprotokoll), nicht "system_instruction" daneben.
        using var sent = JsonDocument.Parse(body!);
        Assert.DoesNotContain("Bisher gegessen", sent.RootElement.GetProperty("input").GetString());
        var item = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items")[0];
        Assert.Equal("generic", item.GetProperty("source").GetString());
    }

    /// <summary>
    /// Ein zukuenftig datierter Eintrag ist ueber die normale Oberflaeche anlegbar (weder
    /// POST /api/meals noch die Datumsnavigation pruefen gegen die Zukunft) und darf trotzdem
    /// nicht im Verlaufsblock auftauchen - sonst verdraengt er per OrderByDescending echte
    /// Vergangenheit vom 40er-Deckel. Gegen "input" geprueft, nicht gegen den ganzen Rumpf: die
    /// Systemanweisung nennt "Bisher gegessen" auch ohne Verlauf (siehe Test oben).
    /// </summary>
    [Fact]
    public async Task ParseMeal_ExcludesFutureDatedEntriesFromHistory()
    {
        var (client, _, _) = await factory.CreateUserAsync();
        var heute = DateOnly.FromDateTime(DateTime.Now);
        await AnlegenAsync(client, "Heutiger Testsnack", 52m, 150m, heute);
        await AnlegenAsync(client, "Morgiges Testfruehstueck", 300m, 100m, heute.AddDays(1));

        string? body = null;
        factory.GeminiResponder = request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubGeminiHandler.Payload("""{"items":[]}""");
        };

        await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "was habe ich gegessen" } }
        });

        Assert.NotNull(body);
        using var sent = JsonDocument.Parse(body!);
        var input = sent.RootElement.GetProperty("input").GetString();

        Assert.Contains("Heutiger Testsnack", input);
        Assert.DoesNotContain("Morgiges Testfruehstueck", input);
    }

    /// <summary>
    /// Belegt den Fallschirm in LoadHistoryAsync: die MealEntries-Tabelle fehlt der laufenden
    /// Anwendung unterm Fuss weg (ohne WAL wirkt das aehnlich wie ein SQLITE_BUSY unter
    /// Schreiblast - beides eine Ausnahme, die vor diesem Fix an den drei Gemini-catch-Bloecken
    /// in AiEndpoints vorbeigeflogen waere). Die Erfassung muss trotzdem mit 200 antworten, im
    /// bereits getesteten Pfad "kein Verlauf".
    /// </summary>
    [Fact]
    public async Task ParseMeal_WhenHistoryCannotBeLoaded_StillSucceedsWithoutHistory()
    {
        using var isolated = new NutriTrackApiFactory();
        await isolated.ResetDatabaseAsync();

        var (client, _, _) = await isolated.CreateUserAsync();

        using (var scope = isolated.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE MealEntries");
        }

        isolated.GeminiResponder = _ => StubGeminiHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Apfel", "label": "Apfel", "quantityInGrams": 150,
              "mealType": "Snack", "productKind": "generic",
              "estimate": { "calories": 52, "protein": 0.3, "carbohydrates": 14, "fat": 0.2 } }
          ]
        }
        """);

        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var parsed = await response.Content.ReadFromJsonAsync<ParseMealResponseDto>();
        Assert.Equal("generic", Assert.Single(parsed!.Items).Source);
    }
}
