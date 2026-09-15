using NutriTrack.Api.Services;
using NutriTrack.Domain.Entities;

namespace NutriTrack.Api.Tests;

public class MealHistoryContextTests
{
    private static readonly DateOnly Heute = new(2026, 9, 15);

    private static MealEntry Eintrag(
        string name, decimal quantity, DateOnly date, TimeOnly time, MealType mealType)
        => new()
        {
            Id = Guid.NewGuid(),
            UserId = "u1",
            FoodItemId = Guid.NewGuid(),
            FoodItem = new FoodItem { Name = name, Calories = 200m, Protein = 3m, Carbohydrates = 25m, Fat = 9m },
            QuantityInGrams = quantity,
            MealType = mealType,
            Date = date,
            Time = time
        };

    [Fact]
    public void Build_WithoutEntries_IsEmpty()
    {
        var context = MealHistoryContext.Build([], Heute);

        Assert.True(context.IsEmpty);
        Assert.Equal(string.Empty, context.Text);
    }

    [Fact]
    public void Build_NumbersEntriesAndNamesDaysRelatively()
    {
        var context = MealHistoryContext.Build(
        [
            Eintrag("Haferflocken", 80m, Heute, new TimeOnly(8, 10), MealType.Breakfast),
            Eintrag("Eis, Vanille", 100m, Heute.AddDays(-1), new TimeOnly(21, 30), MealType.Snack),
            Eintrag("Spaghetti (gekocht)", 250m, Heute.AddDays(-2), new TimeOnly(12, 15), MealType.Lunch),
        ], Heute);

        Assert.False(context.IsEmpty);
        Assert.Contains("[v1] heute 08:10 Breakfast - Haferflocken (80 g)", context.Text);
        Assert.Contains("[v2] gestern 21:30 Snack - Eis, Vanille (100 g)", context.Text);
        Assert.Contains("[v3] vorgestern 12:15 Lunch - Spaghetti (gekocht) (250 g)", context.Text);
    }

    [Fact]
    public void TryResolve_WithKnownRef_ReturnsEntryAndHint()
    {
        var eis = Eintrag("Eis, Vanille", 100m, Heute.AddDays(-1), new TimeOnly(21, 30), MealType.Snack);
        var context = MealHistoryContext.Build(
            [Eintrag("Haferflocken", 80m, Heute, new TimeOnly(8, 10), MealType.Breakfast), eis], Heute);

        Assert.True(context.TryResolve("v2", out var entry, out var hint));
        Assert.Equal(eis.Id, entry.Id);
        Assert.Equal("gestern 21:30", hint);
    }

    [Theory]
    [InlineData("v99")]
    [InlineData("V1 ")]   // Kennungen sind kleingeschrieben, werden aber getrimmt und gefaltet
    [InlineData("")]
    [InlineData(null)]
    public void TryResolve_WithUnknownOrEmptyRef_ReturnsFalseOrFolds(string? sourceRef)
    {
        var context = MealHistoryContext.Build(
            [Eintrag("Haferflocken", 80m, Heute, new TimeOnly(8, 10), MealType.Breakfast)], Heute);

        var found = context.TryResolve(sourceRef, out _, out _);

        // "V1 " ist dieselbe Kennung wie "v1"; alles Uebrige darf nicht treffen.
        Assert.Equal(sourceRef == "V1 ", found);
    }

    [Fact]
    public void Build_WithMoreThanMaxEntries_KeepsOnlyTheFirstOnes()
    {
        var viele = Enumerable.Range(0, MealHistoryContext.MaxEntries + 5)
            .Select(i => Eintrag($"Posten {i}", 10m, Heute, new TimeOnly(12, 0), MealType.Snack))
            .ToList();

        var context = MealHistoryContext.Build(viele, Heute);

        Assert.Contains($"[v{MealHistoryContext.MaxEntries}]", context.Text);
        Assert.DoesNotContain($"[v{MealHistoryContext.MaxEntries + 1}]", context.Text);
    }

    [Fact]
    public void Build_WithFractionalQuantity_WritesNoTrailingZeros()
    {
        var context = MealHistoryContext.Build(
            [Eintrag("Olivenoel", 12.5m, Heute, new TimeOnly(19, 0), MealType.Dinner)], Heute);

        Assert.Contains("(12.5 g)", context.Text);
    }
}
