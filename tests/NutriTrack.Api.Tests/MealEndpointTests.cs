using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NutriTrack.Api.Contracts.Meals;
using NutriTrack.Api.Tests.Infrastructure;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Tests;

public class MealEndpointTests(NutriTrackApiFactory factory) : IClassFixture<NutriTrackApiFactory>
{
    // Bewusst krumme Werte und eine Menge != 100 g, damit die Hochrechnung wirklich geprueft wird
    // und nicht zufaellig durch einen 1:1-Durchreicher erfuellt waere.
    private static object OatsPayload(decimal quantity, string mealType = "Breakfast", DateOnly? date = null) => new
    {
        foodName = "Haferflocken",
        brand = "Testmuehle",
        calories = 250m,
        protein = 12.5m,
        carbohydrates = 30m,
        fat = 8.2m,
        fiber = 4.4m,
        sugar = 1.2m,
        saturatedFat = 1.5m,
        sodium = 0.02m,
        quantityInGrams = quantity,
        mealType,
        date = date?.ToString("yyyy-MM-dd"),
        time = "08:15:00"
    };

    private static object BananaPayload(decimal quantity, string mealType = "Snack", DateOnly? date = null) => new
    {
        foodName = "Banane",
        brand = (string?)null,
        calories = 89m,
        protein = 1.1m,
        carbohydrates = 22.8m,
        fat = 0.3m,
        fiber = 2.6m,
        quantityInGrams = quantity,
        mealType,
        date = date?.ToString("yyyy-MM-dd"),
        time = "10:30:00"
    };

    [Fact]
    public async Task Create_CalculatesNutrientsForQuantity()
    {
        var (client, _, _) = await factory.CreateUserAsync();
        var date = new DateOnly(2026, 3, 1);

        var response = await client.PostAsJsonAsync("/api/meals", OatsPayload(250m, date: date));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var entry = await response.Content.ReadFromJsonAsync<MealEntryResponse>();
        Assert.NotNull(entry);

        // 250 g von 250 kcal/100 g
        Assert.Equal(625.00m, entry!.Calories);
        Assert.Equal(31.25m, entry.Protein);
        Assert.Equal(75.00m, entry.Carbohydrates);
        Assert.Equal(20.50m, entry.Fat);
        Assert.Equal(11.00m, entry.Fiber);
        Assert.Equal(3.00m, entry.Sugar);
        Assert.Equal(3.75m, entry.SaturatedFat);
        Assert.Equal(0.05m, entry.Sodium);

        Assert.Equal("Haferflocken", entry.FoodName);
        Assert.Equal("Testmuehle", entry.Brand);
        Assert.Equal("Breakfast", entry.MealType);
        Assert.Equal(250m, entry.QuantityInGrams);
        Assert.Equal(date, entry.Date);
        Assert.NotEqual(Guid.Empty, entry.Id);
    }

    [Fact]
    public async Task Create_UsesCamelCaseOnTheWire()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/meals", OatsPayload(100m, date: new DateOnly(2026, 3, 2)));
        var json = await response.ReadJsonAsync();

