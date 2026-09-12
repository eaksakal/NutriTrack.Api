namespace NutriTrack.Api.Contracts.Goals;

public class GoalsResponse
{
    public Guid Id { get; set; }
    public decimal CalorieGoal { get; set; }
    public decimal ProteinGoal { get; set; }
    public decimal CarbohydrateGoal { get; set; }
    public decimal FatGoal { get; set; }
    public DateTime UpdatedAt { get; set; }
}
