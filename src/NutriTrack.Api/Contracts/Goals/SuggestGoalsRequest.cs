namespace NutriTrack.Api.Contracts.Goals;

/// <summary>
/// Koerperdaten plus Wunsch in Worten.
///
/// WICHTIG: Von diesem Objekt verlaesst NUR <see cref="Wish"/> den Server. Gewicht, Groesse,
/// Alter und Geschlecht gehen nie an Google - sie werden ausschliesslich hier gebraucht, um mit
/// GoalCalculator zu rechnen. Im unbezahlten Kontingent nutzt Google uebermittelte Inhalte zur
/// Produktverbesserung und laesst sie von Menschen pruefen; Koerperdaten haben dort nichts
/// verloren.
/// </summary>
public class SuggestGoalsRequest
{
    public decimal WeightKg { get; set; }
    public decimal HeightCm { get; set; }
    public int Age { get; set; }

    /// <summary>"male" oder "female" - die Mifflin-St-Jeor-Formel kennt nur diese zwei Konstanten.</summary>
    public string Sex { get; set; } = "male";

    /// <summary>sedentary, light, moderate, active, veryActive.</summary>
    public string ActivityLevel { get; set; } = "sedentary";

    /// <summary>Der Wunsch in eigenen Worten, z. B. "abnehmen, aber nicht hungern".</summary>
    public string Wish { get; set; } = string.Empty;
}

public class GoalsSuggestionResponse
{
    public decimal CalorieGoal { get; set; }
    public decimal ProteinGoal { get; set; }
    public decimal CarbohydrateGoal { get; set; }
    public decimal FatGoal { get; set; }

    /// <summary>Grundumsatz und Erhaltungsbedarf, damit die Zahlen nachvollziehbar bleiben.</summary>
    public decimal BasalMetabolicRate { get; set; }
    public decimal MaintenanceCalories { get; set; }

    /// <summary>Wie die Werte zustande kommen, in einem Satz.</summary>
    public string Explanation { get; set; } = string.Empty;

    /// <summary>Wie der Wunsch verstanden wurde - damit ein Missverstaendnis auffaellt, bevor
    /// die Ziele uebernommen werden.</summary>
    public string InterpretedWish { get; set; } = string.Empty;
}
