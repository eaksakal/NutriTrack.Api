namespace NutriTrack.Api.Services;

/// <summary>
/// Ein Sprachmodell im eigenen Heimnetz: Ollama auf dem Gaming-PC, angebunden ueber dessen
/// OpenAI-kompatible Schnittstelle (Umsetzung in OpenAiCompatibleService).
///
/// Warum es ihn gibt: kostenlos, ohne Tageskontingent, und der Esstext verlaesst das Haus nicht -
/// bei Gemini und OpenRouter lesen die Anbieter im Freikontingent mit. Dasselbe Muster laeuft im
/// homecloud-Projekt fuer Nextclouds Context Chat (Ollama 0.40, Firewallregel nur fuer den
/// Server 192.168.188.96, auf dem auch NutriTrack laeuft).
///
/// Der Haken: der PC ist nicht immer an. Deshalb ist Ollama nur das erste Glied der Kette in
/// AiProviderChain, und deshalb hat der HttpClient einen kurzen Verbindungsdeckel (Program.cs) -
/// ein ausgeschalteter PC soll zwei Sekunden kosten, nicht neunzig.
///
/// Gemessen am 2026-11-15 auf dem PC selbst, echte Systemanweisung, zehn Testeingaben:
/// qwen2.5:14b im Schnitt 4,2 s (warm), erstes Laden des Modells rund 50 s. qwen3:8b war mit
/// 2,6 s schneller, setzte aber grobe Mengen ein (zwei Broetchen = 360 g, "die andere Haelfte"
/// beim Verweis auf den Verlauf) - deshalb qwen2.5:14b als Vorgabe.
/// </summary>
public class OllamaService(
    HttpClient httpClient,
    IConfiguration configuration,
    AiSettingsProvider settingsProvider,
    ILogger<OllamaService> logger,
    TimeProvider timeProvider) : OpenAiCompatibleService(httpClient, settingsProvider, logger, timeProvider)
{
    private const string BaseUrlSetting = "Ollama:BaseUrl";

    public override string Name => IAiProvider.Ollama;

    /// <summary>Ollama braucht keinen Schluessel - "eingerichtet" heisst: die Adresse steht da.</summary>
    public override string? MissingSetting =>
        string.IsNullOrWhiteSpace(configuration[BaseUrlSetting]) ? BaseUrlSetting : null;

    protected override string Label => "Ollama";

    protected override string Endpoint =>
        $"{configuration[BaseUrlSetting]!.Trim().TrimEnd('/')}/v1/chat/completions";

    protected override string Model(AiSettingsSnapshot einstellungen) => einstellungen.OllamaModel;

    /// <summary>Kein Zugang noetig: geschuetzt wird Ollama durch die Firewall des PCs, die nur
    /// den Server durchlaesst.</summary>
    protected override void Authorize(HttpRequestMessage request)
    {
    }
}
