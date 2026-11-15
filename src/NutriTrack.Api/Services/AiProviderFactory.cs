namespace NutriTrack.Api.Services;

/// <summary>
/// Baut nach der gespeicherten Einstellung die Kette aus eingestelltem Anbieter und seinen
/// Rueckfaellen (siehe AiProviderChain).
///
/// Scoped und nicht als Registrierung beim Start: welcher Anbieter gilt, steht erst zur Laufzeit
/// fest und aendert sich, sobald der Betreiber in der Verwaltung umschaltet. Ein beim Start
/// gebundener Dienst braeuchte dafuer einen Neustart - und genau den soll dieses Feature ersparen.
/// </summary>
public class AiProviderFactory(
    GeminiService gemini,
    OpenRouterService openRouter,
    OllamaService ollama,
    AiSettingsProvider settingsProvider,
    AiFailureRecorder recorder,
    ILogger<AiProviderChain> chainLogger)
{
    public AiProviderChain Current()
    {
        var eingestellt = settingsProvider.Read().Provider;

        var glieder = IAiProvider.All
            .SkipWhile(name => name != eingestellt)
            .Select(Anbieter)
            .ToList();

        return new AiProviderChain(glieder, recorder, chainLogger);
    }

    private IAiProvider Anbieter(string name) => name switch
    {
        IAiProvider.Ollama => ollama,
        IAiProvider.OpenRouter => openRouter,
        _ => gemini,
    };
}
