using Microsoft.EntityFrameworkCore;
using NutriTrack.Api.Contracts.Food;
using NutriTrack.Api.Services;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Endpoints;

public static class FoodEndpoints
{
    public static void MapFoodEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/food").WithTags("Food").RequireAuthorization();

        group.MapGet("/search", async (string query, OpenFoodFactsService offService, AppDbContext db, int page = 1, int pageSize = 20) =>
        {
            var products = await offService.SearchAsync(query, page, pageSize);

            var results = products
                .Where(p => p.ProductName is not null)
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

            return Results.Ok(results);
        });

        group.MapGet("/barcode/{barcode}", async (string barcode, OpenFoodFactsService offService) =>
        {
            var product = await offService.GetByBarcodeAsync(barcode);
            if (product is null)
                return Results.NotFound(new { Error = "Product not found" });

            return Results.Ok(new FoodSearchResponse
            {
                Name = product.ProductName ?? "Unknown",
                Brand = product.Brands,
                Barcode = product.Code,
                Calories = product.Nutriments?.EnergyKcal100g ?? 0,
                Protein = product.Nutriments?.Proteins100g ?? 0,
                Carbohydrates = product.Nutriments?.Carbohydrates100g ?? 0,
                Fat = product.Nutriments?.Fat100g ?? 0,
                Fiber = product.Nutriments?.Fiber100g,
                Sugar = product.Nutriments?.Sugars100g,
                SaturatedFat = product.Nutriments?.SaturatedFat100g,
                Sodium = product.Nutriments?.Sodium100g,
                VitaminA = product.Nutriments?.VitaminA100g,
                VitaminC = product.Nutriments?.VitaminC100g,
                VitaminD = product.Nutriments?.VitaminD100g,
                Calcium = product.Nutriments?.Calcium100g,
                Iron = product.Nutriments?.Iron100g,
                Potassium = product.Nutriments?.Potassium100g
            });
        });

        group.MapGet("/{id:guid}", async (Guid id, AppDbContext db) =>
        {
            var food = await db.FoodItems.FindAsync(id);
            if (food is null)
                return Results.NotFound();

            return Results.Ok(MapToResponse(food));
        });
    }

    private static FoodSearchResponse MapToResponse(FoodItem f) => new()
    {
        Id = f.Id,
        Name = f.Name,
        Brand = f.Brand,
        Barcode = f.Barcode,
        Calories = f.Calories,
        Protein = f.Protein,
        Carbohydrates = f.Carbohydrates,
        Fat = f.Fat,
        Fiber = f.Fiber,
        Sugar = f.Sugar,
        SaturatedFat = f.SaturatedFat,
        Sodium = f.Sodium,
        VitaminA = f.VitaminA,
        VitaminC = f.VitaminC,
        VitaminD = f.VitaminD,
        Calcium = f.Calcium,
        Iron = f.Iron,
        Potassium = f.Potassium
    };
}
