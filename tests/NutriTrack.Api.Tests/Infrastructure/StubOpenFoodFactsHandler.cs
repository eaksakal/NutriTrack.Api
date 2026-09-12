using System.Net;
using System.Text;

namespace NutriTrack.Api.Tests.Infrastructure;

/// <summary>
/// Ersetzt den Primary-Handler des OpenFoodFactsService. Die Tests duerfen nicht gegen die echte
/// OpenFoodFacts-API laufen, sonst haengt das Ergebnis an Netzwerk und Fremddaten.
/// Liefert <paramref name="responder"/> eine Antwort, gewinnt sie — darueber stellt ein Test den
/// Ausfall des Fremddienstes nach, ohne das uebrige Stub-Verhalten anzufassen.
/// </summary>
// Der Rueckgabetyp ist nullbar, nicht nur die Delegate-Referenz: "null" heisst hier ausdruecklich
// "kein Ausfall gesetzt, nimm das normale Stub-Verhalten".
public sealed class StubOpenFoodFactsHandler(Func<HttpRequestMessage, HttpResponseMessage?>? responder = null)
    : HttpMessageHandler
{
    public const string KnownBarcode = "4000417025005";
    public const string UnknownBarcode = "0000000000000";

    public const string SearchProductName = "Stub Haferflocken";
    public const string SearchProductBrand = "StubBrand";
    public const decimal SearchProductCalories = 372m;

    /// <summary>Suchbegriff, zu dem der Stub bewusst nichts findet — fuer den Schaetzungs-Rueckfall.</summary>
    public const string UnknownSearchTerm = "hausmannskost-ohne-treffer";

    public const string BarcodeProductName = "Stub Vollmilch";
    public const string BarcodeProductBrand = "StubMolkerei";
    public const decimal BarcodeProductCalories = 64m;

    private const string SearchJson = $$"""
    {
      "count": 2,
      "products": [
        {
          "code": "1111111111111",
          "product_name": "{{SearchProductName}}",
          "brands": "{{SearchProductBrand}}",
          "nutriments": {
            "energy-kcal_100g": 372,
            "proteins_100g": 13.5,
            "carbohydrates_100g": 58.7,
            "fat_100g": 7,
            "fiber_100g": 10,
            "sugars_100g": 1.1,
            "saturated-fat_100g": 1.3,
            "sodium_100g": 0.01,
            "calcium_100g": 0.054,
            "iron_100g": 0.0045
          }
        },
        {
          "code": "2222222222222",
          "product_name": null,
          "brands": "NoName",
          "nutriments": {
            "energy-kcal_100g": 100
          }
        }
      ]
    }
    """;

    private const string KnownProductJson = $$"""
    {
      "status": 1,
      "code": "{{KnownBarcode}}",
      "product": {
        "code": "{{KnownBarcode}}",
        "product_name": "{{BarcodeProductName}}",
        "brands": "{{BarcodeProductBrand}}",
        "nutriments": {
          "energy-kcal_100g": 64,
          "proteins_100g": 3.4,
          "carbohydrates_100g": 4.8,
          "fat_100g": 3.6,
          "saturated-fat_100g": 2.3,
          "calcium_100g": 0.12
        }
      }
    }
    """;

    private const string EmptySearchJson = """
    { "count": 0, "products": [] }
    """;

    private const string UnknownProductJson = """
    {
      "status": 0,
      "status_verbose": "product not found"
    }
    """;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (responder?.Invoke(request) is { } overridden)
            return Task.FromResult(overridden);

        var url = request.RequestUri!.ToString();

        if (url.Contains("/cgi/search.pl", StringComparison.Ordinal)
            && url.Contains(Uri.EscapeDataString(UnknownSearchTerm), StringComparison.Ordinal))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(EmptySearchJson, Encoding.UTF8, "application/json")
            });
        }

        var json = url.Contains("/cgi/search.pl", StringComparison.Ordinal)
            ? SearchJson
            : url.Contains($"/product/{KnownBarcode}.json", StringComparison.Ordinal)
                ? KnownProductJson
                : UnknownProductJson;

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
    }
}
