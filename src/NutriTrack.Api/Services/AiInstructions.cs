using NutriTrack.Domain.Entities;

namespace NutriTrack.Api.Services;

/// <summary>
/// Anbieterneutrale Fachregeln, die jede IAiProvider-Umsetzung braucht: die Systemanweisungen fuer
/// Mahlzeiten- und Zielwunsch-Deutung sowie die Nachbearbeitung der Modellantwort (Mahlzeitentyp,
/// Naehrwert-Notbremse).
///
/// Eigene Klasse statt an GeminiService haengen: GeminiService und OpenRouterService sind zwei
/// gleichrangige Umsetzungen derselben Schnittstelle IAiProvider, keine ist die Heimat der
/// anderen. Vorher griff OpenRouterService auf GeminiService.SystemInstruction und
/// .NormalizeEstimate zu - das funktionierte, zeigte aber seitwaerts auf ein Geschwister statt
/// nach unten auf gemeinsame Logik, und GeminiService trug damit zwei Rollen (Gemini-Anbindung
/// UND Heimat der anbieterneutralen Regeln). Jetzt zeigen beide Anbieter auf diese Klasse.
///
/// Der wichtigste Grund, warum diese Klasse ueberhaupt existiert: die Natriumregel in
/// SystemInstruction und NormalizeEstimate hat am 2026-09-12 verhindert, dass Natrium um den
/// Faktor 1000 zu hoch im Tagebuch landet. Zwei Fassungen dieser Regel liefen unweigerlich
/// auseinander - sie darf nur an EINER Stelle im Code stehen, unabhaengig davon, wie viele
/// Anbieter sie am Ende nutzen.
/// </summary>
public static class AiInstructions
{
    // Die Grenze zwischen Nachfragen und Annehmen entscheidet, ob das Feature im Alltag taugt:
    // zu viele Rueckfragen sind laestiger als die bestehende Suche.
    //
    // Seit 2026-09-17 liegt die Grenze bei der Mengenfrage ausdruecklich auf "schaetzen". Vorher
    // verlangte der Prompt bei jeder unbestimmten Menge ("eine Hand voll", "ein Teller") eine
    // Rueckfrage - gedacht gegen den Schaetzfehler von leicht 300 kcal. In der Praxis fragte die
    // KI bei fast jeder gesprochenen Beschreibung zurueck, obwohl die Bestaetigungsmaske die
    // Gramm ohnehin als Eingabefeld zeigt (AiEntryPage, Spalte "Menge (g)"): der Nutzer
    // korrigiert dort genauer und schneller, als er die Rueckfrage beantworten koennte. Die
    // Rueckfrage bleibt fuer den Fall, dass unklar ist, WAS gegessen wurde - das kann kein
    // Eingabefeld nachholen.
    //
    // Die Einheiten stehen hier AUSDRUECKLICH je Feld. Der Rest der Anwendung fuehrt Natrium in
    // GRAMM je 100 g (OpenFoodFacts-Feld sodium_100g, siehe MealEndpoints.CalcMicro); ein
    // Sprachmodell nennt Natrium von sich aus praktisch immer in Milligramm. Ohne diesen Satz
    // landet der Wert um den Faktor 1000 zu hoch im Tagebuch.
    public const string SystemInstruction = """
        Du zerlegst deutschsprachige Beschreibungen von Mahlzeiten in einzelne Posten.
        Fuer jeden Posten lieferst du: searchTerm (kurzer Suchbegriff fuer eine
        Lebensmitteldatenbank, ohne Mengenangabe), label (lesbarer Name), quantityInGrams
        (Menge in Gramm, Fluessigkeiten in Milliliter gleich Gramm), mealType (genau einer von
        Breakfast, Lunch, Dinner, Snack), productKind und estimate (Naehrwerte je 100 g).

        productKind entscheidet, woher die Naehrwerte am Ende kommen:
          "branded"  Ein gekauftes, verpacktes Produkt, das der Nutzer benennbar gemacht hat -
                     eine Marke ("Koelln Zarte Haferflocken", "Alpro Sojadrink"), ein
                     Fertiggericht oder ein Barcode. Nur dann wird eine Produktdatenbank gefragt.
          "generic"  Alles andere: ein Grundnahrungsmittel ohne Marke ("eine Banane", "Magerquark")
                     und jedes selbst gekochte oder zubereitete Gericht ("Spaghetti Bolognese",
                     "Linsensuppe", "Ruehrei"). Hier zaehlt DEIN Wert, nicht die Datenbank.
        Im Zweifel "generic". Eine Produktdatenbank kennt fuer "Spaghetti" nur TROCKENE Nudeln
        (etwa 360 kcal je 100 g); gekochte haben etwa 150. Ein falsches "branded" macht daraus
        den doppelten Wert.

        Zubereitungszustand gehoert in label UND in estimate: "Spaghetti (gekocht)" mit etwa
        150 kcal je 100 g, nicht der Trockenwert. Dasselbe gilt fuer Reis, Nudeln und
        Huelsenfruechte.
        Die Einheiten in estimate sind bindend und beziehen sich IMMER auf 100 g des
        Lebensmittels:
          calories       Kilokalorien (kcal) je 100 g
          protein        Gramm je 100 g
          carbohydrates  Gramm je 100 g
          fat            Gramm je 100 g
          fiber          Gramm je 100 g
          sugar          Gramm je 100 g
          saturatedFat   Gramm je 100 g
          sodium         GRAMM je 100 g, NICHT Milligramm. Ein Broetchen hat etwa 0.45,
                         nicht 450. Teile einen in Milligramm gedachten Wert durch 1000.
        Rechne Haushaltsmasse um: eine Scheibe Kaese etwa 30 g, eine Tasse Kaffee etwa 200 ml,
        ein Broetchen etwa 60 g.
        Nach der MENGE fragst du NIE zurueck. Unbestimmte Angaben - "eine Hand voll", "eine
        Schuessel", "ein Teller", "grosse Portion", "etwas", "viel", "wenig" - und auch eine
        voellig fehlende Mengenangabe schaetzt du selbst auf eine uebliche Portion und traegst
        sie in quantityInGrams ein. Der Nutzer bekommt deinen Vorschlag in einer Maske, in der
        die Gramm in einem Eingabefeld stehen, und korrigiert sie dort in zwei Sekunden. Eine
        Rueckfrage kostet ihn eine ganze Runde und bringt nichts, was das Feld nicht auch kann.
        Anhaltspunkte: eine Hand voll etwa 30 g, eine Schuessel etwa 350 g, ein Teller einer
        vollstaendigen Mahlzeit etwa 400 g, eine Portion Beilage etwa 200 g. "Klein" nimmst du
        etwa ein Drittel darunter, "gross" etwa ein Drittel darueber.
        Schreibe die Annahme in label mit dazu, damit der Nutzer sieht, was du angenommen hast:
        "Erdnuesse (eine Hand voll)", "Chili con Carne (ein Teller)".
        Eine Rueckfrage im Feld question - GENAU EINE kurze, items dann leer - stellst du nur,
        wenn ohne sie gar nicht feststeht, WAS gegessen wurde: wenn die Beschreibung mehrere
        voellig verschiedene Lebensmittel meinen kann oder unverstaendlich ist. Alles, was bloss
        die Menge betrifft, schaetzt du.
        Steht ueber dem Gespraech ein Abschnitt "Bisher gegessen", dann ist das der Verlauf der
        letzten Tage, jede Zeile mit einer Kennung in eckigen Klammern. Bezieht sich der Nutzer auf
        eine dieser Zeilen ("das Eis von gestern", "nochmal das Fruehstueck", "den Rest davon"),
        setze sourceRef auf ihre Kennung, zum Beispiel "v2". Die Naehrwerte sind dann bereits
        bekannt und dein estimate wird verworfen - fuelle es trotzdem, das Schema verlangt es.
        quantityInGrams gilt weiterhin und ist deine Aufgabe: "die andere Haelfte" und "nochmal
        dasselbe" meinen die Menge aus der Zeile, "die Haelfte davon" die halbe.
        Ohne erkennbaren Bezug laesst du sourceRef weg und verfaehrst wie bisher. Erfinde NIE eine
        Kennung, die nicht im Abschnitt steht.
        Antworte ausschliesslich im vorgegebenen Schema.
        """;

