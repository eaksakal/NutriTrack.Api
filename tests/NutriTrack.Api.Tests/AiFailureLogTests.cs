using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NutriTrack.Api.Services;
using NutriTrack.Api.Tests.Infrastructure;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Tests;

public class AiFailureLogTests(AiFailureLogTests.AdminCapableFactory factory)
    : IClassFixture<AiFailureLogTests.AdminCapableFactory>
{
    /// <summary>
    /// Admin-faehige Fassung der Standard-Factory. Review-Befund 2 verlangt, den
    /// Anbieter-Fehlschlag ueber GET /api/admin/failures zu lesen statt an der API vorbei direkt
    /// aus der Entitaet - nur so deckt derselbe Test zugleich ab, dass die Spalte tatsaechlich
    /// beim Betrachter ankommt (Befund 1). Eine feste Administrator-Adresse genuegt dafuer: kein
    /// anderer Test dieser Datei registriert sie, und ausserhalb dieser Datei ruft niemand
    /// /api/admin/* ueber diese Factory auf - die Admin-Rechte hier stoeren also nirgends.
    /// </summary>
    public sealed class AdminCapableFactory : NutriTrackApiFactory
    {
        public const string AdminEmail = "chef@example.com";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Admin:Email", AdminEmail);
        }
    }

    private async Task<List<AiFailure>> ProtokollAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AiFailures.AsNoTracking().OrderByDescending(f => f.OccurredAt).ToListAsync();
    }

    private async Task LeereAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.AiFailures.ExecuteDeleteAsync();
    }

    /// <summary>
    /// Schreibt den Anbieter direkt in die Datenbank und verwirft den Cache - dieselbe
    /// Vorgehensweise wie AiProviderFactoryTests.SetzeAnbieterAsync. null heisst "zurueck zum
    /// Vorgabewert (gemini)", nicht "leerer Wert".
    /// </summary>
    private async Task SetzeAnbieterAsync(string? provider)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vorhandene = await db.AiSettings.SingleOrDefaultAsync();
        if (vorhandene is not null)
            db.AiSettings.Remove(vorhandene);
        await db.SaveChangesAsync();

        if (provider is not null)
        {
            db.AiSettings.Add(new AiSettings { Id = 1, Provider = provider, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        factory.Services.GetRequiredService<AiSettingsProvider>().Invalidate();
    }

    [Fact]
    public async Task Timeout_IsRecordedWithoutTheMealText()
    {
        await LeereAsync();
        var (client, _, _) = await factory.CreateUserAsync();

        factory.GeminiResponder = _ => throw new TaskCanceledException("Zeitdeckel im Test.");

        // Ein Wort, das ausser in der Mahlzeit nirgends vorkommt: taucht es im Protokoll auf,
        // ist der Esstext hineingeraten. Das ist die tragende Zusage dieses Features.
        await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "Zwetschgendatschi mit Sahne" } }
        });

        var eintrag = Assert.Single(await ProtokollAsync());
        Assert.Equal("Timeout", eintrag.Kind);
        Assert.Equal("gemini-3.6-flash", eintrag.Model);
        Assert.True(eintrag.DurationMs >= 0);
        Assert.DoesNotContain("Zwetschgendatschi", eintrag.Reason);
    }

    [Fact]
    public async Task NetworkFailure_IsRecordedAsUnavailable()
    {
        await LeereAsync();
        var (client, _, _) = await factory.CreateUserAsync();

        // Das Wort "rechtzeitig" steckt hier ABSICHTLICH in einer HttpRequestException, nicht in
        // einer TaskCanceledException: nur der Typ der inneren Ausnahme darf ueber Timeout vs.
        // Unavailable entscheiden, nie der Wortlaut. Ein Test mit unauffaelligem Text bestuende
        // auch unter der verworfenen Substring-Logik (Standardzweig ohne "rechtzeitig" ist
        // ebenfalls "Unavailable") und bewiese damit nichts; dieser Text zieht Wortlaut und
        // Ausnahmetyp bewusst auseinander und wird nur unter der neuen, typbasierten
        // Unterscheidung richtig als "Unavailable" verbucht - unter der alten waere er faelschlich
        // "Timeout" (ex.Detail haengt die Meldung der tiefsten inneren Ausnahme an, siehe
        // AiUnavailableException.Detail, der Text schlaegt also durch).
        factory.GeminiResponder = _ => throw new HttpRequestException("Verbindung nicht rechtzeitig aufgebaut.");

        await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        var eintrag = Assert.Single(await ProtokollAsync());
        Assert.Equal("Unavailable", eintrag.Kind);
    }

    [Fact]
    public async Task MalformedResponse_IsRecordedAsSchema()
    {
        await LeereAsync();
        var (client, _, _) = await factory.CreateUserAsync();

        factory.GeminiResponder = _ => StubGeminiHandler.Payload("kein json");

        await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        var protokoll = await ProtokollAsync();
        Assert.Contains(protokoll, f => f.Kind == "Schema");
    }

    /// <summary>
    /// Der Fall vom 2026-09-15: estimate.sugar geriet in eine Ziffernschleife und sprengte
    /// decimal. Vor diesem Fix stand im Protokoll immer derselbe Satz ("Antwort passt nicht zum
    /// Schema.") - genau die Information, die den Fehler erklaert haette (der Pfad), fehlte.
    /// Dieser Test haelt zweierlei zugleich fest: den Pfad im Protokoll UND, dass der Esstext -
    /// die tragende Zusage des ganzen Features - dabei draussen bleibt. System.Text.Json nennt in
    /// seiner Fehlermeldung fuer eine solche Typkonvertierung ausdruecklich nur Pfad und Position,
    /// nie den gelesenen Wert; die letzte Assertion unten belegt das fuer die Ziffernkette selbst.
    /// </summary>
    [Fact]
    public async Task SchemaBreak_IsRecordedWithPathButWithoutTheMealText()
    {
        await LeereAsync();
        var (client, _, _) = await factory.CreateUserAsync();

        var vieleNeunen = new string('9', 40);
        factory.GeminiResponder = _ => StubGeminiHandler.Payload($$"""
        {
          "items": [
            { "searchTerm": "Zwetschgendatschi", "label": "Zwetschgendatschi",
              "quantityInGrams": 100, "mealType": "Snack", "productKind": "generic",
              "estimate": { "calories": 250, "protein": 4, "carbohydrates": 40, "fat": 8,
                            "sugar": {{vieleNeunen}} } }
          ]
        }
        """);

        // Ein Wort, das ausser in der Mahlzeit nirgends vorkommt - dieselbe Probe wie beim
        // Zeitdeckel-Test oben.
        await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Zwetschgendatschi mit viel Zucker" } }
        });

        var eintrag = Assert.Single(await ProtokollAsync());
        Assert.Equal("Schema", eintrag.Kind);
        Assert.Contains("estimate.sugar", eintrag.Reason);
        Assert.DoesNotContain("Zwetschgendatschi", eintrag.Reason);
        // Die entgleiste Ziffernkette selbst darf ebenfalls nicht im Protokoll landen - waere sie
        // drin, waere estimate.sugar in Wahrheit ein Wert und keine Positionsangabe.
        Assert.DoesNotContain(vieleNeunen, eintrag.Reason);
    }

    [Fact]
    public async Task Quota_IsRecordedWithStatusCode()
    {
        await LeereAsync();
        var (client, _, _) = await factory.CreateUserAsync();

        factory.GeminiResponder = _ => StubGeminiHandler.InteractionsQuotaFailure();

        await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        var eintrag = Assert.Single(await ProtokollAsync());
        Assert.Equal("Quota", eintrag.Kind);
        Assert.Equal(429, eintrag.StatusCode);
    }

    /// <summary>
    /// GoalsEndpoints ruft Gemini ebenfalls auf (die Deutung eines Zielwunsches), protokollierte
    /// Fehlschlaege dort bislang aber gar nicht. Dieser Test belegt sowohl das Nachziehen als auch
    /// die tragende Zusage auf dem zweiten Pfad: der Zielwunsch-Text darf ins Protokoll so wenig
    /// wie der Esstext.
    /// </summary>
    [Fact]
    public async Task GoalsSuggest_Timeout_IsRecordedWithoutTheWishText()
    {
        await LeereAsync();
        var (client, _, _) = await factory.CreateUserAsync();

        factory.GeminiResponder = _ => throw new TaskCanceledException("Zeitdeckel im Test.");

        // Ein Wort, das ausser im Zielwunsch nirgends vorkommt.
        await client.PostAsJsonAsync("/api/goals/suggest", new
        {
            weightKg = 82, heightCm = 180, age = 35, sex = "male",
            activityLevel = "sedentary", wish = "Marathonvorbereitung mit Radikaldiaet"
        });

        var eintrag = Assert.Single(await ProtokollAsync());
        Assert.Equal("Timeout", eintrag.Kind);
        Assert.DoesNotContain("Marathonvorbereitung", eintrag.Reason);
    }

    [Fact]
    public async Task Failure_RecordsWhichProviderItWas()
    {
        await LeereAsync();
        var (client, _, _) = await factory.CreateUserAsync(AdminCapableFactory.AdminEmail);

        // Provoziert bewusst MIT dem aktiven Anbieter openrouter statt mit dem Vorgabewert: ein
        // fest verdrahtetes "gemini" im Recorder bliebe gegen den Vorgabewert weiterhin gruen und
        // bewiese damit nicht, dass die Spalte dem AKTIVEN Anbieter folgt (Review-Befund 2). Die
        // Testklasse teilt Datenbank und AiSettingsProvider-Singleton mit den uebrigen Tests
        // dieser Datei, deshalb im finally wieder auf den Vorgabewert zurueckstellen.
        await SetzeAnbieterAsync("openrouter");
        try
        {
            factory.OpenRouterResponder = _ => throw new TaskCanceledException("Zeitdeckel im Test.");

            await client.PostAsJsonAsync("/api/ai/parse-meal", new
            {
                messages = new[] { new { role = "user", text = "ein Apfel" } }
            });

            // UEBER DEN ENDPUNKT gelesen, nicht an der API vorbei: sonst bliebe unbemerkt, dass
            // AiFailureLogEntry und die Select-Projektion in AdminEndpoints.cs das Feld nie
            // ausliefern (Review-Befund 1) - die Spalte in der Datenbank allein nuetzt niemandem
            // an der Oberflaeche.
            var antwort = await client.GetAsync("/api/admin/failures");
            antwort.EnsureSuccessStatusCode();
            var eintraege = await antwort.Content.ReadFromJsonAsync<JsonElement>();

            var eintrag = Assert.Single(eintraege.EnumerateArray());
            Assert.Equal("openrouter", eintrag.GetProperty("provider").GetString());

            // Abschluss-Review Befund 2: vor dem Fix stand hier IMMER Geminis Modell und dessen
            // Denkstufe, egal welcher Anbieter tatsaechlich ausgefallen war. Ein Test, der nur den
            // Anbieter prueft, bliebe gegen diesen Fehler gruen - deshalb hier ausdruecklich das
            // OpenRouter-Modell UND die fehlende Denkstufe.
            Assert.Equal(AiSettingsProvider.DefaultOpenRouterModel, eintrag.GetProperty("model").GetString());
            Assert.Equal(JsonValueKind.Null, eintrag.GetProperty("thinkingLevel").ValueKind);
        }
        finally
        {
            await SetzeAnbieterAsync(null);
            await LeereAsync();
        }
    }

    [Fact]
    public async Task Recorder_KeepsOnlyTheNewestEntries()
    {
        await LeereAsync();
        factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<AiFailureRecorder>();

        for (var i = 0; i < AiFailureRecorder.MaxEntries + 5; i++)
            await recorder.RecordAsync("Timeout", $"Fehlschlag {i}", 10, null);

        var protokoll = await ProtokollAsync();
        Assert.Equal(AiFailureRecorder.MaxEntries, protokoll.Count);
        Assert.Contains(protokoll, f => f.Reason.EndsWith($"{AiFailureRecorder.MaxEntries + 4}"));
        Assert.DoesNotContain(protokoll, f => f.Reason == "Fehlschlag 0");
    }

    [Fact]
    public async Task Recorder_TruncatesLongReasons()
    {
        await LeereAsync();
        factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<AiFailureRecorder>();

        await recorder.RecordAsync("Schema", new string('x', 900), 10, null);

        var eintrag = Assert.Single(await ProtokollAsync());
        Assert.True(eintrag.Reason.Length <= 200);
    }
}
