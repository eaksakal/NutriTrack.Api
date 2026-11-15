namespace NutriTrack.Api.Services;

/// <summary>
/// Der zweite Anbieter, angebunden ueber OpenRouters OpenAI-Dialekt (Umsetzung in
/// OpenAiCompatibleService).
///
/// Es gibt ihn, weil Googles Freikontingent 20 Anfragen am TAG erlaubt (gemessen 2026-09-16) und
/// damit fuer ein Ernaehrungstagebuch zu klein ist. OpenRouter gibt 50 - dafuer brauchten die
/// kostenlosen Modelle im selben Test 24 bis 35 Sekunden statt Geminis 3 bis 9.
/// </summary>
public class OpenRouterService(
    HttpClient httpClient,
    IConfiguration configuration,
    AiSettingsProvider settingsProvider,
    ILogger<OpenRouterService> logger,
    TimeProvider timeProvider) : OpenAiCompatibleService(httpClient, settingsProvider, logger, timeProvider)
{
    private const string ApiKeySetting = "OpenRouter:ApiKey";

    public override string Name => IAiProvider.OpenRouter;

    public override string? MissingSetting =>
        string.IsNullOrWhiteSpace(configuration[ApiKeySetting]) ? ApiKeySetting : null;

    protected override string Label => "OpenRouter";

    protected override string Endpoint => "https://openrouter.ai/api/v1/chat/completions";

    protected override string Model(AiSettingsSnapshot einstellungen) => einstellungen.OpenRouterModel;

    protected override void Authorize(HttpRequestMessage request) =>
        request.Headers.Add("Authorization", $"Bearer {configuration[ApiKeySetting]}");
}
