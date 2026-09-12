using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NutriTrack.Api.Contracts.Auth;
using NutriTrack.Api.Services;
using NutriTrack.Infrastructure.Data;

namespace NutriTrack.Api.Tests.Infrastructure;

/// <summary>
/// Startet die echte API im TestServer. Jede Instanz bekommt eine eigene SQLite-Datei, damit
/// parallel laufende Testklassen sich weder Schema noch Daten teilen.
/// </summary>
// AuthResponse dient nur als Anker fuer die API-Assembly: Program.cs nutzt Top-Level-Statements,
// die generierte Program-Klasse ist internal und damit als TEntryPoint nicht erreichbar.
// WebApplicationFactory braucht lediglich irgendeinen oeffentlichen Typ aus der Ziel-Assembly.
public class NutriTrackApiFactory : WebApplicationFactory<AuthResponse>, IAsyncLifetime
{
    private const string TestJwtKey = "NutriTrack-Integration-Test-Signing-Key-0123456789";

    // Program.cs liest Jwt:Key & Co. direkt aus builder.Configuration, also noch bevor
    // WebApplicationFactory ihre eigenen Settings einspeisen kann, und bricht bei fehlendem
    // Schluessel mit Exit-Code 1 ab. Echte Prozess-Umgebungsvariablen sind die einzige Quelle,
    // die zu diesem Zeitpunkt schon steht.
    static NutriTrackApiFactory()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Testing");
        Environment.SetEnvironmentVariable("NUTRITRACK_Jwt__Key", TestJwtKey);
        Environment.SetEnvironmentVariable("NUTRITRACK_Jwt__Issuer", "NutriTrack");
        Environment.SetEnvironmentVariable("NUTRITRACK_Jwt__Audience", "NutriTrack");
        Environment.SetEnvironmentVariable("NUTRITRACK_DATA_ROOT", DataRoot);
    }

    private static readonly string DataRoot = Path.Combine(Path.GetTempPath(), "nutritrack-tests");

    private readonly string _databasePath = Path.Combine(
        DataRoot,
        $"nutritrack-tests-{Guid.NewGuid():N}.db");

    // "Foreign Keys=True" wie im Produktivcode, sonst ignoriert SQLite den FK
    // MealEntry -> FoodItem und die Tests wuerden eine Integritaet pruefen, die es nicht gibt.
    // "Pooling=False", damit nach dem Testlauf kein Handle mehr auf der Datei liegt und sie
    // geloescht werden kann.
    private string ConnectionString => $"Data Source={_databasePath};Foreign Keys=True;Pooling=False";

    /// <summary>Antwortverhalten des Gemini-Stubs. Je Testklasse eine eigene Factory-Instanz,
    /// deshalb ist eine veraenderbare Property hier gefahrlos.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage> GeminiResponder { get; set; } =
        _ => StubGeminiHandler.Payload("""{"items":[]}""");

    /// <summary>Null bedeutet: normales Stub-Verhalten. Gesetzt: diese Antwort fuer jede Anfrage.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? OpenFoodFactsResponder { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(DataRoot);

        builder.UseContentRoot(ApiContentRoot.Path);
        builder.UseEnvironment("Testing");

        builder.UseSetting("ConnectionStrings:DefaultConnection", ConnectionString);
        builder.UseSetting("Jwt:Key", TestJwtKey);
        builder.UseSetting("Jwt:Issuer", "NutriTrack");
        builder.UseSetting("Jwt:Audience", "NutriTrack");
        builder.UseSetting("Gemini:ApiKey", "test-key");
        builder.UseSetting("Gemini:Model", "gemini-3.5-flash");

        builder.ConfigureTestServices(services =>
        {
            RemoveDbContextRegistrations(services);
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(ConnectionString));

            // Erneutes AddHttpClient fuer denselben typisierten Client haengt nur eine weitere
            // Konfiguration an denselben Namen an; der zuletzt gesetzte Primary-Handler gewinnt.
            services.AddHttpClient<OpenFoodFactsService>()
                .ConfigurePrimaryHttpMessageHandler(() => new StubOpenFoodFactsHandler(request => OpenFoodFactsResponder?.Invoke(request)));

            services.AddHttpClient<GeminiService>()
                .ConfigurePrimaryHttpMessageHandler(() => new StubGeminiHandler(request => GeminiResponder(request)));
        });
    }

    /// <summary>
    /// Der Produktivcode registriert den DbContext samt Provider bereits in Program.cs. Ohne das
    /// Entfernen aller zugehoerigen Descriptors wuerde die Options-Konfiguration des Produktivcodes
    /// zusaetzlich angewendet und der Provider doppelt gesetzt.
    /// </summary>
    private static void RemoveDbContextRegistrations(IServiceCollection services)
    {
        var obsolete = services
            .Where(d => d.ServiceType == typeof(AppDbContext)
                        || d.ServiceType == typeof(IDbContextFactory<AppDbContext>)
                        || (d.ServiceType.FullName?.Contains("DbContextOptions", StringComparison.Ordinal) ?? false))
            .ToList();

        foreach (var descriptor in obsolete)
            services.Remove(descriptor);
    }

    /// <summary>
    /// Frisches Schema pro Testklasse. EnsureCreated statt Migrate, damit die Tests unabhaengig
    /// davon laufen, welche Migrationen der Produktivcode gerade mitbringt; das Modell ist dasselbe.
    /// </summary>
    public async Task ResetDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureDeletedAsync();
        await db.Database.EnsureCreatedAsync();
    }

    // Explizit implementiert: WebApplicationFactory besitzt bereits ein DisposeAsync mit
    // ValueTask-Rueckgabe, das mit der gleichnamigen IAsyncLifetime-Methode kollidieren wuerde.
    Task IAsyncLifetime.InitializeAsync() => ResetDatabaseAsync();

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing)
            return;

        foreach (var file in new[] { _databasePath, _databasePath + "-shm", _databasePath + "-wal" })
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch (IOException)
            {
                // Aufraeumen im Temp-Verzeichnis darf keinen Testlauf zum Scheitern bringen.
            }
        }
    }
}
