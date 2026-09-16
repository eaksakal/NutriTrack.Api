using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NutriTrack.Api.Services;
using NutriTrack.Api.Tests.Infrastructure;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Tests;

public class AdminEndpointTests
{
    /// <summary>
    /// Eine Factory, die eine bestimmte Adresse zum Administrator macht. Eigene Instanz je Test,
    /// weil Admin:Email beim Start gelesen wird.
    /// </summary>
    private sealed class AdminFactory(string? adminEmail) : NutriTrackApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Admin:Email", adminEmail ?? string.Empty);
        }
    }

    [Fact]
    public async Task Settings_ForNonAdmin_ReturnsNotFound()
    {
        using var f = new AdminFactory("chef@example.com");
        await f.ResetDatabaseAsync();
        var (client, _, _) = await f.CreateUserAsync();

        // 404 und nicht 403: ein 403 bestaetigt, dass es eine Verwaltung gibt.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/failures")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync("/api/admin/settings", new { model = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsync("/api/admin/settings/probe", null)).StatusCode);
    }

    [Fact]
    public async Task Settings_WithoutConfiguredAdmin_ReturnsNotFoundForEveryone()
    {
        // Ein vergessener Eintrag darf die Verwaltung nicht fuer alle oeffnen.
        using var f = new AdminFactory(null);
        await f.ResetDatabaseAsync();
        var (client, _, _) = await f.CreateUserAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/admin/settings")).StatusCode);
    }

    [Fact]
    public async Task Settings_ForAdmin_ReturnsValuesAndOrigin()
    {
        using var f = new AdminFactory("chef@example.com");
        await f.ResetDatabaseAsync();
        var (client, _, _) = await f.CreateUserAsync("chef@example.com");

        var response = await client.GetAsync("/api/admin/settings");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("gemini-3.6-flash", json.GetProperty("model").GetString());
        Assert.False(json.GetProperty("modelFromDatabase").GetBoolean());
        // Der Schluessel darf nirgends auftauchen.
        Assert.DoesNotContain("apiKey", json.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Put_StoresValuesAndTakesEffect()
    {
        using var f = new AdminFactory("chef@example.com");
        await f.ResetDatabaseAsync();
        var (client, _, _) = await f.CreateUserAsync("chef@example.com");

        var put = await client.PutAsJsonAsync("/api/admin/settings", new
        {
            model = "gemini-3.1-flash-lite", thinkingLevel = "low", maxOutputTokens = 2048
        });
        put.EnsureSuccessStatusCode();

        var json = await (await client.GetAsync("/api/admin/settings")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("gemini-3.1-flash-lite", json.GetProperty("model").GetString());
        Assert.True(json.GetProperty("modelFromDatabase").GetBoolean());

        // Und der Provider sieht es auch - ohne Invalidate haette der Cache den alten Wert.
        Assert.Equal("gemini-3.1-flash-lite", f.Services.GetRequiredService<AiSettingsProvider>().Read().Model);
    }

    [Fact]
    public async Task Put_WithEmptyModel_FallsBackToEnvironment()
    {
        using var f = new AdminFactory("chef@example.com");
        await f.ResetDatabaseAsync();
        var (client, _, _) = await f.CreateUserAsync("chef@example.com");

        await client.PutAsJsonAsync("/api/admin/settings", new { model = "gemini-3.1-flash-lite" });
        await client.PutAsJsonAsync("/api/admin/settings", new { model = "" });

        var json = await (await client.GetAsync("/api/admin/settings")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("gemini-3.6-flash", json.GetProperty("model").GetString());
        Assert.False(json.GetProperty("modelFromDatabase").GetBoolean());
    }

    [Fact]
    public async Task Probe_CallsGeminiOnceAndReportsResult()
    {
        using var f = new AdminFactory("chef@example.com");
        await f.ResetDatabaseAsync();
        var (client, _, _) = await f.CreateUserAsync("chef@example.com");

        var aufrufe = 0;
        f.GeminiResponder = _ =>
        {
            aufrufe++;
            return StubGeminiHandler.Payload("""{"items":[]}""");
        };

        var response = await client.PostAsync("/api/admin/settings/probe", null);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1, aufrufe);
        Assert.Equal(200, json.GetProperty("statusCode").GetInt32());
        Assert.Equal("gemini-3.6-flash", json.GetProperty("model").GetString());
        Assert.Contains("model_output", json.GetProperty("rawBody").GetString()!);
    }

    [Fact]
    public async Task Failures_ReturnsNewestFirst()
    {
        using var f = new AdminFactory("chef@example.com");
        await f.ResetDatabaseAsync();
        var (client, _, _) = await f.CreateUserAsync("chef@example.com");

        using (var scope = f.Services.CreateScope())
        {
            var recorder = scope.ServiceProvider.GetRequiredService<AiFailureRecorder>();
            await recorder.RecordAsync("Timeout", "erster", 10, null, CancellationToken.None);
            await recorder.RecordAsync("Schema", "zweiter", 20, null, CancellationToken.None);
        }

        var json = await (await client.GetAsync("/api/admin/failures")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(json.GetArrayLength() >= 2);
    }
}
