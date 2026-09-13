namespace NutriTrack.Api.Contracts.Meals;

public class PeriodSummaryResponse
{
    public DateOnly From { get; set; }
    public DateOnly To { get; set; }

    /// <summary>Anzahl Kalendertage im Zeitraum, From und To eingeschlossen.</summary>
    public int DaysInPeriod { get; set; }

    /// <summary>Tage mit mindestens einem Eintrag - der Teiler fuer <see cref="Averages"/>.</summary>
    public int DaysWithEntries { get; set; }

    public PeriodAveragesResponse Averages { get; set; } = new();

    public List<PeriodDaySummaryResponse> Days { get; set; } = [];
}

/// <summary>
/// Tagesdurchschnitt ueber die Tage MIT Eintraegen. Leere Tage bleiben aussen vor, weil sonst
/// jeder nicht erfasste Tag den Schnitt nach unten zoege und eine Luecke in der Erfassung wie
/// ein Ernaehrungserfolg aussaehe.
/// </summary>
public class PeriodAveragesResponse
{
    public decimal Calories { get; set; }
    public decimal Protein { get; set; }
    public decimal Carbohydrates { get; set; }
    public decimal Fat { get; set; }
    public decimal Fiber { get; set; }
    public decimal Sugar { get; set; }
    public decimal SaturatedFat { get; set; }
    public decimal Sodium { get; set; }
    public decimal VitaminA { get; set; }
    public decimal VitaminC { get; set; }
    public decimal VitaminD { get; set; }
    public decimal Calcium { get; set; }
    public decimal Iron { get; set; }
    public decimal Potassium { get; set; }
}

/// <summary>
/// Ein Kalendertag des Zeitraums. Auch Tage ohne Eintrag stehen hier (mit TotalEntries = 0),
/// damit das Frontend die Luecken im Tagesstreifen sieht und nicht ueber sie hinwegzeichnet.
/// </summary>
public class PeriodDaySummaryResponse
{
    public DateOnly Date { get; set; }
    public int TotalEntries { get; set; }

    public decimal TotalCalories { get; set; }
    public decimal TotalProtein { get; set; }
    public decimal TotalCarbohydrates { get; set; }
    public decimal TotalFat { get; set; }
}
