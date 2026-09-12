using NutriTrack.Api.Contracts.Food;

namespace NutriTrack.Api.Contracts.Ai;

public class ParseMealResponse
{
    /// <summary>Gesetzt, wenn die KI nachfragt. Dann ist Items leer — beides zugleich gibt es nicht.</summary>
    public string? Question { get; set; }

    public List<ParsedItem> Items { get; set; } = [];

    /// <summary>Gesetzt, wenn die Postenliste gekappt wurde. Die Oberflaeche benennt das sichtbar;
    /// stilles Abschneiden waere schlimmer als eine Ablehnung.</summary>
    public string? Notice { get; set; }
}

public class ParsedItem
{
    public string Label { get; set; } = string.Empty;
    public decimal QuantityInGrams { get; set; }
    public string MealType { get; set; } = "Snack";

    /// <summary>"openfoodfacts", wenn Candidates gefuellt ist, sonst "estimate".</summary>
    public string Source { get; set; } = "estimate";

    public List<FoodSearchResponse> Candidates { get; set; } = [];

    public NutrientEstimate? Estimate { get; set; }
}

public class NutrientEstimate
{
    public decimal Calories { get; set; }
    public decimal Protein { get; set; }
    public decimal Carbohydrates { get; set; }
    public decimal Fat { get; set; }
    public decimal? Fiber { get; set; }
    public decimal? Sugar { get; set; }
    public decimal? SaturatedFat { get; set; }
    public decimal? Sodium { get; set; }
}
