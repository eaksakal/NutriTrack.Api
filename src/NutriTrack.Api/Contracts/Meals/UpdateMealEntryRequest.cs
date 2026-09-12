using System.ComponentModel.DataAnnotations;

namespace NutriTrack.Api.Contracts.Meals;

public class UpdateMealEntryRequest
{
    // Nur Dokumentation, durchgesetzt wird die Regel im Handler (MealEndpoints.IsValidQuantity):
    // Minimal APIs werten DataAnnotations ohne AddValidation() nicht aus.
    [Range(0, 10000, MinimumIsExclusive = true)]
    public decimal QuantityInGrams { get; set; }

    [Required]
    public string MealType { get; set; } = "Snack";

    public DateOnly? Date { get; set; }
    public TimeOnly? Time { get; set; }
}
