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
    ///   "history"        Bezug auf einen eigenen frueheren Eintrag. Die Werte stammen aus dem
    ///                    Tagebuch, nicht vom Modell; SourceEntryId zeigt auf das Original.
    /// </summary>
    public string Source { get; set; } = "generic";

    public List<FoodSearchResponse> Candidates { get; set; } = [];

    public NutrientEstimate? Estimate { get; set; }

    /// <summary>
    /// Der Eintrag, auf den sich dieser Posten bezieht. Gesetzt genau dann, wenn Source
    /// "history" ist. Das Frontend traegt solche Posten ueber POST /api/meals/{id}/repeat ein -
    /// derselbe FoodItem, also keine Dublette mit minimal abweichenden Werten.
    /// </summary>
    public Guid? SourceEntryId { get; set; }

    /// <summary>
    /// Der Zeitpunkt des Originals in Worten ("gestern 21:30"), damit der Nutzer VOR der
    /// Bestaetigung sieht, worauf das Modell sich bezogen hat. Der Bezug ist die eine Stelle, an
    /// der es etwas entscheidet, das niemand getippt hat.
    /// </summary>
    public string? SourceHint { get; set; }
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
