using System.Diagnostics;
using NutriTrack.Api.Contracts.Ai;

namespace NutriTrack.Api.Services;

/// <summary>
/// Der eingestellte Anbieter samt seinen Rueckfaellen, nach aussen ein einziger IAiProvider.
///
/// Reihenfolge ist <see cref="IAiProvider.All"/>: Ollama, OpenRouter, Gemini. Die Kette beginnt
/// beim eingestellten Anbieter und laeuft von dort nach hinten - "ollama" heisst also Ollama, dann
/// OpenRouter, dann Gemini; "gemini" heisst Gemini allein, wie vor der Kette. Nach VORNE geht es
/// bewusst nicht: wer OpenRouter einstellt, will nicht, dass bei dessen Ausfall still der PC
/// angefragt wird. Nur eingerichtete Anbieter (MissingSetting == null) sind Glieder.
///
/// Weiter zum naechsten geht es bei jedem der drei KI-Fehlschlaege: Ausfall (PC aus, Zeitdeckel),
/// Mengengrenze und ein Schemabruch, der auch den zweiten Anlauf uebersteht. Ein Abbruch durch
/// den Aufrufer ist kein Fehlschlag und schlaegt durch.
///
/// Jeder uebersprungene Fehlschlag landet im Fehlerprotokoll, mit dem Namen des ausgefallenen
/// Anbieters und dem Ziel des Ausweichens. Ohne das sieht der Betreiber nur, dass die Erfassung
/// klappt - nicht, dass sie seit Wochen bei jeder Anfrage erst zwei Sekunden auf einen
/// ausgeschalteten PC wartet. Den Fehlschlag des LETZTEN Glieds protokolliert wie bisher der
/// Endpunkt; damit er den richtigen Anbieter nennt, traegt die Ausnahme ihn in
/// <see cref="AiException.Provider"/>.
/// </summary>
public class AiProviderChain(
    IReadOnlyList<IAiProvider> members,
    AiFailureRecorder recorder,
    ILogger<AiProviderChain> logger) : IAiProvider
{
    /// <summary>Der eingestellte Anbieter - das erste Glied, auch wenn er nicht eingerichtet ist.</summary>
    public IAiProvider Selected => members[0];

    /// <summary>Die Glieder, die wirklich gefragt werden.</summary>
    public IReadOnlyList<IAiProvider> Active => members.Where(m => m.MissingSetting is null).ToList();

    public string Name => Selected.Name;

    /// <summary>Eingerichtet ist die Kette, sobald EIN Glied es ist. Ist keines eingerichtet,
    /// nennt sie den fehlenden Wert des eingestellten Anbieters - das ist der, den der Betreiber
    /// gemeint hat.</summary>
    public string? MissingSetting => Active.Count > 0 ? null : Selected.MissingSetting;

    public Task<AiParseResult> ParseAsync(
        IReadOnlyList<ChatMessage> messages, string historyBlock, CancellationToken ct) =>
        RunAsync(p => p.ParseAsync(messages, historyBlock, ct), retryOnMalformed: true);

    /// <summary>Ohne zweiten Anlauf je Glied - die Zielwunsch-Deutung hatte auch vor der Kette
    /// keinen.</summary>
    public Task<AiWishResult> ParseWishAsync(string wish, CancellationToken ct) =>
        RunAsync(p => p.ParseWishAsync(wish, ct), retryOnMalformed: false);

    /// <summary>
    /// Die Probe gilt dem EINGESTELLTEN Anbieter, nicht der Kette: sie ist ein Diagnosewerkzeug,
    /// und eine Probe, die bei ausgeschaltetem PC still OpenRouters Antwort zeigt, verschwiege
    /// genau das, was der Betreiber wissen will.
    /// </summary>
    public Task<AiProbeResult> ProbeAsync(CancellationToken ct) => Selected.ProbeAsync(ct);

    private async Task<T> RunAsync<T>(Func<IAiProvider, Task<T>> call, bool retryOnMalformed)
    {
        var aktive = Active;

        // Nichts eingerichtet: der eingestellte Anbieter wirft selbst seinen "fehlt"-Fehler. Die
        // Endpunkte pruefen MissingSetting vorher, hier kommt man nur bei einem Umbau an.
        if (aktive.Count == 0)
            aktive = [Selected];

        if (aktive[0] != Selected)
            logger.LogWarning(
                "{Anbieter} ist nicht eingerichtet ({Fehlt} fehlt); es antwortet {Ersatz}.",
                Selected.Name, Selected.MissingSetting, aktive[0].Name);

        for (var i = 0; ; i++)
        {
            var anbieter = aktive[i];
            var naechster = i + 1 < aktive.Count ? aktive[i + 1] : null;
            var uhr = Stopwatch.StartNew();

            try
            {
                return await CallAsync(anbieter, call, retryOnMalformed);
            }
            catch (AiException ex)
            {
                ex.Provider = anbieter.Name;

                if (naechster is null)
                    throw;

                var (art, grund, status) = AiFailureRecorder.Describe(ex);
                logger.LogWarning(
                    "{Anbieter} fiel aus ({Art}); weiter mit {Naechster}.", anbieter.Name, art, naechster.Name);
                await recorder.RecordAsync(
                    art, $"Ausweichen auf {naechster.Name}: {grund}", uhr.ElapsedMilliseconds, status, anbieter.Name);
            }
        }
    }

    private async Task<T> CallAsync<T>(IAiProvider anbieter, Func<IAiProvider, Task<T>> call, bool retryOnMalformed)
    {
        try
        {
            return await call(anbieter);
        }
        catch (AiMalformedResponseException) when (retryOnMalformed)
        {
            // Genau ein zweiter Anlauf beim SELBEN Anbieter: Modelle straucheln gelegentlich
            // einmalig am Schema. Mehr Versuche kosten Kontingent und Wartezeit, ohne die
            // Trefferquote zu heben. Stand frueher in AiMealAssistant; hier, weil ein zweiter
            // Anlauf der ganzen Kette jedes Glied doppelt fragen wuerde.
            logger.LogWarning("{Anbieter}-Antwort unbrauchbar, ein Wiederholungsversuch.", anbieter.Name);
            return await call(anbieter);
        }
    }
}
