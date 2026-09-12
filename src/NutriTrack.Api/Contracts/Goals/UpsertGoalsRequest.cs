using System.ComponentModel.DataAnnotations;

namespace NutriTrack.Api.Contracts.Goals;

public class UpsertGoalsRequest
{
    [Range(0.1, 100000)]
    public decimal CalorieGoal { get; set; }

    [Range(0.1, 100000)]
    public decimal ProteinGoal { get; set; }

    [Range(0.1, 100000)]
    public decimal CarbohydrateGoal { get; set; }

    [Range(0.1, 100000)]
    public decimal FatGoal { get; set; }
}
