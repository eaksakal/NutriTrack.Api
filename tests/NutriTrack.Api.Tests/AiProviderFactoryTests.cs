using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NutriTrack.Api.Services;
using NutriTrack.Api.Tests.Infrastructure;
using NutriTrack.Domain.Entities;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Tests;

public class AiProviderFactoryTests(NutriTrackApiFactory factory) : IClassFixture<NutriTrackApiFactory>
{
    private async Task SetzeAnbieterAsync(string? provider)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var vorhandene = await db.AiSettings.SingleOrDefaultAsync();
        if (vorhandene is not null)
            db.AiSettings.Remove(vorhandene);
        await db.SaveChangesAsync();

        if (provider is not null)
        {
            db.AiSettings.Add(new AiSettings { Id = 1, Provider = provider, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        factory.Services.GetRequiredService<AiSettingsProvider>().Invalidate();
    }

    private IAiProvider Current()
    {
        factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AiProviderFactory>().Current();
    }

    [Fact]
    public async Task Current_WithoutSetting_IsGemini()
    {
        await SetzeAnbieterAsync(null);
        Assert.Equal("gemini", Current().Name);
    }

    [Fact]
    public async Task Current_WithOpenRouter_IsOpenRouter()
    {
        await SetzeAnbieterAsync("openrouter");
        try
        {
            Assert.Equal("openrouter", Current().Name);
        }
        finally
        {
            await SetzeAnbieterAsync(null);
        }
    }

    [Fact]
    public async Task Current_ChangesWithoutRestart()
    {
        // Der Kern des Features: umschalten, ohne die Anwendung neu zu starten.
        await SetzeAnbieterAsync(null);
        Assert.Equal("gemini", Current().Name);

        await SetzeAnbieterAsync("openrouter");
        try
        {
            Assert.Equal("openrouter", Current().Name);
        }
        finally
        {
            await SetzeAnbieterAsync(null);
        }
    }
}
