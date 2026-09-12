using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NutriTrack.Api.Contracts.Meals;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Endpoints;

public static class MealEndpoints
{
    // Obergrenze wie im [Range]-Attribut von CreateMealEntryRequest: 10 kg in einer Mahlzeit ist
    // sicher ein Tippfehler, und eine offene Obergrenze laesst die Tagessumme beliebig entgleisen.
    private const decimal MaxQuantityInGrams = 10000m;

    private const string QuantityError =
        "Quantity must be greater than 0 and at most 10000 grams.";

    private static bool IsValidQuantity(decimal quantityInGrams) =>
        quantityInGrams > 0 && quantityInGrams <= MaxQuantityInGrams;

    private static readonly string MealTypeError =
        $"Invalid meal type. Use: {string.Join(", ", Enum.GetNames<MealType>())}";

    // Enum.TryParse akzeptiert auch numerische Strings und prueft dabei KEINEN Wertebereich:
    // "99" laeuft als MealType 99 durch, wird gespeichert und kommt spaeter als "99" zurueck.
    // Erst Enum.IsDefined schliesst nicht definierte Werte aus. Beide Endpunkte (POST und PUT)
    // gehen ueber diesen Helper, damit die Regel nicht wieder auseinanderlaeuft.
    private static bool TryParseMealType(string? value, out MealType mealType) =>
        Enum.TryParse(value, true, out mealType) && Enum.IsDefined(mealType);

    public static void MapMealEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/meals").WithTags("Meals").RequireAuthorization();

