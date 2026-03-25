namespace NutriTrack.Domain.Entities;

public class MealEntry
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public Guid FoodItemId { get; set; }
    public FoodItem FoodItem { get; set; } = null!;

    public decimal QuantityInGrams { get; set; }
    public MealType MealType { get; set; }
    public DateOnly Date { get; set; }
    public TimeOnly Time { get; set; }

    public DateTime CreatedAt { get; set; }
}

public enum MealType
{
    Breakfast,
    Lunch,
    Dinner,
    Snack
}
