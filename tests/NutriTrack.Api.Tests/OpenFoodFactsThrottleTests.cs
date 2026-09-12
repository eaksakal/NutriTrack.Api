using Microsoft.Extensions.Logging.Abstractions;
using NutriTrack.Api.Services;

namespace NutriTrack.Api.Tests;

/// <summary>
/// Die Bremse haelt die Suchanfragen unter dem Limit von OpenFoodFacts (10/min/IP). Getestet wird
/// mit einer gestellten Uhr statt mit echtem Warten - ein Test, der eine Minute schlaeft, wird
/// beim naechsten Aufraeumen zu Recht geloescht.
/// </summary>
public class OpenFoodFactsThrottleTests
{
    /// <summary>Nur so viel TimeProvider, wie die Bremse tatsaechlich benutzt.</summary>
    private sealed class StellbareUhr(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Vorstellen(TimeSpan spanne) => _now += spanne;
    }

    private static (OpenFoodFactsThrottle Throttle, StellbareUhr Uhr) Erzeugen()
    {
        var uhr = new StellbareUhr(new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero));
        return (new OpenFoodFactsThrottle(uhr, NullLogger<OpenFoodFactsThrottle>.Instance), uhr);
    }

    [Fact]
    public void TryAcquireSearch_BisZumLimit_Erlaubt_DannNicht()
    {
        var (throttle, _) = Erzeugen();

        // Acht sind erlaubt - bewusst zwei unter dem Limit von OpenFoodFacts, siehe Begruendung
        // in OpenFoodFactsThrottle.
        for (var i = 1; i <= 8; i++)
            Assert.True(throttle.TryAcquireSearch(), $"Aufruf {i} haette erlaubt sein muessen.");

        Assert.False(throttle.TryAcquireSearch());
        Assert.False(throttle.TryAcquireSearch());
    }

    [Fact]
    public void TryAcquireSearch_NachAblaufDesFensters_WiederErlaubt()
    {
        var (throttle, uhr) = Erzeugen();

        for (var i = 0; i < 8; i++)
            Assert.True(throttle.TryAcquireSearch());

        Assert.False(throttle.TryAcquireSearch());

        uhr.Vorstellen(TimeSpan.FromSeconds(61));

        Assert.True(throttle.TryAcquireSearch());
    }

    [Fact]
    public void TryAcquireSearch_FensterGleitet_GibtNichtAllesAufEinmalFrei()
    {
        var (throttle, uhr) = Erzeugen();

        // Vier Aufrufe, dann eine halbe Minute Pause, dann vier weitere: das Fenster ist voll.
        for (var i = 0; i < 4; i++)
            Assert.True(throttle.TryAcquireSearch());

        uhr.Vorstellen(TimeSpan.FromSeconds(30));

        for (var i = 0; i < 4; i++)
            Assert.True(throttle.TryAcquireSearch());

        Assert.False(throttle.TryAcquireSearch());

        // Weitere 31 s: nur die ERSTEN vier sind aus dem Fenster gefallen, nicht alle acht.
        uhr.Vorstellen(TimeSpan.FromSeconds(31));

        for (var i = 0; i < 4; i++)
            Assert.True(throttle.TryAcquireSearch(), $"Freigabe {i + 1} der ersten Vierergruppe fehlt.");

        Assert.False(throttle.TryAcquireSearch());
    }
}
