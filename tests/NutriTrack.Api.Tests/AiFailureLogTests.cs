using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NutriTrack.Api.Services;
using NutriTrack.Api.Tests.Infrastructure;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Tests;

public class AiFailureLogTests(NutriTrackApiFactory factory) : IClassFixture<NutriTrackApiFactory>
{
    private async Task<List<AiFailure>> ProtokollAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.AiFailures.AsNoTracking().OrderByDescending(f => f.OccurredAt).ToListAsync();
    }

    private async Task LeereAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.AiFailures.ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Timeout_IsRecordedWithoutTheMealText()
    {
        await LeereAsync();
        var (client, _, _) = await factory.CreateUserAsync();

        factory.GeminiResponder = _ => throw new TaskCanceledException("Zeitdeckel im Test.");

        // Ein Wort, das ausser in der Mahlzeit nirgends vorkommt: taucht es im Protokoll auf,
        // ist der Esstext hineingeraten. Das ist die tragende Zusage dieses Features.
        await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "Zwetschgendatschi mit Sahne" } }
        });

        var eintrag = Assert.Single(await ProtokollAsync());
        Assert.Equal("Timeout", eintrag.Kind);
        Assert.Equal("gemini-3.6-flash", eintrag.Model);
        Assert.True(eintrag.DurationMs >= 0);
        Assert.DoesNotContain("Zwetschgendatschi", eintrag.Reason);
    }

    [Fact]
    public async Task MalformedResponse_IsRecordedAsSchema()
    {
        await LeereAsync();
        var (client, _, _) = await factory.CreateUserAsync();

        factory.GeminiResponder = _ => StubGeminiHandler.Payload("kein json");

        await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        var protokoll = await ProtokollAsync();
        Assert.Contains(protokoll, f => f.Kind == "Schema");
    }

    [Fact]
    public async Task Quota_IsRecordedWithStatusCode()
    {
        await LeereAsync();
        var (client, _, _) = await factory.CreateUserAsync();

        factory.GeminiResponder = _ => StubGeminiHandler.InteractionsQuotaFailure();

        await client.PostAsJsonAsync("/api/ai/parse-meal", new
        {
            messages = new[] { new { role = "user", text = "ein Apfel" } }
        });

        var eintrag = Assert.Single(await ProtokollAsync());
        Assert.Equal("Quota", eintrag.Kind);
        Assert.Equal(429, eintrag.StatusCode);
    }

    [Fact]
    public async Task Recorder_KeepsOnlyTheNewestEntries()
    {
        await LeereAsync();
        factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<AiFailureRecorder>();

        for (var i = 0; i < AiFailureRecorder.MaxEntries + 5; i++)
            await recorder.RecordAsync("Timeout", $"Fehlschlag {i}", 10, null, CancellationToken.None);

        var protokoll = await ProtokollAsync();
        Assert.Equal(AiFailureRecorder.MaxEntries, protokoll.Count);
        Assert.Contains(protokoll, f => f.Reason.EndsWith($"{AiFailureRecorder.MaxEntries + 4}"));
        Assert.DoesNotContain(protokoll, f => f.Reason == "Fehlschlag 0");
    }

    [Fact]
    public async Task Recorder_TruncatesLongReasons()
    {
        await LeereAsync();
        factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var recorder = scope.ServiceProvider.GetRequiredService<AiFailureRecorder>();

        await recorder.RecordAsync("Schema", new string('x', 900), 10, null, CancellationToken.None);

        var eintrag = Assert.Single(await ProtokollAsync());
        Assert.True(eintrag.Reason.Length <= 200);
    }
}
