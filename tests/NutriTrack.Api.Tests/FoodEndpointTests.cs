using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NutriTrack.Api.Contracts.Food;
using NutriTrack.Api.Tests.Infrastructure;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Tests;

public class FoodEndpointTests(NutriTrackApiFactory factory) : IClassFixture<NutriTrackApiFactory>
{
    [Fact]
    public async Task Search_ReturnsMappedProductsFromStub()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.GetAsync("/api/food/search?query=hafer");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var results = await response.Content.ReadFromJsonAsync<List<FoodSearchResponse>>();
        Assert.NotNull(results);

        // Das zweite Stub-Produkt hat product_name = null und muss herausgefiltert werden.
        var single = Assert.Single(results!);
        Assert.Equal(StubOpenFoodFactsHandler.SearchProductName, single.Name);
        Assert.Equal(StubOpenFoodFactsHandler.SearchProductBrand, single.Brand);
        Assert.Equal("1111111111111", single.Barcode);
        Assert.Equal(StubOpenFoodFactsHandler.SearchProductCalories, single.Calories);
        Assert.Equal(13.5m, single.Protein);
        Assert.Equal(58.7m, single.Carbohydrates);
        Assert.Equal(7m, single.Fat);
        Assert.Equal(10m, single.Fiber);
        Assert.Null(single.VitaminA);
    }

    [Fact]
    public async Task Barcode_WithKnownCode_ReturnsProduct()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.GetAsync($"/api/food/barcode/{StubOpenFoodFactsHandler.KnownBarcode}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var product = await response.Content.ReadFromJsonAsync<FoodSearchResponse>();
        Assert.NotNull(product);
        Assert.Equal(StubOpenFoodFactsHandler.BarcodeProductName, product!.Name);
        Assert.Equal(StubOpenFoodFactsHandler.BarcodeProductBrand, product.Brand);
        Assert.Equal(StubOpenFoodFactsHandler.KnownBarcode, product.Barcode);
        Assert.Equal(StubOpenFoodFactsHandler.BarcodeProductCalories, product.Calories);
        Assert.Equal(3.4m, product.Protein);
        Assert.Equal(4.8m, product.Carbohydrates);
        Assert.Equal(3.6m, product.Fat);
        Assert.Null(product.Fiber);
    }

    [Fact]
    public async Task Barcode_WithUnknownCode_ReturnsNotFound()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.GetAsync($"/api/food/barcode/{StubOpenFoodFactsHandler.UnknownBarcode}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetById_WithUnknownId_ReturnsNotFound()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.GetAsync($"/api/food/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetById_ReturnsFoodItemCreatedByMealEntry()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var created = await client.PostAsJsonAsync("/api/meals", new
        {
            foodName = "Testquark",
            brand = "Testmarke",
            calories = 67m,
            protein = 12m,
            carbohydrates = 4m,
            fat = 0.2m,
            quantityInGrams = 200m,
            mealType = "Snack",
            date = "2026-02-02"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // Die Meal-Antwort enthaelt keine FoodItemId, deshalb wird sie direkt aus der Test-DB geholt.
        Guid foodItemId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            foodItemId = (await db.FoodItems.SingleAsync(f => f.Name == "Testquark")).Id;
        }

        var response = await client.GetAsync($"/api/food/{foodItemId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var food = await response.Content.ReadFromJsonAsync<FoodSearchResponse>();
        Assert.NotNull(food);
        Assert.Equal(foodItemId, food!.Id);
        Assert.Equal("Testquark", food.Name);
        Assert.Equal("Testmarke", food.Brand);

        // In der DB liegen die Naehrwerte unveraendert pro 100 g, nicht auf 200 g hochgerechnet.
        Assert.Equal(67m, food.Calories);
        Assert.Equal(12m, food.Protein);
    }

    [Fact]
    public async Task Search_WhenOpenFoodFactsIsDown_ReturnsServiceUnavailable()
    {
        factory.OpenFoodFactsResponder = _ => new HttpResponseMessage(HttpStatusCode.BadGateway);

        try
        {
            var (client, _, _) = await factory.CreateUserAsync();

            var response = await client.GetAsync("/api/food/search?query=hafer");

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            // Die Factory lebt fuer die ganze Testklasse. Bliebe der Ausfall stehen, haengte das
            // Ergebnis der uebrigen Tests an der Reihenfolge, in der xUnit sie aufruft.
            factory.OpenFoodFactsResponder = null;
        }
    }

    [Fact]
    public async Task Barcode_WithoutToken_ReturnsUnauthorized()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/food/barcode/{StubOpenFoodFactsHandler.KnownBarcode}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Beobachtet am 2026-09-12 im Betrieb: die Suche von OpenFoodFacts antwortete abwechselnd mit
    /// 503 und 200, ohne dass ein Limit verletzt war. Ein einziger Wiederholungsversuch rettet
    /// genau diesen Fall - ohne ihn steht der KI-Pfad meistens auf Schaetzwerten.
    /// </summary>
    [Fact]
    public async Task Search_WhenFirstAttemptFails_RetriesOnceAndSucceeds()
    {
        using var isolated = new NutriTrackApiFactory();
        await isolated.ResetDatabaseAsync();

        var versuche = 0;
        isolated.OpenFoodFactsResponder = request =>
        {
            if (!request.RequestUri!.ToString().Contains("/cgi/search.pl", StringComparison.Ordinal))
                return null;

            // Nur der ERSTE Anlauf scheitert; der zweite laeuft in das normale Stub-Verhalten.
            return Interlocked.Increment(ref versuche) == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : null;
        };

        var (client, _, _) = await isolated.CreateUserAsync();

        var response = await client.GetAsync("/api/food/search?query=hafer");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, versuche);

        var treffer = await response.Content.ReadFromJsonAsync<List<FoodSearchDto>>();
        Assert.NotNull(treffer);
        Assert.Equal(StubOpenFoodFactsHandler.SearchProductName, treffer![0].Name);
    }

    [Fact]
    public async Task Search_WhenBothAttemptsFail_ReturnsServiceUnavailable()
    {
        using var isolated = new NutriTrackApiFactory();
        await isolated.ResetDatabaseAsync();

        var versuche = 0;
        isolated.OpenFoodFactsResponder = request =>
        {
            if (!request.RequestUri!.ToString().Contains("/cgi/search.pl", StringComparison.Ordinal))
                return null;

            Interlocked.Increment(ref versuche);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        };

        var (client, _, _) = await isolated.CreateUserAsync();

        var response = await client.GetAsync("/api/food/search?query=hafer");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        // Genau zwei - kein Dauerfeuer gegen einen Dienst, der ohnehin schon am Boden liegt.
        Assert.Equal(2, versuche);
    }

    /// <summary>
    /// Die Bremse sitzt im Dienst, nicht im KI-Pfad - also greift sie auch fuer die Suchseite.
    /// Der Nutzer bekommt 429 und nicht 503: er kann etwas tun, naemlich kurz warten.
    /// </summary>
    [Fact]
    public async Task Search_BeyondSearchQuota_ReturnsTooManyRequests()
    {
        using var isolated = new NutriTrackApiFactory();
        await isolated.ResetDatabaseAsync();

        var (client, _, _) = await isolated.CreateUserAsync();

        // Acht erfolgreiche Suchen schoepfen das Minutenkontingent aus (jede mit eigenem Begriff,
        // damit der Zwischenspeicher nicht dazwischenfunkt - er sitzt allerdings im KI-Pfad).
        for (var i = 0; i < 8; i++)
        {
            var ok = await client.GetAsync($"/api/food/search?query=begriff-{i}");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var blockiert = await client.GetAsync("/api/food/search?query=begriff-neun");

        Assert.Equal(HttpStatusCode.TooManyRequests, blockiert.StatusCode);
    }
}