    public const string WishInstruction = """
        Du liest aus einem deutschsprachigen Satz heraus, welches Ernaehrungsziel jemand verfolgt.
        Du rechnest NICHTS aus - Kalorien und Makros bestimmt eine Formel, nicht du.
        Liefere:
          direction         "lose" (abnehmen), "hold" (Gewicht halten) oder "gain" (aufbauen)
          intensityPercent  gewuenschte Abweichung vom Erhaltungsbedarf in Prozent, falls der Satz
                            eine Geschwindigkeit nennt ("langsam" etwa 10, "zuegig" etwa 25,
                            ohne Angabe: weglassen)
          style             "lowCarb" bei ausdruecklichem Wunsch nach wenig Kohlenhydraten,
                            "highProtein" bei Muskelaufbau oder ausdruecklichem Proteinwunsch,
                            sonst "balanced"
          interpretation    EIN kurzer deutscher Satz, wie du den Wunsch verstanden hast. Der
                            Nutzer liest ihn zur Gegenkontrolle, bevor die Ziele uebernommen
                            werden - schreibe ihn so, dass ein Missverstaendnis auffaellt.
        Ist kein Ziel erkennbar, nimm "hold" und sage das in interpretation.
        """;

    /// <summary>
    /// Bringt den Mahlzeitentyp auf einen der vier Enum-Werte.
    ///
    /// Zweiter Riegel hinter dem enum im Antwortschema: das Schema ist die Zusage des Anbieters,
    /// diese Methode die Absicherung dagegen, dass die Zusage bricht. Bei der Handprobe am
    /// 2026-09-12 kam "Frühstück" zurueck — unser Prompt ist deutsch, also antwortet das Modell
    /// deutsch. Ohne Umsetzung lehnt MealEndpoints den Eintrag spaeter mit 400 ab, und der Nutzer
    /// haette eine Bestaetigungsmaske vor sich, die sich nicht uebernehmen laesst.
    ///
    /// Unbekanntes wird zu Snack statt zu einem Fehler: der Typ ist in der Maske ohnehin
    /// aenderbar, und eine ganze Mahlzeit an einer Vokabel scheitern zu lassen waere
    /// unverhaeltnismaessig.
    /// </summary>
    public static string NormalizeMealType(string? mealType)
    {
        var wert = (mealType ?? string.Empty).Trim();

        if (Enum.TryParse<MealType>(wert, ignoreCase: true, out var treffer) && Enum.IsDefined(treffer))
            return treffer.ToString();

        return wert.ToLowerInvariant() switch
        {
            "frühstück" or "fruehstueck" or "fruhstuck" or "morgens" or "breakfast" => "Breakfast",
            "mittagessen" or "mittag" or "mittags" or "lunch" => "Lunch",
            "abendessen" or "abendbrot" or "abend" or "abends" or "dinner" => "Dinner",
            _ => "Snack",
        };
    }

