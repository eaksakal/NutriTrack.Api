using NutriTrack.Api.Contracts.Ai;

namespace NutriTrack.Api.Services;

/// <summary>
/// Was die Anwendung von einem KI-Anbieter braucht - nicht mehr.
///
/// Es gibt sie, weil das Freikontingent von Google 20 Anfragen am TAG erlaubt (gemessen am
/// 2026-09-16) und damit fuer ein Ernaehrungstagebuch zu klein ist. Ein zweiter Anbieter loest
/// das; die Wahl zwischen beiden trifft der Betreiber zur Laufzeit, nicht der Uebersetzer.
///
/// Absichtlich schmal: drei Aufrufe, keine anbieterspezifischen Begriffe. Die Denkstufe etwa ist
/// eine Eigenheit von Gemini und hat hier nichts zu suchen - sie steckt in dessen Umsetzung.
/// </summary>
public interface IAiProvider
{
    public const string Gemini = "gemini";
    public const string OpenRouter = "openrouter";

    /// <summary>"gemini" oder "openrouter". Wandert ins Fehlerprotokoll, damit nach einem
    /// Wechsel feststeht, welcher Dienst welchen Fehlschlag verursacht hat.</summary>
    string Name { get; }

    /// <summary>Konfigurationsschluessel des Zugangsschluessels dieses Anbieters ("Gemini:ApiKey"
    /// oder "OpenRouter:ApiKey"). Eine Eigenschaft statt einer Fallunterscheidung an jeder
    /// Aufrufstelle: ohne sie stuende `provider.Name == IAiProvider.OpenRouter ? ... : ...`
    /// gleichlautend in AiEndpoints, AdminEndpoints UND GoalsEndpoints - und ein dritter Anbieter
    /// muesste an drei Stellen zugleich nachgezogen werden statt an einer.</summary>
    string ApiKeySetting { get; }

    Task<AiParseResult> ParseAsync(
        IReadOnlyList<ChatMessage> messages, string historyBlock, CancellationToken ct);

    Task<AiWishResult> ParseWishAsync(string wish, CancellationToken ct);

    Task<AiProbeResult> ProbeAsync(CancellationToken ct);
}
