using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NutriTrack.Infrastructure.Data;

/// <summary>
/// Nur fuer "dotnet ef migrations add/script". Ohne diese Factory muesste das EF-Tool den
/// Host aus NutriTrack.Api hochfahren; der bricht aber absichtlich ab, wenn NUTRITRACK_Jwt__Key
/// fehlt - dann liesse sich auf einer frischen Maschine keine Migration mehr erzeugen.
/// Die Datei hier wird zur Laufzeit nie angefasst, der Pfad ist reiner Platzhalter fuer das
/// Scaffolding (Migrationen sind provider-, nicht datenbankabhaengig).
/// </summary>
public class AppDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options;

        return new AppDbContext(options);
    }
}
