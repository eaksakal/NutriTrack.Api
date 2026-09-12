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

    /// <summary>
    /// Woher die Werte stammen. Drei Zustaende, die bewusst unterschieden werden, weil sie fuer
    /// den Nutzer Verschiedenes bedeuten:
    ///   "openfoodfacts"  Treffer in der Produktdatenbank, Candidates ist gefuellt.
    ///   "generic"        Standardwert fuer ein Grundnahrungsmittel oder ein selbst gekochtes
    ///                    Gericht. KEIN Rueckfall, sondern die bessere Quelle: die Datenbank
    ///                    kennt fuer "Spaghetti" nur trockene Nudeln (360 statt 150 kcal je 100 g).
    ///   "estimate"       Rueckfall. Es war ein Markenprodukt, aber die Datenbank hatte nichts
    ///                    oder war nicht erreichbar.
    /// </summary>
    public string Source { get; set; } = "generic";

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