        Assert.Equal("Haferflocken", json.GetProperty("foodName").GetString());
        Assert.Equal(250.00m, json.GetProperty("calories").GetDecimal());
        Assert.Equal(100m, json.GetProperty("quantityInGrams").GetDecimal());
        Assert.Equal("2026-03-02", json.GetProperty("date").GetString());
    }

    [Fact]
    public async Task Create_WithSameNameAndBrandButOtherNutrients_UsesValuesFromRequest()
    {
        var (first, _, _) = await factory.CreateUserAsync();
        var (second, _, _) = await factory.CreateUserAsync();
        var date = new DateOnly(2026, 11, 1);
        var name = $"Mueslimischung-{Guid.NewGuid():N}";

        // Der erste Nutzer belegt Name+Marke mit seinen Werten ...
        var firstResponse = await first.PostAsJsonAsync("/api/meals", new
        {
            foodName = name,
            brand = "Testmuehle",
            calories = 250m,
            protein = 12.5m,
            carbohydrates = 30m,
            fat = 8.2m,
            quantityInGrams = 100m,
            mealType = "Breakfast",
            date = date.ToString("yyyy-MM-dd")
        });
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);

        // ... der zweite schickt dasselbe Namenspaar mit anderen Naehrwerten und muss SEINE
        // Werte zurueckbekommen, nicht die des ersten.
        var response = await second.PostAsJsonAsync("/api/meals", new
        {
            foodName = name,
            brand = "Testmuehle",
            calories = 400m,
            protein = 5m,
            carbohydrates = 60m,
            fat = 20m,
            quantityInGrams = 100m,
            mealType = "Breakfast",
            date = date.ToString("yyyy-MM-dd")
        });

        var entry = await response.Content.ReadFromJsonAsync<MealEntryResponse>();
        Assert.NotNull(entry);
        Assert.Equal(400.00m, entry!.Calories);
        Assert.Equal(5.00m, entry.Protein);
        Assert.Equal(60.00m, entry.Carbohydrates);
        Assert.Equal(20.00m, entry.Fat);

        // Der Eintrag des ersten Nutzers bleibt bei seinen urspruenglichen Werten.
        var firstEntries = await first.GetFromJsonAsync<List<MealEntryResponse>>($"/api/meals?date={date:yyyy-MM-dd}");
        var untouched = Assert.Single(firstEntries!);
        Assert.Equal(250.00m, untouched.Calories);
    }

    [Fact]
    public async Task Create_WithSameBarcodeButCorrectedNutrients_UsesValuesFromRequest()
    {
        var (client, _, _) = await factory.CreateUserAsync();
        var date = new DateOnly(2026, 11, 2);
        var barcode = $"400{Random.Shared.Next(1000000, 9999999)}";

        object Payload(decimal calories) => new
        {
            foodName = "Schokoriegel",
            brand = "Testmuehle",
            barcode,
            calories,
            protein = 6.5m,
            carbohydrates = 58m,
            fat = 24m,
            quantityInGrams = 50m,
            mealType = "Snack",
            date = date.ToString("yyyy-MM-dd")
        };

        await client.PostAsJsonAsync("/api/meals", Payload(500m));

        // Korrigierter Datensatz zum selben Barcode: die neuen Werte gelten fuer den neuen Eintrag.
        var response = await client.PostAsJsonAsync("/api/meals", Payload(520m));

        var entry = await response.Content.ReadFromJsonAsync<MealEntryResponse>();
        Assert.NotNull(entry);
        Assert.Equal(260.00m, entry!.Calories);
    }

    [Fact]
    public async Task Create_WithIdenticalProductTwice_ReusesSameFoodItem()
    {
        var (client, _, _) = await factory.CreateUserAsync();
        var name = $"Reisgebaeck-{Guid.NewGuid():N}";

        object payload = new
        {
            foodName = name,
            brand = "Testmuehle",
            calories = 380m,
            protein = 7.5m,
            carbohydrates = 80m,
            fat = 3.2m,
            quantityInGrams = 50m,
            mealType = "Snack",
            date = "2026-11-03"
        };

        await client.PostAsJsonAsync("/api/meals", payload);
        await client.PostAsJsonAsync("/api/meals", payload);

        // Unveraenderte Werte duerfen den Katalog nicht mit Dubletten fluten.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(1, await db.FoodItems.CountAsync(f => f.Name == name));
    }

    [Fact]
    public async Task Create_KeepsMicronutrientsBelowGramScale()
    {
        var (client, _, _) = await factory.CreateUserAsync();
        var date = new DateOnly(2026, 11, 4);

        // Einheiten wie von OpenFoodFacts: Gramm pro 100 g, also Vitamin D 5 µg = 0.000005 g.
        var response = await client.PostAsJsonAsync("/api/meals", new
        {
            foodName = $"Vitaminmilch-{Guid.NewGuid():N}",
            calories = 42m,
            protein = 3.4m,
            carbohydrates = 4.8m,
            fat = 1.5m,
            sodium = 0.0005m,
            vitaminA = 0.000035m,
            vitaminD = 0.000005m,
            iron = 0.0002m,
            quantityInGrams = 200m,
            mealType = "Breakfast",
            date = date.ToString("yyyy-MM-dd")
        });

        var entry = await response.Content.ReadFromJsonAsync<MealEntryResponse>();
        Assert.NotNull(entry);
        Assert.Equal(0.001m, entry!.Sodium);
        Assert.Equal(0.00007m, entry.VitaminA);
        Assert.Equal(0.00001m, entry.VitaminD);
        Assert.Equal(0.0004m, entry.Iron);

        // Und die Tagessumme darf die Werte ebenso wenig auf 0 runden.
        var summary = await client.GetFromJsonAsync<DailySummaryResponse>($"/api/meals/summary?date={date:yyyy-MM-dd}");
        Assert.NotNull(summary);
        Assert.Equal(0.00007m, summary!.TotalVitaminA);
        Assert.Equal(0.00001m, summary.TotalVitaminD);
        Assert.Equal(0.0004m, summary.TotalIron);
    }

    [Fact]
    public async Task Create_WithoutDate_UsesLocalServerTime()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var before = DateTime.Now;
        var response = await client.PostAsJsonAsync("/api/meals", new
        {
            foodName = $"Apfel-{Guid.NewGuid():N}",
            calories = 52m,
            protein = 0.3m,
            carbohydrates = 14m,
            fat = 0.2m,
            quantityInGrams = 150m,
            mealType = "Snack"
        });
        var after = DateTime.Now;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var entry = await response.Content.ReadFromJsonAsync<MealEntryResponse>();
        Assert.NotNull(entry);

        // Lokale Zeit des Servers, nicht UTC: sonst laege die Uhrzeit 1-2 h zurueck und kurz nach
        // Mitternacht sogar das Datum.
        Assert.InRange(entry!.Date.ToDateTime(entry.Time), before.AddSeconds(-1), after.AddSeconds(1));

        // GET ohne date-Parameter muss denselben lokalen Tag treffen.
        var today = await client.GetFromJsonAsync<List<MealEntryResponse>>("/api/meals");
        Assert.Contains(today!, e => e.Id == entry.Id);
    }

    [Fact]
    public async Task Create_WithInvalidMealType_ReturnsBadRequest()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/meals", new
        {
            foodName = "Haferflocken",
            calories = 250m,
            protein = 12.5m,
            carbohydrates = 30m,
            fat = 8.2m,
            quantityInGrams = 100m,
            mealType = "Brunch"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Enum.TryParse winkt numerische Strings ohne Bereichspruefung durch - "99" darf trotzdem
    // nicht gespeichert und spaeter als "99" zurueckgegeben werden.
    [Theory]
    [InlineData("99")]
    [InlineData("-1")]
    public async Task Create_WithNumericMealType_ReturnsBadRequest(string mealType)
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/meals", OatsPayload(100m, mealType, new DateOnly(2026, 11, 5)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-500)]
    [InlineData(10001)]
    public async Task Create_WithInvalidQuantity_ReturnsBadRequest(decimal quantity)
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/meals", OatsPayload(quantity, date: new DateOnly(2026, 3, 3)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_WithNegativeQuantity_DoesNotAffectDailySummary()
    {
        var (client, _, _) = await factory.CreateUserAsync();
        var date = new DateOnly(2026, 3, 4);

        await client.PostAsJsonAsync("/api/meals", OatsPayload(-500m, date: date));

        var summary = await client.GetFromJsonAsync<DailySummaryResponse>($"/api/meals/summary?date={date:yyyy-MM-dd}");

        Assert.NotNull(summary);
        Assert.Equal(0, summary!.TotalEntries);
        Assert.Equal(0m, summary.TotalCalories);
        Assert.Equal(0m, summary.TotalFat);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_WithEmptyFoodName_ReturnsBadRequest(string foodName)
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.PostAsJsonAsync("/api/meals", new
        {
            foodName,
            calories = 250m,
            protein = 12.5m,
            carbohydrates = 30m,
            fat = 8.2m,
            quantityInGrams = 100m,
            mealType = "Snack"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetDaily_ReturnsOnlyEntriesOfRequestedDate()
    {
        var (client, _, _) = await factory.CreateUserAsync();
        var date = new DateOnly(2026, 4, 10);
        var otherDate = new DateOnly(2026, 4, 11);

        await client.PostAsJsonAsync("/api/meals", OatsPayload(200m, date: date));
        await client.PostAsJsonAsync("/api/meals", BananaPayload(120m, date: otherDate));

        var entries = await client.GetFromJsonAsync<List<MealEntryResponse>>($"/api/meals?date={date:yyyy-MM-dd}");

        Assert.NotNull(entries);
        var single = Assert.Single(entries!);
        Assert.Equal("Haferflocken", single.FoodName);
        Assert.Equal(date, single.Date);
    }

    [Fact]
    public async Task Summary_AddsUpNutrientsOfAllEntries()
    {
        var (client, _, _) = await factory.CreateUserAsync();
        var date = new DateOnly(2026, 5, 5);

        await client.PostAsJsonAsync("/api/meals", OatsPayload(250m, date: date));
        await client.PostAsJsonAsync("/api/meals", BananaPayload(150m, date: date));

        var summary = await client.GetFromJsonAsync<DailySummaryResponse>($"/api/meals/summary?date={date:yyyy-MM-dd}");

        Assert.NotNull(summary);
        Assert.Equal(date, summary!.Date);
        Assert.Equal(2, summary.TotalEntries);

        // Hafer 250 g: 625 / 31.25 / 75 / 20.50 / 11.00
        // Banane 150 g: 133.50 / 1.65 / 34.20 / 0.45 / 3.90
        Assert.Equal(758.50m, summary.TotalCalories);
        Assert.Equal(32.90m, summary.TotalProtein);
        Assert.Equal(109.20m, summary.TotalCarbohydrates);
        Assert.Equal(20.95m, summary.TotalFat);
        Assert.Equal(14.90m, summary.TotalFiber);

        Assert.Equal(2, summary.Entries.Count);
        Assert.Equal(summary.TotalCalories, summary.Entries.Sum(e => e.Calories));
    }

    [Fact]
    public async Task Summary_IgnoresEntriesOfOtherDays()
    {
        var (client, _, _) = await factory.CreateUserAsync();
        var date = new DateOnly(2026, 6, 1);

        await client.PostAsJsonAsync("/api/meals", OatsPayload(100m, date: date));
        await client.PostAsJsonAsync("/api/meals", OatsPayload(100m, date: new DateOnly(2026, 6, 2)));

        var summary = await client.GetFromJsonAsync<DailySummaryResponse>($"/api/meals/summary?date={date:yyyy-MM-dd}");

        Assert.NotNull(summary);
        Assert.Equal(1, summary!.TotalEntries);
        Assert.Equal(250.00m, summary.TotalCalories);
    }

    [Fact]
    public async Task Update_ChangesQuantityAndRecalculatesNutrients()
    {
        var (client, _, _) = await factory.CreateUserAsync();
        var date = new DateOnly(2026, 7, 7);

        var created = await client.PostAsJsonAsync("/api/meals", OatsPayload(100m, date: date));
        var entry = await created.Content.ReadFromJsonAsync<MealEntryResponse>();
        Assert.NotNull(entry);

        var response = await client.PutAsJsonAsync($"/api/meals/{entry!.Id}", new
        {
            quantityInGrams = 250m,
            mealType = "Dinner",
            date = "2026-07-08",
            time = "19:45:00"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var updated = await response.Content.ReadFromJsonAsync<MealEntryResponse>();
        Assert.NotNull(updated);
        Assert.Equal(entry.Id, updated!.Id);
        Assert.Equal(250m, updated.QuantityInGrams);
        Assert.Equal("Dinner", updated.MealType);
        Assert.Equal(new DateOnly(2026, 7, 8), updated.Date);
        Assert.Equal(new TimeOnly(19, 45, 0), updated.Time);
        Assert.Equal(625.00m, updated.Calories);
        Assert.Equal(31.25m, updated.Protein);
        Assert.Equal(20.50m, updated.Fat);

        // Persistiert, nicht nur in der Antwort
        var reloaded = await client.GetFromJsonAsync<List<MealEntryResponse>>("/api/meals?date=2026-07-08");
        var single = Assert.Single(reloaded!);
        Assert.Equal(625.00m, single.Calories);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task Update_WithInvalidQuantity_ReturnsBadRequest(decimal quantity)
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var created = await client.PostAsJsonAsync("/api/meals", OatsPayload(100m, date: new DateOnly(2026, 8, 1)));
        var entry = await created.Content.ReadFromJsonAsync<MealEntryResponse>();

        var response = await client.PutAsJsonAsync($"/api/meals/{entry!.Id}", new
        {
            quantityInGrams = quantity,
            mealType = "Lunch"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Update_WithInvalidMealType_ReturnsBadRequest()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var created = await client.PostAsJsonAsync("/api/meals", OatsPayload(100m, date: new DateOnly(2026, 8, 2)));
        var entry = await created.Content.ReadFromJsonAsync<MealEntryResponse>();

        var response = await client.PutAsJsonAsync($"/api/meals/{entry!.Id}", new
        {
            quantityInGrams = 120m,
            mealType = "Brunch"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("99")]
    [InlineData("-1")]
    public async Task Update_WithNumericMealType_ReturnsBadRequest(string mealType)
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var created = await client.PostAsJsonAsync("/api/meals", OatsPayload(100m, date: new DateOnly(2026, 8, 3)));
        var entry = await created.Content.ReadFromJsonAsync<MealEntryResponse>();

        var response = await client.PutAsJsonAsync($"/api/meals/{entry!.Id}", new
        {
            quantityInGrams = 120m,
            mealType
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Der gespeicherte Eintrag behaelt seinen gueltigen Typ.
        var reloaded = await client.GetFromJsonAsync<List<MealEntryResponse>>("/api/meals?date=2026-08-03");
        Assert.All(reloaded!, e => Assert.Equal("Breakfast", e.MealType));
    }

    [Fact]
    public async Task Update_WithUnknownId_ReturnsNotFound()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.PutAsJsonAsync($"/api/meals/{Guid.NewGuid()}", new
        {
            quantityInGrams = 120m,
            mealType = "Lunch"
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Delete_RemovesEntry()
    {
        var (client, _, _) = await factory.CreateUserAsync();
        var date = new DateOnly(2026, 9, 9);

        var created = await client.PostAsJsonAsync("/api/meals", OatsPayload(100m, date: date));
        var entry = await created.Content.ReadFromJsonAsync<MealEntryResponse>();

        var response = await client.DeleteAsync($"/api/meals/{entry!.Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var remaining = await client.GetFromJsonAsync<List<MealEntryResponse>>($"/api/meals?date={date:yyyy-MM-dd}");
        Assert.Empty(remaining!);
    }

    [Fact]
    public async Task Delete_WithUnknownId_ReturnsNotFound()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.DeleteAsync($"/api/meals/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ForeignUser_CanNeitherSeeNorChangeNorDeleteEntry()
    {
        var (owner, _, _) = await factory.CreateUserAsync();
        var (intruder, _, _) = await factory.CreateUserAsync();
        var date = new DateOnly(2026, 10, 10);

        var created = await owner.PostAsJsonAsync("/api/meals", OatsPayload(180m, date: date));
        var entry = await created.Content.ReadFromJsonAsync<MealEntryResponse>();
        Assert.NotNull(entry);

        var listed = await intruder.GetFromJsonAsync<List<MealEntryResponse>>($"/api/meals?date={date:yyyy-MM-dd}");
        Assert.Empty(listed!);

        var summary = await intruder.GetFromJsonAsync<DailySummaryResponse>($"/api/meals/summary?date={date:yyyy-MM-dd}");
        Assert.Equal(0, summary!.TotalEntries);
        Assert.Equal(0m, summary.TotalCalories);

        var update = await intruder.PutAsJsonAsync($"/api/meals/{entry!.Id}", new
        {
            quantityInGrams = 1m,
            mealType = "Lunch"
        });
        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);

        var delete = await intruder.DeleteAsync($"/api/meals/{entry.Id}");
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);

        // Der Eintrag des Eigentuemers ist unveraendert geblieben
        var ownerEntries = await owner.GetFromJsonAsync<List<MealEntryResponse>>($"/api/meals?date={date:yyyy-MM-dd}");
        var single = Assert.Single(ownerEntries!);
        Assert.Equal(180m, single.QuantityInGrams);
        Assert.Equal(450.00m, single.Calories);
    }
}
