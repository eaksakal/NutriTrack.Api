namespace NutriTrack.Domain.Entities;

public class UserGoal
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;

    // Tagesziele, absolute Werte (kcal bzw. Gramm) - nicht pro 100 g wie bei FoodItem.
    public decimal CalorieGoal { get; set; }
    public decimal ProteinGoal { get; set; }
    public decimal CarbohydrateGoal { get; set; }
    public decimal FatGoal { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
