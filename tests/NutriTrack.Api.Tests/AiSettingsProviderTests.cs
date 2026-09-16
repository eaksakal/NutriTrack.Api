using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NutriTrack.Api.Services;
using NutriTrack.Api.Tests.Infrastructure;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Tests;

public class AiSettingsProviderTests(NutriTrackApiFactory factory) : IClassFixture<NutriTrackApiFactory>
{
    private AiSettingsProvider Provider()
    {
        factory.CreateClient();
        return factory.Services.GetRequiredService<AiSettingsProvider>();
    }

    private async Task SpeichereAsync(string? model, string? level, int? maxTokens)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var vorhandene = await db.AiSettings.SingleOrDefaultAsync();
        if (vorhandene is not null)
            db.AiSettings.Remove(vorhandene);
        await db.SaveChangesAsync();

        db.AiSettings.Add(new AiSettings
        {
            Id = 1, Model = model, ThinkingLevel = level, MaxOutputTokens = maxTokens,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        Provider().Invalidate();
    }

    private async Task LeereAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vorhandene = await db.AiSettings.SingleOrDefaultAsync();
        if (vorhandene is not null)
        {
            db.AiSettings.Remove(vorhandene);
            await db.SaveChangesAsync();
        }

        Provider().Invalidate();
    }

    [Fact]
    public async Task Read_WithoutRow_UsesConfiguration()
    {
        await LeereAsync();

        var snapshot = Provider().Read();

        // Die Testfactory setzt Gemini:Model auf gemini-3.6-flash (NutriTrackApiFactory).
        Assert.Equal("gemini-3.6-flash", snapshot.Model);
        Assert.False(snapshot.ModelFromDb);
        Assert.False(snapshot.ThinkingLevelFromDb);
        Assert.False(snapshot.MaxOutputTokensFromDb);
    }

    [Fact]
    public async Task Read_WithStoredValues_PrefersThem()
    {
        await SpeichereAsync("gemini-3.1-flash-lite", "low", 2048);

        var snapshot = Provider().Read();

        Assert.Equal("gemini-3.1-flash-lite", snapshot.Model);
        Assert.True(snapshot.ModelFromDb);
        Assert.Equal("low", snapshot.ThinkingLevel);
        Assert.True(snapshot.ThinkingLevelFromDb);
        Assert.Equal(2048, snapshot.MaxOutputTokens);
        Assert.True(snapshot.MaxOutputTokensFromDb);
    }

    [Fact]
    public async Task Read_WithPartialRow_FallsBackPerField()
    {
        // Nur die Denkstufe ist gesetzt: das Modell muss weiter aus der Umgebung kommen. Ein
        // Rueckfall, der nur ganz oder gar nicht greift, macht die Zeile zum Alles-oder-nichts.
        await SpeichereAsync(null, "low", null);

        var snapshot = Provider().Read();

        Assert.Equal("gemini-3.6-flash", snapshot.Model);
        Assert.False(snapshot.ModelFromDb);
        Assert.Equal("low", snapshot.ThinkingLevel);
        Assert.True(snapshot.ThinkingLevelFromDb);
    }

    [Fact]
    public async Task Read_WithWhitespaceModel_FallsBackToConfiguration()
    {
        // Ein Feld, das der Nutzer leergeraeumt hat, kommt als "" oder "   " an. Das ist die
        // Aussage "nimm wieder die Umgebung", nicht ein Modell ohne Namen.
        await SpeichereAsync("   ", null, null);

        var snapshot = Provider().Read();

        Assert.Equal("gemini-3.6-flash", snapshot.Model);
        Assert.False(snapshot.ModelFromDb);
    }

    [Fact]
    public async Task Read_WithBrokenDatabase_FallsBackToConfiguration()
    {
        // Der Spec verlangt: eine kaputte Nebensache reisst die Haupterfassung nicht mit. Die
        // Tabelle wird in einer EIGENEN Factory geworfen, damit die uebrigen Tests ihre Datenbank
        // unbeschaedigt behalten - dieselbe Technik wie in AiEndpointTests.
        using var eigene = new NutriTrackApiFactory();
        await eigene.ResetDatabaseAsync();
        eigene.CreateClient();

        using (var scope = eigene.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE AiSettings");
        }

        var provider = eigene.Services.GetRequiredService<AiSettingsProvider>();
        provider.Invalidate();

        var snapshot = provider.Read();

        Assert.Equal("gemini-3.6-flash", snapshot.Model);
        Assert.False(snapshot.ModelFromDb);
    }

