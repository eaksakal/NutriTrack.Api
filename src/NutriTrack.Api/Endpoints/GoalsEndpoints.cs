using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NutriTrack.Api.Contracts.Goals;
using NutriTrack.Api.Services;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Endpoints;

public static class GoalsEndpoints
{
    public static void MapGoalsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/goals").WithTags("Goals").RequireAuthorization();

        group.MapGet("/", async (ClaimsPrincipal user, AppDbContext db) =>
        {
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)!.Value;

            var goal = await db.UserGoals.FirstOrDefaultAsync(g => g.UserId == userId);

            if (goal is null)
                return Results.NotFound();

            return Results.Ok(MapToResponse(goal));
        });

        // Schlaegt Ziele vor, SPEICHERT ABER NICHTS. Uebernommen wird ueber das bestehende PUT,
        // nachdem der Nutzer die Zahlen gesehen hat - dasselbe Muster wie bei den Mahlzeiten.
        group.MapPost("/suggest", async (
            SuggestGoalsRequest request,
            IConfiguration configuration,
            GeminiService gemini,
            ClaimsPrincipal user,
            AiRateLimiter limiter,
            HttpResponse httpResponse,
            CancellationToken ct) =>
        {
            if (request.WeightKg is < 25m or > 400m)
                return Results.BadRequest(new { Error = "Gewicht muss zwischen 25 und 400 kg liegen." });

            if (request.HeightCm is < 100m or > 250m)
                return Results.BadRequest(new { Error = "Größe muss zwischen 100 und 250 cm liegen." });

            if (request.Age is < 14 or > 120)
                return Results.BadRequest(new { Error = "Alter muss zwischen 14 und 120 Jahren liegen." });

            if (request.Wish.Length > 500)
                return Results.BadRequest(new { Error = "Bitte höchstens 500 Zeichen." });

            var body = new BodyData(
                request.WeightKg,
                request.HeightCm,
                request.Age,
                string.Equals(request.Sex, "female", StringComparison.OrdinalIgnoreCase) ? Sex.Female : Sex.Male,
                request.ActivityLevel?.Trim().ToLowerInvariant() switch
                {
                    "light" => ActivityLevel.Light,
                    "moderate" => ActivityLevel.Moderate,
                    "active" => ActivityLevel.Active,
                    "veryactive" => ActivityLevel.VeryActive,
                    _ => ActivityLevel.Sedentary,
                });

            var richtung = GoalDirection.Hold;
            var stil = MacroStyle.Balanced;
            decimal? intensitaet = null;
            var deutung = "Gewicht halten.";

            // Ohne Wunsch und ohne eingerichtete KI wird einfach der Erhaltungsbedarf gerechnet -
            // das ist eine brauchbare Antwort und kein Fehlerfall.
            if (!string.IsNullOrWhiteSpace(request.Wish)
                && !string.IsNullOrWhiteSpace(configuration["Gemini:ApiKey"]))
            {
                var userId = user.FindFirst(ClaimTypes.NameIdentifier)!.Value;
                if (!limiter.TryAcquire(userId))
                    return Results.Json(
                        new { Error = "Zu viele KI-Anfragen in der letzten Stunde. Versuche es später noch einmal." },
                        statusCode: StatusCodes.Status429TooManyRequests);

                try
                {
                    // HIER GEHT NUR DER WUNSCH RAUS. Gewicht, Groesse, Alter und Geschlecht
                    // bleiben auf diesem Rechner - das ist die Abmachung mit dem Nutzer.
                    var gedeutet = await gemini.ParseWishAsync(request.Wish, ct);

                    richtung = gedeutet.Direction switch
                    {
                        "lose" => GoalDirection.Lose,
                        "gain" => GoalDirection.Gain,
                        _ => GoalDirection.Hold,
                    };

                    stil = gedeutet.Style switch
                    {
                        "lowCarb" => MacroStyle.LowCarb,
                        "highProtein" => MacroStyle.HighProtein,
                        _ => MacroStyle.Balanced,
                    };

                    intensitaet = gedeutet.IntensityPercent;
                    deutung = gedeutet.Interpretation;
                }
                catch (GeminiQuotaException ex)
                {
                    return AiQuotaResponse.From(ex, httpResponse);
                }
                catch (Exception ex) when (ex is GeminiUnavailableException or GeminiMalformedResponseException)
                {
                    return Results.Json(
                        new { Error = "Die KI ist gerade nicht erreichbar. Du kannst die Ziele von Hand eintragen." },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }

            var ziele = GoalCalculator.Calculate(body, richtung, stil, intensitaet);

            return Results.Ok(new GoalsSuggestionResponse
            {
                CalorieGoal = ziele.Calories,
                ProteinGoal = ziele.Protein,
                CarbohydrateGoal = ziele.Carbohydrates,
                FatGoal = ziele.Fat,
                BasalMetabolicRate = ziele.BasalMetabolicRate,
                MaintenanceCalories = ziele.MaintenanceCalories,
                Explanation = ziele.Explanation,
                InterpretedWish = deutung,
            });
        });

        group.MapPut("/", async (UpsertGoalsRequest request, ClaimsPrincipal user, AppDbContext db) =>
        {
            var userId = user.FindFirst(ClaimTypes.NameIdentifier)!.Value;

            // Minimal APIs werten die DataAnnotations hier nicht automatisch aus,
            // deshalb wird explizit geprueft - sonst landen 0 oder negative Ziele in der DB.
            if (request.CalorieGoal <= 0 || request.ProteinGoal <= 0 ||
                request.CarbohydrateGoal <= 0 || request.FatGoal <= 0)
                return Results.BadRequest(new { Error = "All goals must be greater than 0." });

            var goal = await db.UserGoals.FirstOrDefaultAsync(g => g.UserId == userId);

            if (goal is not null)
            {
                ApplyRequest(goal, request);
                await db.SaveChangesAsync();

                return Results.Ok(MapToResponse(goal));
            }

            goal = new UserGoal
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                CalorieGoal = request.CalorieGoal,
                ProteinGoal = request.ProteinGoal,
                CarbohydrateGoal = request.CarbohydrateGoal,
                FatGoal = request.FatGoal,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            db.UserGoals.Add(goal);

            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException ex) when (IsUserGoalsUniqueViolation(ex))
            {
                // Zwischen Lesen und Schreiben hat ein paralleler Request desselben Nutzers
                // (zweiter Browsertab, Doppelklick) den Zielsatz bereits angelegt - IX_UserGoals_UserId
                // laesst den zweiten Insert nicht zu. Fachlich ist das kein Fehler, sondern schlicht
                // ein Update: die verworfene neue Zeile abhaengen, den tatsaechlich gespeicherten
                // Datensatz frisch laden und die Werte dieses Requests darauf schreiben. Der Aufrufer
                // bekommt dadurch dieselbe 200-Antwort wie ohne Wettlauf.
                db.Entry(goal).State = EntityState.Detached;

                goal = await db.UserGoals.FirstOrDefaultAsync(g => g.UserId == userId);

                // Kein Zielsatz da, obwohl der Unique-Index angeschlagen hat: dann war es nicht
                // dieser Wettlauf, und die Ursache darf nicht stillschweigend verschwinden.
                if (goal is null)
                    throw;

                ApplyRequest(goal, request);
                await db.SaveChangesAsync();
            }

            return Results.Ok(MapToResponse(goal));
        });
    }

    private static void ApplyRequest(UserGoal goal, UpsertGoalsRequest request)
    {
        goal.CalorieGoal = request.CalorieGoal;
        goal.ProteinGoal = request.ProteinGoal;
        goal.CarbohydrateGoal = request.CarbohydrateGoal;
        goal.FatGoal = request.FatGoal;
        goal.UpdatedAt = DateTime.UtcNow;
    }

    // SQLITE_CONSTRAINT (19) mit der erweiterten Ursache SQLITE_CONSTRAINT_UNIQUE (2067) ist der
    // Unique-Index. Bewusst so eng gefasst: ein verletzter Fremdschluessel oder NOT NULL kommt
    // ebenfalls als DbUpdateException an, ist aber ein echter Fehler und gehoert nicht aufgeloest.
    private static bool IsUserGoalsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 2067 };

    private static GoalsResponse MapToResponse(UserGoal goal) => new()
    {
        Id = goal.Id,
        CalorieGoal = goal.CalorieGoal,
        ProteinGoal = goal.ProteinGoal,
        CarbohydrateGoal = goal.CarbohydrateGoal,
        FatGoal = goal.FatGoal,
        UpdatedAt = goal.UpdatedAt
    };
}
