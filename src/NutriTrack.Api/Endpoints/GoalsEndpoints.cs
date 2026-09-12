using System.Security.Claims;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NutriTrack.Api.Contracts.Goals;
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
