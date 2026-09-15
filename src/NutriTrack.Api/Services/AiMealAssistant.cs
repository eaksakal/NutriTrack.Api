using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NutriTrack.Api.Contracts.Ai;
using NutriTrack.Api.Contracts.Food;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Services;

public class AiMealAssistant(
    GeminiService gemini,
    OpenFoodFactsService openFoodFacts,
    IMemoryCache cache,
    AppDbContext db,
    ILogger<AiMealAssistant> logger)
{
    private const int MaxItems = 20;
    private const int CandidatesPerItem = 3;

    // Gesamtfrist fuer ALLE Kandidatensuchen einer Anfrage zusammen. Ohne sie hat der Deckel auf
    // dem Gemini-Aufruf (15 s, Program.cs) keine Wirkung: haengt OpenFoodFacts, lief je Posten der
    // Einzel-Timeout von 10 s ab, bei 20 Posten also ueber drei Minuten — eine Anfrage, die weder
    // Kestrel noch der Browser abbricht, waehrend die Oberflaeche "Denkt nach..." zeigt.
    // 8 s ist so bemessen, dass eine gesunde Fremddatenbank (Antwort im Bereich von 100 ms)
    // muehelos alle Posten schafft. Frueher standen hier 20 s - im Betrieb am 2026-09-12 wartete
    // der Nutzer damit ueber 25 Sekunden auf eine Antwort, die am Ende doch nur Schaetzwerte
    // enthielt, weil OpenFoodFacts gerade bei jedem zweiten Aufruf 503 sagte. Eine schnelle
    // Schaetzung schlaegt eine langsame Schaetzung.
    private const int SearchBudgetSeconds = 8;

    // Mehrere Suchen gleichzeitig, aber nicht alle: OpenFoodFacts ist ein Gratisdienst, und ein
    // Schwall von 20 gleichzeitigen Anfragen je Nutzereingabe ist der schnellste Weg zur
    // IP-Sperre. Vier gleichzeitig holen aus dem Zeitbudget oben das Vierfache heraus, ohne den
    // Fremddienst zu ueberfahren.
    private const int MaxParallelSearches = 4;

    // Naehrwerte je 100 g aendern sich nicht im Minutentakt. Ein kurzer Zwischenspeicher kostet
    // nichts und spart bei den Begriffen, die staendig wiederkehren ("Kaffee", "Broetchen"),
    // genau die Anfragen, die sonst das knappe Suchkontingent aufbrauchen.
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);

    public async Task<ParseMealResponse> ParseAsync(
        IReadOnlyList<ChatMessage> messages, string userId, CancellationToken ct)
    {
        var history = await LoadHistoryAsync(userId, ct);

        GeminiParseResult parsed;
        try
        {
            parsed = await gemini.ParseAsync(messages, history.Text, ct);
        }
        catch (GeminiMalformedResponseException)
        {
            // Genau ein zweiter Anlauf: Modelle straucheln gelegentlich einmalig am Schema.
            // Mehr Versuche kosten Kontingent und Wartezeit, ohne die Trefferquote zu heben.
            logger.LogWarning("Gemini-Antwort unbrauchbar, ein Wiederholungsversuch.");
            parsed = await gemini.ParseAsync(messages, history.Text, ct);
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

        // EINE SUCHE JE VERSCHIEDENEM BEGRIFF, nicht eine je Posten. "zwei Broetchen, dazu noch
        // ein Broetchen mit Butter" nennt denselben Begriff mehrfach, und OpenFoodFacts erlaubt
        // nur 10 Suchen je Minute und IP (siehe OpenFoodFactsThrottle). Jede eingesparte Anfrage
        // ist bares Kontingent - und die Antwort ist fuer denselben Begriff ohnehin dieselbe.
        // NUR Markenprodukte gehen an die Produktdatenbank. Fuer Grundnahrungsmittel und
        // Selbstgekochtes ist sie die schlechtere Quelle, nicht die bessere: sie enthaelt
        // Fertiggerichte, Saucen, Gewuerzmischungen und Rohware unter denselben Suchbegriffen
        // und unterscheidet roh nicht von gekocht. Gemessen am 2026-09-12: "spaghetti bolognese"
        // lieferte 80 bis 315 kcal je 100 g (darunter ein Gewuerzpulver), "spaghetti" allein
        // TROCKENE Nudeln mit 359 - gegen 150 fuer gekochte. Ein Treffer daraus haette den
        // Tageswert still verdoppelt.
        // Erst aufloesen, dann suchen: ein Posten mit Bezug hat seine Werte schon und darf weder
        // Suchbudget noch das Minutenkontingent von OpenFoodFacts verbrauchen.
        var resolved = new Dictionary<GeminiItem, (MealEntry Entry, string Hint)>();
        foreach (var item in items)
        {
            if (item.SourceRef is null)
                continue;

            if (history.TryResolve(item.SourceRef, out var entry, out var hint))
            {
                resolved[item] = (entry, hint);
            }
            else
            {
                // Erfundene Kennungen sind die erwartete Abweichung, kein Fehlerfall: der Posten
                // laeuft den gewoehnlichen Weg. Haeufen sie sich, stimmt etwas mit dem Prompt
                // nicht - deshalb ueberhaupt eine Zeile.
                logger.LogWarning(
                    "Gemini nannte die unbekannte Verlaufskennung {SourceRef}; Posten faellt auf die Schaetzung zurueck.",
                    item.SourceRef);
            }
        }

        var terms = items
            .Where(item => !resolved.ContainsKey(item))
            .Where(IstMarkenprodukt)
            .Select(NormalizeTerm)
            .Where(term => term.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Wird von den Suchaufgaben gesetzt, sobald eine wegen des Kontingents gar nicht erst
        // gestellt wurde. Nur gesetzt, nie zurueckgenommen - ein einfaches bool genuegt.
        var throttled = false;

        async Task<KeyValuePair<string, List<FoodSearchResponse>>> RunAsync(string term)
        {
            await gate.WaitAsync(ct);
            try
            {
                var (candidates, wasThrottled) = await SearchCandidatesAsync(term, budget.Token, ct);
                if (wasThrottled)
                    throttled = true;

                return new KeyValuePair<string, List<FoodSearchResponse>>(term, candidates);
            }
            finally
            {
                gate.Release();
            }
        }

        var searches = terms.Select(RunAsync).ToArray();

        KeyValuePair<string, List<FoodSearchResponse>>[] candidateLists;
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

        var byTerm = candidateLists.ToDictionary(
            entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);

        if (throttled)
        {
            // Sichtbar sagen, warum manche Posten nur geschaetzt sind. Ohne diesen Hinweis wirkt
            // es wie eine Luecke in der Lebensmitteldatenbank statt wie eine Bremse bei uns.
            response.Notice = Join(response.Notice,
                "Das Suchkontingent der Lebensmitteldatenbank war kurz erschöpft; einzelne Posten sind deshalb nur geschätzt. In einer Minute erneut versuchen liefert genauere Werte.");
        }

        foreach (var item in items)
        {
            if (resolved.TryGetValue(item, out var bezug))
            {
                var food = bezug.Entry.FoodItem;

                response.Items.Add(new ParsedItem
                {
                    // Der Name aus dem Tagebuch, nicht der des Modells: der Nutzer erkennt daran,
                    // welcher Eintrag gemeint ist, und genau das soll er vor dem Haken pruefen.
                    Label = food.Name,
                    QuantityInGrams = item.QuantityInGrams,
                    // Die Mahlzeit kommt vom Modell und nicht aus dem Original: der Rest des
                    // Abendessens ist mittags ein Mittagessen.
                    MealType = item.MealType,
                    Source = "history",
                    SourceEntryId = bezug.Entry.Id,
                    SourceHint = bezug.Hint,
                    Candidates = [],
                    Estimate = new NutrientEstimate
                    {
                        Calories = food.Calories,
                        Protein = food.Protein,
                        Carbohydrates = food.Carbohydrates,
                        Fat = food.Fat,
                        Fiber = food.Fiber,
                        Sugar = food.Sugar,
                        SaturatedFat = food.SaturatedFat,
                        Sodium = food.Sodium
                    }
                });

                continue;
            }

            var markenprodukt = IstMarkenprodukt(item);
            var candidates = markenprodukt && byTerm.TryGetValue(NormalizeTerm(item), out var found)
                ? found
                : [];

            response.Items.Add(new ParsedItem
            {
                Label = item.Label,
                QuantityInGrams = item.QuantityInGrams,
                MealType = item.MealType,
                // "generic" ist kein Rueckfall, sondern die gewaehlte Quelle - deshalb von
                // "estimate" getrennt, das fuer ein Markenprodukt OHNE Datenbanktreffer steht.
                Source = candidates.Count > 0 ? "openfoodfacts"
                       : markenprodukt ? "estimate"
                       : "generic",
                Candidates = candidates,
                Estimate = item.Estimate
            });
        }

        return response;
    }

    private static bool IstMarkenprodukt(GeminiItem item) =>
        string.Equals(item.ProductKind, "branded", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeTerm(GeminiItem item) => NormalizeTerm(item.SearchTerm);

    private static string NormalizeTerm(string? searchTerm) => (searchTerm ?? string.Empty).Trim();

    private static string Join(string? existing, string addition) =>
        string.IsNullOrWhiteSpace(existing) ? addition : $"{existing} {addition}";

    /// <returns>
    /// Die Kandidaten und ob die Anfrage am Suchkontingent gescheitert ist. Die Unterscheidung
    /// ist wichtig: "nichts gefunden" ist ein Ergebnis, "nicht gefragt" ist ein Hinweis wert.
    /// </returns>
    private async Task<(List<FoodSearchResponse> Candidates, bool Throttled)> SearchCandidatesAsync(
        string searchTerm, CancellationToken budgetToken, CancellationToken callerToken)
    {
        var cacheKey = $"off-search:{searchTerm.ToLowerInvariant()}";
        if (cache.TryGetValue(cacheKey, out List<FoodSearchResponse>? cached) && cached is not null)
            return (cached, false);

        List<OpenFoodFactsProduct> products;
        try
        {
            // Ohne Wiederholung: hier haengen mehrere Begriffe an einer Eingabe, und fuer jeden
            // gibt es mit der Schaetzung bereits einen Rueckfall. Die Suchseite wiederholt sehr
            // wohl - dort wartet ein Mensch auf genau dieses eine Ergebnis.
            products = await openFoodFacts.SearchAsync(
                searchTerm, page: 1, pageSize: CandidatesPerItem * 2, ct: budgetToken,
                retryOnFailure: false);
        }
        // Gesamtfrist abgelaufen (nicht der Nutzer hat abgebrochen): die restlichen Posten fallen
        // auf ihre Schaetzung zurueck. Eine halbe Antwort jetzt ist besser als eine vollstaendige
        // in drei Minuten - und der Nutzer sieht am Merkmal "geschaetzt", woran er ist.
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Zeitbudget von {Seconds} s fuer die Kandidatensuche erschoepft; {SearchTerm} nutzt die Schaetzung.",
                SearchBudgetSeconds, searchTerm);
            return ([], false);
        }
        // Gar nicht erst gefragt, weil das Kontingent leer war. Der Posten faellt auf die
        // Schaetzung zurueck UND der Nutzer erfaehrt den Grund - anders als beim Ausfall, wo es
        // nichts gibt, das er anders machen koennte.
        catch (OpenFoodFactsThrottledException)
        {
            logger.LogWarning(
                "Suchkontingent erschoepft; {SearchTerm} nutzt die Schaetzung statt einer Anfrage.", searchTerm);
            return ([], true);
        }
        catch (OpenFoodFactsUnavailableException ex)
        {
            // Der KI-Pfad soll nicht scheitern, nur weil die Fremddatenbank streikt — der Posten
            // faellt auf die Schaetzung zurueck und ist als solche gekennzeichnet.
            logger.LogWarning(ex, "Kandidatensuche fuer {SearchTerm} fehlgeschlagen, nutze Schaetzung.", searchTerm);
            return ([], false);
        }

        var candidates = products
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

        // Auch ein leeres Ergebnis wird gemerkt: "kennt OpenFoodFacts nicht" bleibt eine Minute
        // spaeter richtig, und genau die Begriffe ohne Treffer wiederholen sich beim Nachfassen.
        cache.Set(cacheKey, candidates, CacheDuration);

        return (candidates, false);
    }

    /// <summary>
    /// Die juengsten Eintraege der letzten <see cref="MealHistoryContext.Days"/> Tage. Absteigend,
    /// weil der Deckel dann die aeltesten abschneidet - auf die bezieht sich am seltensten jemand.
    /// </summary>
    private async Task<MealHistoryContext> LoadHistoryAsync(string userId, CancellationToken ct)
    {
        // DateTime.Now und nicht UtcNow: Date und Time der Eintraege sind lokale Angaben (siehe
        // den repeat-Endpunkt), und "gestern" muss dieselbe Grenze meinen wie dort.
        var heute = DateOnly.FromDateTime(DateTime.Now);
        var seit = heute.AddDays(-(MealHistoryContext.Days - 1));

        List<MealEntry> entries;
        try
        {
            entries = await db.MealEntries
                .Include(entry => entry.FoodItem)
                // Obere Grenze noetig: /api/meals prueft das Datum nicht gegen die Zukunft, und
                // ohne "<= heute" wuerde ein vorgetragener Eintrag per OrderByDescending ganz oben
                // stehen und einem echten juengeren Eintrag den Platz unter dem 40er-Deckel nehmen.
                .Where(entry => entry.UserId == userId && entry.Date >= seit && entry.Date <= heute)
                .OrderByDescending(entry => entry.Date)
                .ThenByDescending(entry => entry.Time)
                .Take(MealHistoryContext.MaxEntries)
                .ToListAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Abbruch durch den Aufrufer ist kein Ladefehler - er soll durchschlagen und nicht als
            // "Verlauf nicht ladbar" enden.
            throw;
        }
        catch (Exception ex)
        {
            // Vor diesem Feature fasste die Erfassung die Datenbank gar nicht an; ein Ausfall hier
            // (z. B. SQLITE_BUSY unter Schreiblast, kein WAL-Modus) waere sonst eine Regression, die
            // an den drei Gemini-catch-Bloecken in AiEndpoints vorbeifliegt und einen blanken 500
            // erzeugt. Die Erfassung laeuft stattdessen im bereits getesteten Pfad "kein Verlauf"
            // weiter - nur ohne Verlaufsbezug.
            logger.LogWarning(ex, "Verlauf konnte nicht geladen werden, KI-Erfassung laeuft ohne Verlaufsbezug weiter.");
            return MealHistoryContext.Empty;
        }

        return MealHistoryContext.Build(entries, heute);
    }
}