        group.MapPost("/", async (CreateMealEntryRequest request, ClaimsPrincipal user, AppDbContext db) =>
        {
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)!.Value;

            // Minimal APIs werten die DataAnnotations des Requests NICHT automatisch aus (dazu
            // braeuchte es AddValidation(), das nirgends aufgerufen wird). [Required] und
            // [Range] auf CreateMealEntryRequest sind deshalb wirkungslos - ohne die Pruefungen
            // hier landen negative Mengen in der DB und ziehen die Tagessumme ins Minus, und ein
            // leerer FoodName legt einen namenlosen FoodItem im GLOBALEN Katalog an, den das
            // Find-or-Create unten danach fuer jeden weiteren namenlosen Eintrag wiederverwendet.
            // Dieselbe Pruefung wie im PUT weiter unten - beide Wege schreiben dasselbe Feld.
            if (string.IsNullOrWhiteSpace(request.FoodName))
                return Results.BadRequest(new { Error = "Food name must not be empty." });

            if (!IsValidQuantity(request.QuantityInGrams))
                return Results.BadRequest(new { Error = QuantityError });

            // Find or create FoodItem
            var foodItem = await FindReusableFoodItemAsync(db, request);

            if (foodItem is null)
            {
                foodItem = new FoodItem
                {
                    Id = Guid.NewGuid(),
                    Name = request.FoodName,
                    Brand = request.Brand,
                    // Leerstring zu null normalisieren: die Suche unten behandelt "kein Barcode"
                    // als NULL, ein gespeichertes "" waere fuer sie unerreichbar und wuerde bei
                    // jedem weiteren Eintrag eine Dublette erzeugen.
                    Barcode = string.IsNullOrWhiteSpace(request.Barcode) ? null : request.Barcode,
                    Calories = request.Calories,
                    Protein = request.Protein,
                    Carbohydrates = request.Carbohydrates,
                    Fat = request.Fat,
                    Fiber = request.Fiber,
                    Sugar = request.Sugar,
                    SaturatedFat = request.SaturatedFat,
                    Sodium = request.Sodium,
                    VitaminA = request.VitaminA,
                    VitaminC = request.VitaminC,
                    VitaminD = request.VitaminD,
                    Calcium = request.Calcium,
                    Iron = request.Iron,
                    Potassium = request.Potassium,
                    // CreatedAt/UpdatedAt bleiben UTC: technische Zeitstempel, die der Konverter in
                    // SqliteConventions als UTC ueber den Round-Trip haelt. Nur die fachlichen
                    // Felder Date/Time sind Lokalzeit (siehe LocalToday).
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                db.FoodItems.Add(foodItem);
            }

            if (!TryParseMealType(request.MealType, out var mealType))
                return Results.BadRequest(new { Error = MealTypeError });

            // Ein gemeinsamer Snapshot fuer Datum und Uhrzeit, damit ein Aufruf genau um
            // Mitternacht nicht Datum und Zeit aus zwei verschiedenen Tagen kombiniert.
            var localNow = DateTime.Now;

            var entry = new MealEntry
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                FoodItemId = foodItem.Id,
                QuantityInGrams = request.QuantityInGrams,
                MealType = mealType,
                Date = request.Date ?? DateOnly.FromDateTime(localNow),
                Time = request.Time ?? TimeOnly.FromDateTime(localNow),
                // Wie bei FoodItem: CreatedAt ist ein technischer Zeitstempel und bleibt UTC.
                CreatedAt = DateTime.UtcNow
            };

            db.MealEntries.Add(entry);
            await db.SaveChangesAsync();

            return Results.Created($"/api/meals/{entry.Id}", MapToResponse(entry, foodItem));
        });

        group.MapGet("/", async (ClaimsPrincipal user, AppDbContext db, DateOnly? date) =>
        {
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)!.Value;
            var targetDate = date ?? LocalToday();

            var entries = await db.MealEntries
                .Include(m => m.FoodItem)
                .Where(m => m.UserId == userId && m.Date == targetDate)
                .OrderBy(m => m.Time)
                .ToListAsync();

            return Results.Ok(entries.Select(e => MapToResponse(e, e.FoodItem)));
        });

        group.MapGet("/summary", async (ClaimsPrincipal user, AppDbContext db, DateOnly? date) =>
        {
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)!.Value;
            var targetDate = date ?? LocalToday();

            var entries = await db.MealEntries
                .Include(m => m.FoodItem)
                .Where(m => m.UserId == userId && m.Date == targetDate)
                .OrderBy(m => m.Time)
                .ToListAsync();

            var responses = entries.Select(e => MapToResponse(e, e.FoodItem)).ToList();

            return Results.Ok(new DailySummaryResponse
            {
                Date = targetDate,
                TotalEntries = responses.Count,
                TotalCalories = responses.Sum(r => r.Calories),
                TotalProtein = responses.Sum(r => r.Protein),
                TotalCarbohydrates = responses.Sum(r => r.Carbohydrates),
                TotalFat = responses.Sum(r => r.Fat),
                TotalFiber = responses.Sum(r => r.Fiber ?? 0),
                TotalSugar = responses.Sum(r => r.Sugar ?? 0),
                TotalSaturatedFat = responses.Sum(r => r.SaturatedFat ?? 0),
                TotalSodium = responses.Sum(r => r.Sodium ?? 0),
                TotalVitaminA = responses.Sum(r => r.VitaminA ?? 0),
                TotalVitaminC = responses.Sum(r => r.VitaminC ?? 0),
                TotalVitaminD = responses.Sum(r => r.VitaminD ?? 0),
                TotalCalcium = responses.Sum(r => r.Calcium ?? 0),
                TotalIron = responses.Sum(r => r.Iron ?? 0),
                TotalPotassium = responses.Sum(r => r.Potassium ?? 0),
                Entries = responses
            });
        });

        group.MapPut("/{id:guid}", async (Guid id, UpdateMealEntryRequest request, ClaimsPrincipal user, AppDbContext db) =>
        {
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)!.Value;

            // FoodItem wird mitgeladen, weil die Antwort die Naehrwerte daraus hochrechnet.
            var entry = await db.MealEntries
                .Include(m => m.FoodItem)
                .FirstOrDefaultAsync(m => m.Id == id && m.UserId == userId);

            // 404 statt 403 bei fremden Eintraegen, damit fremde IDs nicht unterscheidbar sind.
            if (entry is null)
                return Results.NotFound();

            if (!IsValidQuantity(request.QuantityInGrams))
                return Results.BadRequest(new { Error = QuantityError });

            if (!TryParseMealType(request.MealType, out var mealType))
                return Results.BadRequest(new { Error = MealTypeError });

            entry.QuantityInGrams = request.QuantityInGrams;
            entry.MealType = mealType;
            entry.Date = request.Date ?? entry.Date;
            entry.Time = request.Time ?? entry.Time;

            await db.SaveChangesAsync();

            return Results.Ok(MapToResponse(entry, entry.FoodItem));
        });

        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, AppDbContext db) =>
        {
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)!.Value;
            var entry = await db.MealEntries.FirstOrDefaultAsync(m => m.Id == id && m.UserId == userId);

            if (entry is null)
                return Results.NotFound();

            db.MealEntries.Remove(entry);
            await db.SaveChangesAsync();

            return Results.NoContent();
        });
    }

    /// <summary>
    /// Liefert einen bestehenden FoodItem, der zum Request passt - oder null, wenn ein neuer
    /// Datensatz angelegt werden muss.
    /// </summary>
    private static async Task<FoodItem?> FindReusableFoodItemAsync(AppDbContext db, CreateMealEntryRequest request)
    {
        // Identitaet des Produkts: der Barcode, wo er vorliegt - er unterscheidet zwei Produkte
        // zuverlaessig, die zufaellig denselben Namen und dieselbe Marke tragen. Ohne Barcode
        // bleibt nur Name+Marke, dann aber auch nur gegen barcodelose Datensaetze: ein Eintrag mit
        // Barcode gehoert zu einem konkreten Produkt und darf nicht von einer freien Namenseingabe
        // getroffen werden.
        var candidates = string.IsNullOrWhiteSpace(request.Barcode)
            ? db.FoodItems.Where(f => f.Name == request.FoodName && f.Brand == request.Brand && f.Barcode == null)
            : db.FoodItems.Where(f => f.Barcode == request.Barcode);

        // Der Naehrwertvergleich passiert bewusst im Speicher: decimal liegt unter SQLite als TEXT
        // (siehe SqliteConventions), ein Vergleich in SQL waere ein Stringvergleich und wuerde
        // "0.50" und "0.5" faelschlich als verschieden werten.
        var matching = await candidates.ToListAsync();
        return matching.FirstOrDefault(f => HasSameNutrients(f, request));
    }

    /// <summary>
    /// Wiederverwendet wird ein Datensatz nur, wenn ALLE Naehrwerte exakt uebereinstimmen.
    /// </summary>
    // Frueher wurde allein ueber Name+Marke gesucht und die mitgeschickten Naehrwerte still
    // verworfen: wer einen Namen zuerst belegt, bestimmte damit die Werte fuer alle anderen
    // Nutzer, und eine zwischenzeitliche Korrektur bei OpenFoodFacts kam nie an. Weichen die
    // Werte ab, entsteht deshalb ein NEUER Datensatz, statt den bestehenden zu ueberschreiben -
    // sonst wuerden sich rueckwirkend auch die Eintraege aller anderen Nutzer aendern.
    // Die Tabelle laeuft dadurch nicht voll: identische Wiederholungen - der Normalfall, weil die
    // Werte aus derselben Quelle stammen - treffen weiterhin denselben Datensatz.
    // Bewusst ohne Toleranz: eine echte Korrektur (0.9 statt 1.0 g Fett) faellt kleiner aus als
    // jede Toleranz, die Tippfehler abfangen wuerde, und waere damit genau der Fall, den eine
    // Toleranz verschluckt.
    private static bool HasSameNutrients(FoodItem food, CreateMealEntryRequest request) =>
        food.Calories == request.Calories
        && food.Protein == request.Protein
        && food.Carbohydrates == request.Carbohydrates
        && food.Fat == request.Fat
        && food.Fiber == request.Fiber
        && food.Sugar == request.Sugar
        && food.SaturatedFat == request.SaturatedFat
        && food.Sodium == request.Sodium
        && food.VitaminA == request.VitaminA
        && food.VitaminC == request.VitaminC
        && food.VitaminD == request.VitaminD
        && food.Calcium == request.Calcium
        && food.Iron == request.Iron
        && food.Potassium == request.Potassium;

    // Date und Time sind fachlich die LOKALE Tageszeit des Nutzers, und der Server laeuft mit
    // TZ=Europe/Berlin. Mit UtcNow war die vorbelegte Uhrzeit 1-2 h zu frueh und ein Eintrag
    // zwischen Mitternacht und 01/02 Uhr landete auf dem Vortag - bzw. beim Lesen ohne date-Parameter
    // wurde noch der Vortag angezeigt. Deshalb ueberall DateTime.Now (= TimeZoneInfo.Local).
    private static DateOnly LocalToday() => DateOnly.FromDateTime(DateTime.Now);

    // Makronaehrwerte stehen in Gramm im Zehntel-/Hundertstelbereich; zwei Nachkommastellen sind
    // hier die passende Anzeigegenauigkeit.
    private const int MacroDecimals = 2;

    // Mikronaehrstoffe kommen von OpenFoodFacts (Feld "<naehrstoff>_100g", siehe
    // OpenFoodFactsService/FoodEndpoints) in GRAMM pro 100 g und bleiben hier in dieser Einheit -
    // das Frontend rechnet selbst auf mg/µg hoch. Vitamin D liegt damit bei ~0.000005 g/100 g:
    // mit zwei Nachkommastellen war jeder dieser Werte 0.00 und die Tagessumme dauerhaft null.
    // Acht Stellen halten noch 1/100 µg fest (1 µg = 0.000001 g) und damit mehr Genauigkeit, als
    // die Quelldaten hergeben. Natrium gehoert mit dazu: es kommt ebenfalls in Gramm und wird im
    // Frontend in mg angezeigt.
    private const int MicroDecimals = 8;

    private static decimal Calc(decimal per100g, decimal grams) => Math.Round(per100g * grams / 100m, MacroDecimals);
    private static decimal? CalcN(decimal? per100g, decimal grams) => per100g.HasValue ? Math.Round(per100g.Value * grams / 100m, MacroDecimals) : null;
    private static decimal? CalcMicro(decimal? per100g, decimal grams) => per100g.HasValue ? Math.Round(per100g.Value * grams / 100m, MicroDecimals) : null;

    private static MealEntryResponse MapToResponse(MealEntry entry, FoodItem food) => new()
    {
        Id = entry.Id,
        FoodName = food.Name,
        Brand = food.Brand,
        QuantityInGrams = entry.QuantityInGrams,
        MealType = entry.MealType.ToString(),
        Date = entry.Date,
        Time = entry.Time,
        Calories = Calc(food.Calories, entry.QuantityInGrams),
        Protein = Calc(food.Protein, entry.QuantityInGrams),
        Carbohydrates = Calc(food.Carbohydrates, entry.QuantityInGrams),
        Fat = Calc(food.Fat, entry.QuantityInGrams),
        Fiber = CalcN(food.Fiber, entry.QuantityInGrams),
        Sugar = CalcN(food.Sugar, entry.QuantityInGrams),
        SaturatedFat = CalcN(food.SaturatedFat, entry.QuantityInGrams),
        Sodium = CalcMicro(food.Sodium, entry.QuantityInGrams),
        VitaminA = CalcMicro(food.VitaminA, entry.QuantityInGrams),
        VitaminC = CalcMicro(food.VitaminC, entry.QuantityInGrams),
        VitaminD = CalcMicro(food.VitaminD, entry.QuantityInGrams),
        Calcium = CalcMicro(food.Calcium, entry.QuantityInGrams),
        Iron = CalcMicro(food.Iron, entry.QuantityInGrams),
        Potassium = CalcMicro(food.Potassium, entry.QuantityInGrams)
    };
}
