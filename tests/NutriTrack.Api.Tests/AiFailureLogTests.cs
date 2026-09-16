using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NutriTrack.Api.Services;
using NutriTrack.Api.Tests.Infrastructure;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Tests;

public class AiFailureLogTests(NutriTrackApiFactory factory) : IClassFixture<NutriTrackApiFactory>
{
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
        // GeminiUnavailableException.Detail, der Text schlaegt also durch).
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
