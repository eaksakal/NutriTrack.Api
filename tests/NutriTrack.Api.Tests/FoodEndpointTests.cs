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
    public async Task Barcode_WithoutToken_ReturnsUnauthorized()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync($"/api/food/barcode/{StubOpenFoodFactsHandler.KnownBarcode}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
