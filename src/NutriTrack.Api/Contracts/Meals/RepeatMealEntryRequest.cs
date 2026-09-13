using System.ComponentModel.DataAnnotations;

namespace NutriTrack.Api.Contracts.Meals;

/// <summary>
/// Traegt einen bereits erfassten Posten ein ZWEITES Mal ein. Alle Felder sind optional: ohne
/// Angabe gelten Menge und Mahlzeit des Originals, Datum und Uhrzeit werden zu "jetzt".
/// </summary>
/// <remarks>
/// Bewusst kein zweiter Weg ueber POST /api/meals: dort muesste das Frontend die Naehrwerte je
/// 100 g mitschicken, die es gar nicht hat - die Antwort liefert nur die auf die Menge
/// hochgerechneten Werte. Zuruechrechnen waere rundungsbehaftet und legte ueber
/// HasSameNutrients einen zweiten, leicht abweichenden FoodItem an. Hier wird stattdessen
/// dieselbe FoodItemId weiterverwendet.
/// </remarks>
public class RepeatMealEntryRequest
{
    // Siehe CreateMealEntryRequest: das Attribut dokumentiert nur, durchgesetzt wird die Regel
    // im Handler ueber IsValidQuantity.
    [Range(0, 10000, MinimumIsExclusive = true)]
    public decimal? QuantityInGrams { get; set; }

    public string? MealType { get; set; }

    public DateOnly? Date { get; set; }
    public TimeOnly? Time { get; set; }
}
