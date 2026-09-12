using System.Text.Json;
using System.Text.Json.Serialization;

namespace NutriTrack.Api.Services;

/// <summary>Die Fremddatenbank ist nicht erreichbar oder antwortet unbrauchbar.</summary>
public class OpenFoodFactsUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Wir haben gar nicht erst gefragt, weil das eigene Suchkontingent erschoepft war. Bewusst von
/// <see cref="OpenFoodFactsUnavailableException"/> getrennt: "nicht gefragt" ist ein anderer
/// Zustand als "gefragt und keine Antwort bekommen", und der Nutzer bekommt eine andere Auskunft.
/// </summary>
public class OpenFoodFactsThrottledException(string message) : Exception(message);

public class OpenFoodFactsService(
    HttpClient httpClient,
    OpenFoodFactsThrottle throttle,
    TimeProvider timeProvider,
    ILogger<OpenFoodFactsService> logger)
{
    // Beobachtet am 2026-09-12: die Suche von OpenFoodFacts antwortet zeitweise bei JEDEM ZWEITEN
    // Aufruf mit 503, ohne dass ein Limit verletzt waere - fuenf Aufrufe im Abstand von zwei
    // Sekunden ergaben 503/200/503/200/503. Ein einziger Wiederholungsversuch druckt die
    // Fehlerquote von rund der Haelfte auf etwa ein Viertel; ohne ihn steht der KI-Pfad meistens
    // auf Schaetzwerten, obwohl es echte Daten gaebe. Mehr als ein Versuch lohnt nicht: er kostet
    // Suchkontingent und Wartezeit, und wer zweimal hintereinander 503 sagt, meint es ernst.
    private const int MaxAttempts = 2;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(400);

    public async Task<List<OpenFoodFactsProduct>> SearchAsync(
        string query, int page = 1, int pageSize = 20, CancellationToken ct = default)
    {
        var url = $"https://world.openfoodfacts.org/cgi/search.pl?search_terms={Uri.EscapeDataString(query)}&search_simple=1&action=process&json=1&page={page}&page_size={pageSize}";

        Exception? letzterFehler = null;

        for (var versuch = 1; versuch <= MaxAttempts; versuch++)
        {
            // Das Kontingent wird HIER genommen, nicht beim Aufrufer: nur so zaehlen alle Wege in
            // die Suche mit - die Suchseite ebenso wie der KI-Pfad - und ein Wiederholungsversuch
            // zaehlt als das, was er ist, naemlich als weitere Anfrage an den Fremddienst.
            if (!throttle.TryAcquireSearch())
            {
                if (letzterFehler is null)
                    throw new OpenFoodFactsThrottledException("Suchkontingent erschoepft.");

                // Kein Kontingent fuer den zweiten Anlauf: der erste Fehler zaehlt.
                break;
            }

            try
            {
                var response = await httpClient.GetFromJsonAsync<OpenFoodFactsSearchResponse>(url, ct);
                return response?.Products ?? [];
            }
            // Muss VOR dem Ausfall-Griff stehen: ein Abbruch durch den Aufrufer kommt ebenfalls als
            // TaskCanceledException an, ist aber kein Ausfall der Fremddatenbank. Wer abbricht, will
            // ein Ende sehen und keinen Rueckfall auf Schaetzwerte.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                letzterFehler = ex;

                if (versuch < MaxAttempts)
                {
                    logger.LogInformation(
                        "OpenFoodFacts-Suche fuer {Query} fehlgeschlagen (Versuch {Versuch}), ein zweiter Anlauf.",
                        query, versuch);
                    await Task.Delay(RetryDelay, timeProvider, ct);
                }
            }
        }

        // Ohne diesen Griff erreicht der Ausfall den Nutzer als 500 mit leerem Body. Wichtiger
        // noch: der KI-Pfad muss "nichts gefunden" von "Dienst kaputt" unterscheiden koennen,
        // sonst schaetzt er Naehrwerte, obwohl es echte Daten gaebe.
        logger.LogWarning(letzterFehler, "OpenFoodFacts-Suche fuer {Query} endgueltig fehlgeschlagen.", query);
        throw new OpenFoodFactsUnavailableException("OpenFoodFacts ist nicht erreichbar.", letzterFehler);
    }

    public async Task<OpenFoodFactsProduct?> GetByBarcodeAsync(string barcode, CancellationToken ct = default)
    {
        try
        {
            var response = await httpClient.GetFromJsonAsync<OpenFoodFactsBarcodeResponse>(
                $"https://world.openfoodfacts.org/api/v0/product/{barcode}.json", ct);
            return response is { Status: 1 } ? response.Product : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "OpenFoodFacts-Abfrage fuer Barcode {Barcode} fehlgeschlagen.", barcode);
            throw new OpenFoodFactsUnavailableException("OpenFoodFacts ist nicht erreichbar.", ex);
        }
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
