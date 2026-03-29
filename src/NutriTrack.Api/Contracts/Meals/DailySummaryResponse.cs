namespace NutriTrack.Api.Contracts.Meals;

public class DailySummaryResponse
{
    public DateOnly Date { get; set; }
    public int TotalEntries { get; set; }

    public decimal TotalCalories { get; set; }
    public decimal TotalProtein { get; set; }
    public decimal TotalCarbohydrates { get; set; }
    public decimal TotalFat { get; set; }
    public decimal TotalFiber { get; set; }
    public decimal TotalSugar { get; set; }
    public decimal TotalSaturatedFat { get; set; }
    public decimal TotalSodium { get; set; }
    public decimal TotalVitaminA { get; set; }
    public decimal TotalVitaminC { get; set; }
    public decimal TotalVitaminD { get; set; }
    public decimal TotalCalcium { get; set; }
    public decimal TotalIron { get; set; }
    public decimal TotalPotassium { get; set; }

    public List<MealEntryResponse> Entries { get; set; } = [];
}