    /// <summary>
    /// Letzte Notbremse gegen unmoegliche Schaetzwerte, bevor sie ueber die API in die Datenbank
    /// wandern. Der eigentliche Vertrag steht in <see cref="SystemInstruction"/> und im Schema
    /// jedes Anbieters; dies faengt nur ab, was physikalisch nicht sein kann — denn korrigieren
    /// laesst sich ein falscher Naehrwert spaeter nicht mehr: PUT /api/meals/{id} aendert nur
    /// Menge, Mahlzeit und Zeit, und ueber FindReusableFoodItemAsync entstuende ein globaler
    /// FoodItem mit dem Unsinn. Bewusst nur die unmoeglichen Bereiche: ein Wert, der bloss
    /// ungewoehnlich ist, bleibt stehen.
    /// </summary>
    public static void NormalizeEstimate(AiItem item, ILogger logger)
    {
        var estimate = item.Estimate;

        // Reines Fett hat rund 900 kcal je 100 g; mehr kann kein Lebensmittel haben.
        estimate.Calories = Clamp(estimate.Calories, 900m);

        // Ein Naehrstoff kann nicht mehr als 100 g je 100 g ausmachen.
        estimate.Protein = Clamp(estimate.Protein, 100m);
        estimate.Carbohydrates = Clamp(estimate.Carbohydrates, 100m);
        estimate.Fat = Clamp(estimate.Fat, 100m);
        estimate.Fiber = Clamp(estimate.Fiber, 100m);
        estimate.Sugar = Clamp(estimate.Sugar, 100m);
        estimate.SaturatedFat = Clamp(estimate.SaturatedFat, 100m);

        // Natrium fuehrt die Anwendung in GRAMM je 100 g. Selbst reines Kochsalz kommt auf nur
        // rund 39 g Natrium je 100 g — alles darueber ist mit Sicherheit ein in Milligramm
        // gedachter Wert (das Modell neigt trotz Anweisung dazu). Faktor 1000 statt Kappen:
        // Kappen machte aus 450 mg glaubwuerdige 40 g und damit einen unauffaelligen Unsinn.
        const decimal maxSodiumGramsPer100g = 40m;
        if (estimate.Sodium > maxSodiumGramsPer100g)
        {
            logger.LogWarning(
                "Natrium-Schaetzung {Value} je 100 g fuer {Label} ist als Gramm unmoeglich; " +
                "als Milligramm gewertet und durch 1000 geteilt.", estimate.Sodium, item.Label);
            estimate.Sodium /= 1000m;
        }

        estimate.Sodium = Clamp(estimate.Sodium, maxSodiumGramsPer100g);
    }

    private static decimal Clamp(decimal value, decimal max) => value < 0 ? 0m : Math.Min(value, max);

    private static decimal? Clamp(decimal? value, decimal max) => value is null ? null : Clamp(value.Value, max);
}
