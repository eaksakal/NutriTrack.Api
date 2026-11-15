using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using NutriTrack.Api.Contracts.Ai;
using NutriTrack.Api.Services;
using NutriTrack.Api.Tests.Infrastructure;

namespace NutriTrack.Api.Tests;

/// <summary>
/// Nur was Ollama von OpenRouter unterscheidet - Adresse, Zugang, Modell. Der gemeinsame
/// OpenAI-Dialekt ist in OpenRouterServiceTests abgedeckt.
/// </summary>
public class OllamaServiceTests(NutriTrackApiFactory factory) : IClassFixture<NutriTrackApiFactory>
{
    private OllamaService Service()
    {
        factory.CreateClient();
        return factory.Services.GetRequiredService<OllamaService>();
    }

    [Fact]
    public async Task ParseAsync_TalksToTheConfiguredPc_WithoutAKey()
    {
        HttpRequestMessage? gesendet = null;
        string? body = null;
        factory.OllamaResponder = request =>
        {
            gesendet = request;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return StubOpenRouterHandler.Payload("""{"items":[]}""");
        };

        await Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }], string.Empty, CancellationToken.None);

        Assert.Equal("http://ollama.test:11434/v1/chat/completions", gesendet!.RequestUri!.ToString());
        Assert.Null(gesendet.Headers.Authorization);
        Assert.False(gesendet.Headers.Contains("Authorization"));

        using var sent = JsonDocument.Parse(body!);
        Assert.Equal(AiSettingsProvider.DefaultOllamaModel, sent.RootElement.GetProperty("model").GetString());
        Assert.Equal("json_schema", sent.RootElement.GetProperty("response_format").GetProperty("type").GetString());
    }

    [Fact]
    public async Task PcOff_IsReportedAsUnavailable()
    {
        factory.OllamaResponder = _ => throw new HttpRequestException("Verbindung abgelehnt.");

        var ex = await Assert.ThrowsAsync<AiUnavailableException>(() => Service().ParseAsync(
            [new ChatMessage { Role = "user", Text = "ein Apfel" }], string.Empty, CancellationToken.None));

        Assert.Contains("Ollama ist nicht erreichbar", ex.Detail);
    }

    private sealed class WithoutOllamaFactory : NutriTrackApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Ollama:BaseUrl", null);
        }
    }

    [Fact]
    public void WithoutBaseUrl_NamesTheMissingSetting()
    {
        using var ohne = new WithoutOllamaFactory();
        ohne.CreateClient();

        Assert.Equal("Ollama:BaseUrl", ohne.Services.GetRequiredService<OllamaService>().MissingSetting);
        Assert.Null(Service().MissingSetting);
    }
}