    [Fact]
    public async Task Read_IsCachedUntilInvalidated()
    {
        await SpeichereAsync("gemini-3.1-flash-lite", null, null);
        var provider = Provider();
        Assert.Equal("gemini-3.1-flash-lite", provider.Read().Model);

        // Direkt an der Datenbank vorbei am Provider aendern: ohne Invalidate darf er den alten
        // Wert liefern - genau das ist der Zweck des Caches.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var zeile = await db.AiSettings.SingleAsync();
            zeile.Model = "gemini-3.6-flash";
            await db.SaveChangesAsync();
        }

        Assert.Equal("gemini-3.1-flash-lite", provider.Read().Model);

        provider.Invalidate();
        Assert.Equal("gemini-3.6-flash", provider.Read().Model);
    }

    [Fact]
    public async Task Read_RecoversAfterTransientDatabaseFailure_WithoutInvalidate()
    {
        // Reviewbefund: nach einem gefangenen Fehler darf der Provider den Fehlzustand nicht bis
        // zum naechsten Invalidate() einfrieren - sonst maskiert ein kurzer Aussetzer beim
        // Kaltstart die gespeicherten Werte auf unbestimmte Zeit. Eigene Factory-Instanz wie beim
        // DROP-TABLE-Test, damit die geteilte Testdatenbank unangetastet bleibt.
        using var eigene = new NutriTrackApiFactory();
        await eigene.ResetDatabaseAsync();
        eigene.CreateClient();

        using (var scope = eigene.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.ExecuteSqlRawAsync("DROP TABLE AiSettings");
        }

        var provider = eigene.Services.GetRequiredService<AiSettingsProvider>();
        provider.Invalidate();

        var waehrendDesAusfalls = provider.Read();
        Assert.Equal("gemini-3.6-flash", waehrendDesAusfalls.Model);
        Assert.False(waehrendDesAusfalls.ModelFromDb);

        // Tabelle wiederherstellen und eine Zeile ablegen - OHNE Invalidate() aufzurufen. Genau
        // das soll der naechste Read() von selbst bemerken, wenn der Fehlzustand nicht
        // gecacht wurde.
        await eigene.ResetDatabaseAsync();

        using (var scope = eigene.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AiSettings.Add(new AiSettings
            {
                Id = 1, Model = "gemini-3.1-flash-lite", ThinkingLevel = null, MaxOutputTokens = null,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var nachDerErholung = provider.Read();
        Assert.Equal("gemini-3.1-flash-lite", nachDerErholung.Model);
        Assert.True(nachDerErholung.ModelFromDb);
    }

    [Fact]
    public async Task Read_WithoutRow_UsesGeminiAsProvider()
    {
        await LeereAsync();

        var snapshot = Provider().Read();

        // Ohne Eintrag bleibt alles wie vor diesem Feature - ein neuer Anbieter darf sich nicht
        // dadurch einschalten, dass jemand die Tabelle leert.
        Assert.Equal("gemini", snapshot.Provider);
        Assert.False(snapshot.ProviderFromDb);
    }

    [Fact]
    public async Task Read_WithStoredProvider_PrefersIt()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vorhandene = await db.AiSettings.SingleOrDefaultAsync();
        if (vorhandene is not null)
            db.AiSettings.Remove(vorhandene);
        await db.SaveChangesAsync();

        db.AiSettings.Add(new AiSettings
        {
            Id = 1,
            Provider = "openrouter",
            OpenRouterModel = "dots-studio/dots-3-note-preview:free",
            Model = "gemini-3.6-flash",
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        Provider().Invalidate();

        var snapshot = Provider().Read();

        Assert.Equal("openrouter", snapshot.Provider);
        Assert.True(snapshot.ProviderFromDb);
        Assert.Equal("dots-studio/dots-3-note-preview:free", snapshot.OpenRouterModel);
        Assert.True(snapshot.OpenRouterModelFromDb);
        // Geminis Modell bleibt daneben stehen - Zurueckschalten soll kein Nachtippen kosten.
        Assert.Equal("gemini-3.6-flash", snapshot.Model);

        db.AiSettings.Remove(await db.AiSettings.SingleAsync());
        await db.SaveChangesAsync();
        Provider().Invalidate();
    }

    [Fact]
    public async Task Read_WithUnknownProvider_FallsBackToGemini()
    {
        // Ein Tippfehler in der Datenbank darf die Erfassung nicht lahmlegen.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vorhandene = await db.AiSettings.SingleOrDefaultAsync();
        if (vorhandene is not null)
            db.AiSettings.Remove(vorhandene);
        await db.SaveChangesAsync();

        db.AiSettings.Add(new AiSettings { Id = 1, Provider = "opendrouter", UpdatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        Provider().Invalidate();

        try
        {
            Assert.Equal("gemini", Provider().Read().Provider);
        }
        finally
        {
            db.AiSettings.Remove(await db.AiSettings.SingleAsync());
            await db.SaveChangesAsync();
            Provider().Invalidate();
        }
    }
}
