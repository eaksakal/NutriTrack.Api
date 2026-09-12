using NutriTrack.Api.Contracts.Ai;
using NutriTrack.Api.Contracts.Food;

namespace NutriTrack.Api.Services;

public class AiMealAssistant(GeminiService gemini, OpenFoodFactsService openFoodFacts, ILogger<AiMealAssistant> logger)
{
    private const int MaxItems = 20;
    private const int CandidatesPerItem = 3;

    // Gesamtfrist fuer ALLE Kandidatensuchen einer Anfrage zusammen. Ohne sie hat der Deckel auf
    // dem Gemini-Aufruf (15 s, Program.cs) keine Wirkung: haengt OpenFoodFacts, lief je Posten der
    // Einzel-Timeout von 10 s ab, bei 20 Posten also ueber drei Minuten — eine Anfrage, die weder
    // Kestrel noch der Browser abbricht, waehrend die Oberflaeche "Denkt nach..." zeigt.
    // 20 s ist so bemessen, dass eine gesunde Fremddatenbank (Antwort im Bereich von 100 ms)
    // muehelos alle Posten schafft, eine kranke aber nach zwei Einzel-Timeouts Schluss ist.
    private const int SearchBudgetSeconds = 20;

    // Mehrere Suchen gleichzeitig, aber nicht alle: OpenFoodFacts ist ein Gratisdienst, und ein
    // Schwall von 20 gleichzeitigen Anfragen je Nutzereingabe ist der schnellste Weg zur
    // IP-Sperre. Vier gleichzeitig holen aus dem Zeitbudget oben das Vierfache heraus, ohne den
    // Fremddienst zu ueberfahren.
    private const int MaxParallelSearches = 4;

    public async Task<ParseMealResponse> ParseAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
    {
        GeminiParseResult parsed;
        try
        {
            parsed = await gemini.ParseAsync(messages, ct);
        }
        catch (GeminiMalformedResponseException)
        {
            // Genau ein zweiter Anlauf: Modelle straucheln gelegentlich einmalig am Schema.
            // Mehr Versuche kosten Kontingent und Wartezeit, ohne die Trefferquote zu heben.
            logger.LogWarning("Gemini-Antwort unbrauchbar, ein Wiederholungsversuch.");
            parsed = await gemini.ParseAsync(messages, ct);
        }

        if (!string.IsNullOrWhiteSpace(parsed.Question))
            return new ParseMealResponse { Question = parsed.Question };

        var response = new ParseMealResponse();

        // Liefert das Modell mehr Posten als erlaubt, ist das SEIN Fehler und nicht der des
        // Nutzers — ein 400er waere hier die falsche Antwort. Gekappt wird sichtbar, nicht still.
        var items = parsed.Items;
        if (items.Count > MaxItems)
        {
            logger.LogWarning("Gemini lieferte {Count} Posten, gekappt auf {Max}.", items.Count, MaxItems);
            response.Notice = $"Nur die ersten {MaxItems} Posten übernommen.";
            items = items.Take(MaxItems).ToList();
        }

        // Die Frist haengt am Token des Aufrufers: bricht der Browser ab, enden auch die noch
        // laufenden Suchen sofort, statt die Verbindungen bis zum Ende der Liste zu belegen.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(SearchBudgetSeconds));

        // Bewusst ohne using: die Semaphore wird von Aufgaben freigegeben, die beim Abbruch noch
        // laufen. Ein Release auf ein entsorgtes Objekt waere eine ObjectDisposedException in
        // einer Aufgabe, die niemand mehr beobachtet. Ohne AvailableWaitHandle gibt es hier
        // ohnehin nichts zu entsorgen.
        var gate = new SemaphoreSlim(MaxParallelSearches, MaxParallelSearches);

        async Task<List<FoodSearchResponse>> RunAsync(GeminiItem item)
        {
            await gate.WaitAsync(ct);
            try
            {
                return await SearchCandidatesAsync(item.SearchTerm, budget.Token, ct);
            }
            finally
            {
                gate.Release();
            }
        }

        // Task.WhenAll behaelt die Reihenfolge der Aufgaben bei - die Posten bleiben also in der
        // Reihenfolge, in der das Modell sie genannt hat, obwohl die Suchen sich ueberholen.
        var searches = items.Select(RunAsync).ToArray();

        List<FoodSearchResponse>[] candidateLists;
        try
        {
            candidateLists = await Task.WhenAll(searches);
        }
        catch (OperationCanceledException)
        {
            // Der Aufrufer hat abgebrochen. Erst wenn wirklich alle Suchen zum Stehen gekommen
            // sind, darf die CancellationTokenSource aus dem using oben verschwinden - sonst
            // laeuft eine noch wartende Aufgabe in eine ObjectDisposedException.
            foreach (var search in searches)
            {
                try { await search; }
                catch { /* Der Grund ist derselbe Abbruch; er wird gleich weitergereicht. */ }
            }

            throw;
        }

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var candidates = candidateLists[index];

            response.Items.Add(new ParsedItem
            {
                Label = item.Label,
                QuantityInGrams = item.QuantityInGrams,
                MealType = item.MealType,
                Source = candidates.Count > 0 ? "openfoodfacts" : "estimate",
                Candidates = candidates,
                Estimate = item.Estimate
            });
        }

