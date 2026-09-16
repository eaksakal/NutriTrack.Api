namespace NutriTrack.Api.Services;

/// <summary>
/// Waehlt den Anbieter nach der gespeicherten Einstellung.
///
/// Scoped und nicht als Registrierung beim Start: welcher Anbieter gilt, steht erst zur Laufzeit
/// fest und aendert sich, sobald der Betreiber in der Verwaltung umschaltet. Ein beim Start
/// gebundener Dienst braeuchte dafuer einen Neustart - und genau den soll dieses Feature ersparen.
/// </summary>
public class AiProviderFactory(
    GeminiService gemini,
    OpenRouterService openRouter,
    AiSettingsProvider settingsProvider)
{
    public IAiProvider Current() =>
        settingsProvider.Read().Provider == IAiProvider.OpenRouter ? openRouter : gemini;
}
