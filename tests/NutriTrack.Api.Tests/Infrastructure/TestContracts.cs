namespace NutriTrack.Api.Tests.Infrastructure;

/// <summary>
/// Bewusst eigene DTOs statt der Produktiv-Contracts: die Tests sollen den verabredeten
/// Wire-Vertrag pruefen und nicht automatisch jeder Umbenennung im Produktivcode folgen.
/// </summary>
public sealed record GoalsResponseDto(
    Guid Id,
    decimal CalorieGoal,
    decimal ProteinGoal,
    decimal CarbohydrateGoal,
    decimal FatGoal,
    DateTime UpdatedAt);

public sealed record UpsertGoalsRequestDto(
    decimal CalorieGoal,
    decimal ProteinGoal,
    decimal CarbohydrateGoal,
    decimal FatGoal);

public sealed record AuthResponseDto(string Token, DateTime ExpiresAt, string Email);

public sealed record MeResponseDto(string UserId, string Email);

public class ParseMealResponseDto
{
    public string? Question { get; set; }
    public string? Notice { get; set; }
    public List<ParsedItemDto> Items { get; set; } = [];
}

public class ParsedItemDto
{
    public string Label { get; set; } = string.Empty;
    public decimal QuantityInGrams { get; set; }
    public string MealType { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public List<FoodSearchDto> Candidates { get; set; } = [];
    public NutrientEstimateDto? Estimate { get; set; }
}

/// <summary>
/// Ausschnitt des Kandidaten-Vertrags: geprueft wird hier nur, was der KI-Pfad zusagt. Die
/// vollstaendige Abbildung eines Suchtreffers sichern bereits die Food-Tests ab.
/// </summary>
public class FoodSearchDto
{
    public string Name { get; set; } = string.Empty;
    public string? Brand { get; set; }
    public string? Barcode { get; set; }
    public decimal Calories { get; set; }
    public decimal Protein { get; set; }
    public decimal Carbohydrates { get; set; }
    public decimal Fat { get; set; }
}

public class NutrientEstimateDto
{
    public decimal Calories { get; set; }
    public decimal Protein { get; set; }
    public decimal Carbohydrates { get; set; }
    public decimal Fat { get; set; }
}

public class GoalsSuggestionDto
{
    public decimal CalorieGoal { get; set; }
    public decimal ProteinGoal { get; set; }
    public decimal CarbohydrateGoal { get; set; }
    public decimal FatGoal { get; set; }
    public decimal BasalMetabolicRate { get; set; }
    public decimal MaintenanceCalories { get; set; }
    public string Explanation { get; set; } = string.Empty;
    public string InterpretedWish { get; set; } = string.Empty;
}
