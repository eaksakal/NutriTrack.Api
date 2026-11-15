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

/// <summary>
/// Die Rueckfallkette Ollama -> OpenRouter -> Gemini, Ende zu Ende ueber /api/ai/parse-meal.
/// Alle drei Stubs werden in JEDEM Test gesetzt: die Tests teilen sich eine Factory, und ein
/// liegengebliebener Fehlschlag-Stub aus einem anderen Test aenderte sonst den Weg durch die Kette.
/// </summary>
public class AiProviderChainTests(AiProviderChainTests.AdminFactory factory)
    : IClassFixture<AiProviderChainTests.AdminFactory>
{
    public sealed class AdminFactory : NutriTrackApiFactory
    {
        public const string AdminEmail = "chef@example.com";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Admin:Email", AdminEmail);
        }
    }

    /// <summary>Ein Antwortrumpf mit genau einem Posten, dessen Name den Anbieter verraet.</summary>
    private static string Posten(string label) => $$"""
        {
          "items": [
            { "searchTerm": "{{label}}", "label": "{{label}}", "quantityInGrams": 100,
              "mealType": "Snack", "productKind": "generic",
              "estimate": { "calories": 52, "protein": 0.3, "carbohydrates": 14, "fat": 0.2 } }
          ]
        }
        """;

    private static HttpResponseMessage PcAus(HttpRequestMessage _) =>
        throw new HttpRequestException("Es konnte keine Verbindung hergestellt werden.");

    private async Task SetzeAnbieterAsync(string? provider)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.AiSettings.ExecuteDeleteAsync();
        await db.AiFailures.ExecuteDeleteAsync();

        if (provider is not null)
        {
            db.AiSettings.Add(new AiSettings { Id = 1, Provider = provider, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        factory.Services.GetRequiredService<AiSettingsProvider>().Invalidate();
    }

    private async Task<List<AiFailure>> ProtokollAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AiFailures.AsNoTracking().OrderBy(f => f.OccurredAt).ToListAsync();
    }

    private async Task<(HttpStatusCode Status, string? Label)> ErfasseAsync()
    {
        var (client, _, _) = await factory.CreateUserAsync();
        var response = await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        if (response.StatusCode != HttpStatusCode.OK)
            return (response.StatusCode, null);

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (response.StatusCode, json.GetProperty("items")[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task Ollama_Answers_WhenThePcIsOn()
    {
        await SetzeAnbieterAsync("ollama");
        try
        {
            factory.OllamaResponder = _ => StubOpenRouterHandler.Payload(Posten("vom PC"));
            factory.OpenRouterResponder = _ => StubOpenRouterHandler.Payload(Posten("von OpenRouter"));
            factory.GeminiResponder = _ => StubGeminiHandler.Payload(Posten("von Gemini"));

            var (status, label) = await ErfasseAsync();

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("vom PC", label);
            Assert.Empty(await ProtokollAsync());
        }
        finally
        {
            await SetzeAnbieterAsync(null);
        }
    }

    [Fact]
    public async Task PcOff_FallsBackToOpenRouter_AndLogsWhy()
    {
        await SetzeAnbieterAsync("ollama");
        try
        {
            factory.OllamaResponder = PcAus;
            factory.OpenRouterResponder = _ => StubOpenRouterHandler.Payload(Posten("von OpenRouter"));
            factory.GeminiResponder = _ => StubGeminiHandler.Payload(Posten("von Gemini"));

            var (status, label) = await ErfasseAsync();

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("von OpenRouter", label);

            // Ohne diesen Eintrag saehe der Betreiber nur, dass die Erfassung klappt - nicht,
            // dass sie seit Wochen erst auf einen ausgeschalteten PC wartet.
            var eintrag = Assert.Single(await ProtokollAsync());
            Assert.Equal("ollama", eintrag.Provider);
            Assert.Equal("Unavailable", eintrag.Kind);
            Assert.Equal(AiSettingsProvider.DefaultOllamaModel, eintrag.Model);
            Assert.Null(eintrag.ThinkingLevel);
            Assert.StartsWith("Ausweichen auf openrouter", eintrag.Reason);
        }
        finally
        {
            await SetzeAnbieterAsync(null);
        }
    }

    [Fact]
    public async Task PcOff_AndOpenRouterAtItsDailyLimit_FallsBackToGemini()
    {
        await SetzeAnbieterAsync("ollama");
        try
        {
            factory.OllamaResponder = _ => throw new TaskCanceledException("Zeitdeckel im Test.");
            factory.OpenRouterResponder = _ => StubOpenRouterHandler.RateLimited();
            factory.GeminiResponder = _ => StubGeminiHandler.Payload(Posten("von Gemini"));

            var (status, label) = await ErfasseAsync();

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("von Gemini", label);

            var protokoll = await ProtokollAsync();
            Assert.Collection(protokoll,
                e => { Assert.Equal("ollama", e.Provider); Assert.Equal("Timeout", e.Kind); },
                e => { Assert.Equal("openrouter", e.Provider); Assert.Equal("Quota", e.Kind); Assert.Equal(429, e.StatusCode); });
        }
        finally
        {
            await SetzeAnbieterAsync(null);
        }
    }

    [Fact]
    public async Task AllThreeFail_LastFailureReachesTheUser_AndNamesGemini()
    {
        await SetzeAnbieterAsync("ollama");
        try
        {
            factory.OllamaResponder = PcAus;
            factory.OpenRouterResponder = _ => StubOpenRouterHandler.UpstreamError();
            factory.GeminiResponder = _ => throw new HttpRequestException("Google weg.");

            var (status, _) = await ErfasseAsync();

            Assert.Equal(HttpStatusCode.ServiceUnavailable, status);

            // Der letzte Eintrag stammt vom Endpunkt. Vor AiException.Provider haette er den
            // EINGESTELLTEN Anbieter genannt - also "ollama", obwohl Gemini ausgefallen ist.
            var protokoll = await ProtokollAsync();
            Assert.Equal(["ollama", "openrouter", "gemini"], protokoll.Select(e => e.Provider));
            Assert.Equal("gemini-3.6-flash", protokoll[2].Model);
        }
        finally
        {
            await SetzeAnbieterAsync(null);
        }
    }

    [Fact]
    public async Task MalformedAnswer_IsRetriedOnceOnTheSameProvider_BeforeFallingBack()
    {
        await SetzeAnbieterAsync("ollama");
        try
        {
            var ollamaAufrufe = 0;
            factory.OllamaResponder = _ =>
            {
                ollamaAufrufe++;
                return StubOpenRouterHandler.Payload("kein json");
            };
            factory.OpenRouterResponder = _ => StubOpenRouterHandler.Payload(Posten("von OpenRouter"));
            factory.GeminiResponder = _ => StubGeminiHandler.Payload(Posten("von Gemini"));

            var (status, label) = await ErfasseAsync();

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("von OpenRouter", label);
            Assert.Equal(2, ollamaAufrufe);
            Assert.Equal("Schema", Assert.Single(await ProtokollAsync()).Kind);
        }
        finally
        {
            await SetzeAnbieterAsync(null);
        }
    }

    [Fact]
    public async Task OpenRouterSelected_FallsBackToGemini_ButNeverAsksThePc()
    {
        await SetzeAnbieterAsync("openrouter");
        try
        {
            var ollamaGefragt = false;
            factory.OllamaResponder = _ =>
            {
                ollamaGefragt = true;
                return StubOpenRouterHandler.Payload(Posten("vom PC"));
            };
            factory.OpenRouterResponder = _ => StubOpenRouterHandler.UpstreamError();
            factory.GeminiResponder = _ => StubGeminiHandler.Payload(Posten("von Gemini"));

            var (status, label) = await ErfasseAsync();

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("von Gemini", label);
            Assert.False(ollamaGefragt);
        }
        finally
        {
            await SetzeAnbieterAsync(null);
        }
    }

    [Fact]
    public async Task GeminiSelected_HasNoFallback()
    {
        // Das Verhalten von vor der Kette: wer Gemini einstellt, bekommt Gemini - auch dessen
        // Fehlschlag, statt still bei einem anderen Anbieter zu landen.
        await SetzeAnbieterAsync(null);

        factory.OllamaResponder = _ => StubOpenRouterHandler.Payload(Posten("vom PC"));
        factory.OpenRouterResponder = _ => StubOpenRouterHandler.Payload(Posten("von OpenRouter"));
        factory.GeminiResponder = _ => throw new HttpRequestException("Google weg.");

        var (status, _) = await ErfasseAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("gemini", Assert.Single(await ProtokollAsync()).Provider);
    }

    [Fact]
    public async Task Probe_TestsTheSelectedProviderOnly()
    {
        await SetzeAnbieterAsync("ollama");
        try
        {
            factory.OllamaResponder = PcAus;
            factory.OpenRouterResponder = _ => StubOpenRouterHandler.Payload(Posten("von OpenRouter"));

            var (client, _, _) = await factory.CreateUserAsync(AdminFactory.AdminEmail);
            var response = await client.PostAsync("/api/admin/settings/probe", null);

            // Eine Probe, die bei ausgeschaltetem PC still OpenRouters Antwort zeigte,
            // verschwiege genau das, was der Betreiber wissen will.
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(0, json.GetProperty("statusCode").GetInt32());
            Assert.Equal(AiSettingsProvider.DefaultOllamaModel, json.GetProperty("model").GetString());
        }
        finally
        {
            await SetzeAnbieterAsync(null);
        }
    }
}
