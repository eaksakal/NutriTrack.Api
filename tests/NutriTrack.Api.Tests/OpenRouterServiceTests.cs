using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NutriTrack.Api.Contracts.Ai;
using NutriTrack.Api.Services;
using NutriTrack.Api.Tests.Infrastructure;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Tests;

public class OpenRouterServiceTests(NutriTrackApiFactory factory) : IClassFixture<NutriTrackApiFactory>
{
    private OpenRouterService Service()
    {
        factory.CreateClient();
        return factory.Services.GetRequiredService<OpenRouterService>();
    }

    [Fact]
    public void Name_IsOpenRouter()
    {
        IAiProvider provider = Service();
        Assert.Equal("openrouter", provider.Name);
    }

    [Fact]
    public async Task ParseAsync_SendsOpenAiDialect()
    {
        string? body = null;
        factory.OpenRouterResponder = request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubOpenRouterHandler.Payload("""{"items":[]}""");
        };

        await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }], string.Empty, CancellationToken.None);

        using var sent = JsonDocument.Parse(body!);
        var root = sent.RootElement;

        // Systemanweisung als erste Nachricht mit role "system" - Gemini hat dafuer ein eigenes
        // Feld, OpenRouter nicht.
        var messages = root.GetProperty("messages");
        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Contains("NICHT Milligramm", messages[0].GetProperty("content").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());

        var format = root.GetProperty("response_format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var jsonSchema = format.GetProperty("json_schema");
        Assert.True(jsonSchema.GetProperty("strict").GetBoolean());

        // additionalProperties:false muss an JEDEM Objekt des Schemas stehen, nicht nur an der
        // Wurzel - sonst lehnt der strikte Modus ab, aber erst im Betrieb, nicht hier. Eine
        // Pruefung, die nur die Wurzel ansieht, wuerde schweigen, wenn es am Posten- oder am
        // estimate-Objekt spaeter verlorenginge.
        var rootSchema = jsonSchema.GetProperty("schema");
        Assert.False(rootSchema.GetProperty("additionalProperties").GetBoolean());

        var itemSchema = rootSchema.GetProperty("properties").GetProperty("items").GetProperty("items");
        Assert.False(itemSchema.GetProperty("additionalProperties").GetBoolean());

        var estimateSchema = itemSchema.GetProperty("properties").GetProperty("estimate");
        Assert.False(estimateSchema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public async Task ParseAsync_UnwrapsContentFromChoices()
    {
        factory.OpenRouterResponder = _ => StubOpenRouterHandler.Payload("""
        {
          "items": [
            { "searchTerm": "Apfel", "label": "Apfel", "quantityInGrams": 150,
              "mealType": "Snack", "productKind": "generic",
              "estimate": { "calories": 52, "protein": 0.3, "carbohydrates": 14, "fat": 0.2 } }
          ]
        }
        """);

        var result = await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }], string.Empty, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("Apfel", item.Label);
        Assert.Equal(52m, item.Estimate.Calories);
    }

    [Fact]
    public async Task ParseAsync_WithUpstreamErrorInsideA200_ReportsItAsUnavailable()
    {
        // Der Fall, der ohne eigene Pruefung als "unverstaendliche Antwort" durchginge: Status
        // 200, Fehler im Rumpf. Am 2026-09-16 beim echten Dienst beobachtet.
        factory.OpenRouterResponder = _ => StubOpenRouterHandler.UpstreamError();

        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }], string.Empty, CancellationToken.None));

        Assert.Contains("overloaded", ex.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParseAsync_WithNonJsonBodyInsideA200_ReportsSchemaBreak()
    {
        // Status 200, aber der Rumpf ist gar kein JSON - eine Gateway- oder Wartungsseite. Ohne
        // eigenen Fang fliegt hier eine rohe JsonException heraus, die kein Endpunkt kennt
        // (AiFailureResponse faengt nur AiUnavailable-, AiQuota- und
        // AiMalformedResponseException) - der Nutzer saehe einen blanken 500 statt einer
        // sauberen Anbieterfehlermeldung.
        factory.OpenRouterResponder = _ => StubOpenRouterHandler.NonJsonGatewayPage();

        await Assert.ThrowsAsync<AiMalformedResponseException>(() => Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }], string.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ParseAsync_WithRateLimit_ThrowsQuota()
    {
        factory.OpenRouterResponder = _ => StubOpenRouterHandler.RateLimited();

        await Assert.ThrowsAsync<AiQuotaException>(() => Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }], string.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ProbeAsync_ReportsStatusAndRawBody()
    {
        factory.OpenRouterResponder = _ => StubOpenRouterHandler.UpstreamError();

        var result = await Service().ProbeAsync(CancellationToken.None);

        // Die Probe wirft nicht - ein Anbieterfehler ist genau das, was der Betreiber sehen will.
        Assert.Equal(200, result.StatusCode);
        Assert.Contains("overloaded", result.RawBody);
        Assert.Equal("nex-agi/nex-n2.5-pro:free", result.Model);
    }

    [Fact]
    public async Task ParseAsync_UsesStoredOpenRouterModel()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vorhandene = await db.AiSettings.SingleOrDefaultAsync();
        if (vorhandene is not null)
            db.AiSettings.Remove(vorhandene);
        db.AiSettings.Add(new AiSettings
        {
            Id = 1, OpenRouterModel = "dots-studio/dots-3-note-preview:free", UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        factory.Services.GetRequiredService<AiSettingsProvider>().Invalidate();

        string? body = null;
        factory.OpenRouterResponder = request =>
        {
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubOpenRouterHandler.Payload("""{"items":[]}""");
        };

        try
        {
            await Service().ParseAsync(
                [new ChatMessage { Role = "user", Text = "ein Apfel" }], string.Empty, CancellationToken.None);

            using var sent = JsonDocument.Parse(body!);
            Assert.Equal("dots-studio/dots-3-note-preview:free", sent.RootElement.GetProperty("model").GetString());
        }
        finally
        {
            db.AiSettings.Remove(await db.AiSettings.SingleAsync());
            await db.SaveChangesAsync();
            factory.Services.GetRequiredService<AiSettingsProvider>().Invalidate();
        }
    }
}
