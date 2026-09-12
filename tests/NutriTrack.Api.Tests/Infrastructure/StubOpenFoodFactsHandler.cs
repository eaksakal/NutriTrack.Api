using System.Net;
using System.Text;

namespace NutriTrack.Api.Tests.Infrastructure;

/// <summary>
/// Ersetzt den Primary-Handler des OpenFoodFactsService. Die Tests duerfen nicht gegen die echte
/// OpenFoodFacts-API laufen, sonst haengt das Ergebnis an Netzwerk und Fremddaten.
/// </summary>
public sealed class StubOpenFoodFactsHandler : HttpMessageHandler
{
    public const string KnownBarcode = "4000417025005";
    public const string UnknownBarcode = "0000000000000";

    public const string SearchProductName = "Stub Haferflocken";
    public const string SearchProductBrand = "StubBrand";
    public const decimal SearchProductCalories = 372m;

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

    private const string UnknownProductJson = """
    {
      "status": 0,
      "status_verbose": "product not found"
    }
    """;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

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
