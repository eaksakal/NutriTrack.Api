namespace NutriTrack.Api.Contracts.Meals;

public class MealEntryResponse
{
    public Guid Id { get; set; }
    public string FoodName { get; set; } = string.Empty;
    public string? Brand { get; set; }
    public decimal QuantityInGrams { get; set; }
    public string MealType { get; set; } = string.Empty;
    public DateOnly Date { get; set; }
    public TimeOnly Time { get; set; }

    // Calculated based on quantity
    public decimal Calories { get; set; }
    public decimal Protein { get; set; }
    public decimal Carbohydrates { get; set; }
    public decimal Fat { get; set; }
    public decimal? Fiber { get; set; }
    public decimal? Sugar { get; set; }
    public decimal? SaturatedFat { get; set; }
    public decimal? Sodium { get; set; }
    public decimal? VitaminA { get; set; }
    public decimal? VitaminC { get; set; }
    public decimal? VitaminD { get; set; }
    public decimal? Calcium { get; set; }
    public decimal? Iron { get; set; }
    public decimal? Potassium { get; set; }
}