        return response;
    }

    private async Task<List<FoodSearchResponse>> SearchCandidatesAsync(
        string searchTerm, CancellationToken budgetToken, CancellationToken callerToken)
    {
        List<OpenFoodFactsProduct> products;
        try
        {
            products = await openFoodFacts.SearchAsync(
                searchTerm, page: 1, pageSize: CandidatesPerItem * 2, ct: budgetToken);
        }
        // Gesamtfrist abgelaufen (nicht der Nutzer hat abgebrochen): die restlichen Posten fallen
        // auf ihre Schaetzung zurueck. Eine halbe Antwort jetzt ist besser als eine vollstaendige
        // in drei Minuten - und der Nutzer sieht am Merkmal "geschaetzt", woran er ist.
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Zeitbudget von {Seconds} s fuer die Kandidatensuche erschoepft; {SearchTerm} nutzt die Schaetzung.",
                SearchBudgetSeconds, searchTerm);
            return [];
        }
        catch (OpenFoodFactsUnavailableException ex)
        {
            // Der KI-Pfad soll nicht scheitern, nur weil die Fremddatenbank streikt — der Posten
            // faellt auf die Schaetzung zurueck und ist als solche gekennzeichnet.
            logger.LogWarning(ex, "Kandidatensuche fuer {SearchTerm} fehlgeschlagen, nutze Schaetzung.", searchTerm);
            return [];
        }

        return products
            .Where(p => p.ProductName is not null)
            .Take(CandidatesPerItem)
            .Select(p => new FoodSearchResponse
            {
                Name = p.ProductName!,
                Brand = p.Brands,
                Barcode = p.Code,
                Calories = p.Nutriments?.EnergyKcal100g ?? 0,
                Protein = p.Nutriments?.Proteins100g ?? 0,
                Carbohydrates = p.Nutriments?.Carbohydrates100g ?? 0,
                Fat = p.Nutriments?.Fat100g ?? 0,
                Fiber = p.Nutriments?.Fiber100g,
                Sugar = p.Nutriments?.Sugars100g,
                SaturatedFat = p.Nutriments?.SaturatedFat100g,
                Sodium = p.Nutriments?.Sodium100g,
                VitaminA = p.Nutriments?.VitaminA100g,
                VitaminC = p.Nutriments?.VitaminC100g,
                VitaminD = p.Nutriments?.VitaminD100g,
                Calcium = p.Nutriments?.Calcium100g,
                Iron = p.Nutriments?.Iron100g,
                Potassium = p.Nutriments?.Potassium100g
            })
            .ToList();
    }
}
