using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NutriTrack.Api.Contracts.Meals;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Endpoints;

public static class MealEndpoints
{
    public static void MapMealEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/meals").WithTags("Meals").RequireAuthorization();

        group.MapPost("/", async (CreateMealEntryRequest request, ClaimsPrincipal user, AppDbContext db) =>
        {
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)!.Value;

            // Find or create FoodItem
            var foodItem = await db.FoodItems.FirstOrDefaultAsync(f =>
                f.Name == request.FoodName && f.Brand == request.Brand);

            if (foodItem is null)
            {
                foodItem = new FoodItem
                {
                    Id = Guid.NewGuid(),
                    Name = request.FoodName,
                    Brand = request.Brand,
                    Barcode = request.Barcode,
                    Calories = request.Calories,
                    Protein = request.Protein,
                    Carbohydrates = request.Carbohydrates,
                    Fat = request.Fat,
                    Fiber = request.Fiber,
                    Sugar = request.Sugar,
                    SaturatedFat = request.SaturatedFat,
                    Sodium = request.Sodium,
                    VitaminA = request.VitaminA,
                    VitaminC = request.VitaminC,
                    VitaminD = request.VitaminD,
                    Calcium = request.Calcium,
                    Iron = request.Iron,
                    Potassium = request.Potassium,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.FoodItems.Add(foodItem);
            }

            if (!Enum.TryParse<MealType>(request.MealType, true, out var mealType))
                return Results.BadRequest(new { Error = $"Invalid meal type. Use: {string.Join(", ", Enum.GetNames<MealType>())}" });

            var entry = new MealEntry
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                FoodItemId = foodItem.Id,
                QuantityInGrams = request.QuantityInGrams,
                MealType = mealType,
                Date = request.Date ?? DateOnly.FromDateTime(DateTime.UtcNow),
                Time = request.Time ?? TimeOnly.FromDateTime(DateTime.UtcNow),
                CreatedAt = DateTime.UtcNow
            };

            db.MealEntries.Add(entry);
            await db.SaveChangesAsync();

            return Results.Created($"/api/meals/{entry.Id}", MapToResponse(entry, foodItem));
        });

        group.MapGet("/", async (ClaimsPrincipal user, AppDbContext db, DateOnly? date) =>
        {
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)!.Value;
            var targetDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow);

            var entries = await db.MealEntries
                .Include(m => m.FoodItem)
                .Where(m => m.UserId == userId && m.Date == targetDate)
                .OrderBy(m => m.Time)
                .ToListAsync();

            return Results.Ok(entries.Select(e => MapToResponse(e, e.FoodItem)));
        });

        group.MapGet("/summary", async (ClaimsPrincipal user, AppDbContext db, DateOnly? date) =>
        {
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)!.Value;
            var targetDate = date ?? DateOnly.FromDateTime(DateTime.UtcNow);

            var entries = await db.MealEntries
                .Include(m => m.FoodItem)
                .Where(m => m.UserId == userId && m.Date == targetDate)
                .OrderBy(m => m.Time)
                .ToListAsync();

            var responses = entries.Select(e => MapToResponse(e, e.FoodItem)).ToList();

            return Results.Ok(new DailySummaryResponse
            {
                Date = targetDate,
                TotalEntries = responses.Count,
                TotalCalories = responses.Sum(r => r.Calories),
                TotalProtein = responses.Sum(r => r.Protein),
                TotalCarbohydrates = responses.Sum(r => r.Carbohydrates),
                TotalFat = responses.Sum(r => r.Fat),
                TotalFiber = responses.Sum(r => r.Fiber ?? 0),
                TotalSugar = responses.Sum(r => r.Sugar ?? 0),
                TotalSaturatedFat = responses.Sum(r => r.SaturatedFat ?? 0),
                TotalSodium = responses.Sum(r => r.Sodium ?? 0),
                TotalVitaminA = responses.Sum(r => r.VitaminA ?? 0),
                TotalVitaminC = responses.Sum(r => r.VitaminC ?? 0),
                TotalVitaminD = responses.Sum(r => r.VitaminD ?? 0),
                TotalCalcium = responses.Sum(r => r.Calcium ?? 0),
                TotalIron = responses.Sum(r => r.Iron ?? 0),
                TotalPotassium = responses.Sum(r => r.Potassium ?? 0),
                Entries = responses
            });
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
        {
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)!.Value;
            var entry = await db.MealEntries.FirstOrDefaultAsync(m => m.Id == id && m.UserId == userId);

            if (entry is null)
                return Results.NotFound();

            db.MealEntries.Remove(entry);
            await db.SaveChangesAsync();

            return Results.NoContent();
        });
    }

    private static decimal Calc(decimal per100g, decimal grams) => Math.Round(per100g * grams / 100m, 2);
    private static decimal? CalcN(decimal? per100g, decimal grams) => per100g.HasValue ? Math.Round(per100g.Value * grams / 100m, 2) : null;

    private static MealEntryResponse MapToResponse(MealEntry entry, FoodItem food) => new()
    {
        Id = entry.Id,
        FoodName = food.Name,
        Brand = food.Brand,
        QuantityInGrams = entry.QuantityInGrams,
        MealType = entry.MealType.ToString(),
        Date = entry.Date,
        Time = entry.Time,
        Calories = Calc(food.Calories, entry.QuantityInGrams),
        Protein = Calc(food.Protein, entry.QuantityInGrams),
        Carbohydrates = Calc(food.Carbohydrates, entry.QuantityInGrams),
        Fat = Calc(food.Fat, entry.QuantityInGrams),
        Fiber = CalcN(food.Fiber, entry.QuantityInGrams),
        Sugar = CalcN(food.Sugar, entry.QuantityInGrams),
        SaturatedFat = CalcN(food.SaturatedFat, entry.QuantityInGrams),
        Sodium = CalcN(food.Sodium, entry.QuantityInGrams),
        VitaminA = CalcN(food.VitaminA, entry.QuantityInGrams),
        VitaminC = CalcN(food.VitaminC, entry.QuantityInGrams),
        VitaminD = CalcN(food.VitaminD, entry.QuantityInGrams),
        Calcium = CalcN(food.Calcium, entry.QuantityInGrams),
        Iron = CalcN(food.Iron, entry.QuantityInGrams),
        Potassium = CalcN(food.Potassium, entry.QuantityInGrams)
    };
}
