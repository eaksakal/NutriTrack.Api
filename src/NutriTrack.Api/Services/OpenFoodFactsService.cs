using System.Text.Json;
using System.Text.Json.Serialization;

namespace NutriTrack.Api.Services;

public class OpenFoodFactsService(HttpClient httpClient)
{
    public async Task<List<OpenFoodFactsProduct>> SearchAsync(string query, int page = 1, int pageSize = 20)
    {
        var url = $"https://world.openfoodfacts.org/cgi/search.pl?search_terms={Uri.EscapeDataString(query)}&search_simple=1&action=process&json=1&page={page}&page_size={pageSize}";
        var response = await httpClient.GetFromJsonAsync<OpenFoodFactsSearchResponse>(url);
        return response?.Products ?? [];
    }

    public async Task<OpenFoodFactsProduct?> GetByBarcodeAsync(string barcode)
    {
        var response = await httpClient.GetFromJsonAsync<OpenFoodFactsBarcodeResponse>(
            $"https://world.openfoodfacts.org/api/v0/product/{barcode}.json");
        return response is { Status: 1 } ? response.Product : null;
    }
}

public class OpenFoodFactsSearchResponse
{
    [JsonPropertyName("products")]
    public List<OpenFoodFactsProduct> Products { get; set; } = [];

    [JsonPropertyName("count")]
    public int Count { get; set; }
}

public class OpenFoodFactsBarcodeResponse
{
    [JsonPropertyName("status")]
    public int Status { get; set; }

    [JsonPropertyName("product")]
    public OpenFoodFactsProduct? Product { get; set; }
}

public class OpenFoodFactsProduct
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("product_name")]
    public string? ProductName { get; set; }

    [JsonPropertyName("brands")]
    public string? Brands { get; set; }

    [JsonPropertyName("nutriments")]
    public OpenFoodFactsNutriments? Nutriments { get; set; }
}

public class OpenFoodFactsNutriments
{
    [JsonPropertyName("energy-kcal_100g")]
    public decimal? EnergyKcal100g { get; set; }

    [JsonPropertyName("proteins_100g")]
    public decimal? Proteins100g { get; set; }

    [JsonPropertyName("carbohydrates_100g")]
    public decimal? Carbohydrates100g { get; set; }

    [JsonPropertyName("fat_100g")]
    public decimal? Fat100g { get; set; }

    [JsonPropertyName("fiber_100g")]
    public decimal? Fiber100g { get; set; }

    [JsonPropertyName("sugars_100g")]
    public decimal? Sugars100g { get; set; }

    [JsonPropertyName("saturated-fat_100g")]
    public decimal? SaturatedFat100g { get; set; }

    [JsonPropertyName("sodium_100g")]
    public decimal? Sodium100g { get; set; }

    [JsonPropertyName("vitamin-a_100g")]
    public decimal? VitaminA100g { get; set; }

    [JsonPropertyName("vitamin-c_100g")]
    public decimal? VitaminC100g { get; set; }

    [JsonPropertyName("vitamin-d_100g")]
    public decimal? VitaminD100g { get; set; }

    [JsonPropertyName("calcium_100g")]
    public decimal? Calcium100g { get; set; }

    [JsonPropertyName("iron_100g")]
    public decimal? Iron100g { get; set; }

    [JsonPropertyName("potassium_100g")]
    public decimal? Potassium100g { get; set; }
}
