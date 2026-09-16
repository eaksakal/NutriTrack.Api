using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NutriTrack.Api.Tests.Infrastructure;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Tests;

public class AiSettingsStorageTests(NutriTrackApiFactory factory) : IClassFixture<NutriTrackApiFactory>
{
    private async Task<T> InScopeAsync<T>(Func<AppDbContext, Task<T>> arbeit)
    {
        factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        return await arbeit(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    [Fact]
    public async Task AiSettings_RoundTrips()
    {
        var gelesen = await InScopeAsync(async db =>
        {
            db.AiSettings.Add(new AiSettings
            {
                Id = 1,
                Model = "gemini-3.6-flash",
                ThinkingLevel = "low",
                MaxOutputTokens = 2048,
                UpdatedAt = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc)
            });
            await db.SaveChangesAsync();

            return await db.AiSettings.AsNoTracking().SingleAsync();
        });

        Assert.Equal("gemini-3.6-flash", gelesen.Model);
        Assert.Equal("low", gelesen.ThinkingLevel);
        Assert.Equal(2048, gelesen.MaxOutputTokens);
    }

    [Fact]
    public async Task AiSettings_AllowsNullValuesMeaningFallBackToEnvironment()
    {
        // Null ist kein Versehen, sondern die Aussage "nimm die Umgebungsvariable". Die Spalten
        // muessen das also zulassen, sonst zwingt das Schema den Nutzer zu einem Wert.
        var gelesen = await InScopeAsync(async db =>
        {
            var vorhandene = await db.AiSettings.SingleOrDefaultAsync();
            if (vorhandene is not null)
                db.AiSettings.Remove(vorhandene);

            db.AiSettings.Add(new AiSettings { Id = 1, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();

            return await db.AiSettings.AsNoTracking().SingleAsync();
        });

        Assert.Null(gelesen.Model);
        Assert.Null(gelesen.ThinkingLevel);
        Assert.Null(gelesen.MaxOutputTokens);
    }

    [Fact]
    public async Task AiFailure_RoundTripsWithoutMealText()
    {
        var gelesen = await InScopeAsync(async db =>
        {
            db.AiFailures.Add(new AiFailure
            {
                Id = Guid.NewGuid(),
                OccurredAt = new DateTime(2026, 9, 16, 9, 30, 0, DateTimeKind.Utc),
                Kind = "Schema",
                Model = "gemini-3.6-flash",
                ThinkingLevel = "minimal",
                DurationMs = 45000,
                StatusCode = null,
                Reason = "The JSON value could not be converted. Path: $.items[0].estimate.sugar"
            });
            await db.SaveChangesAsync();

            return await db.AiFailures.AsNoTracking()
                .OrderByDescending(f => f.OccurredAt).FirstAsync();
        });

        Assert.Equal("Schema", gelesen.Kind);
        Assert.Equal(45000, gelesen.DurationMs);
        Assert.Contains("estimate.sugar", gelesen.Reason);
    }
}
