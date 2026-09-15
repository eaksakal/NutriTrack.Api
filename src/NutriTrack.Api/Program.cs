using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using NutriTrack.Api.Endpoints;
using NutriTrack.Api.Services;
using NutriTrack.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

// Alle Betriebswerte kommen aus der Umgebung, nicht aus appsettings.json. Praefix NUTRITRACK_,
// Doppel-Unterstrich als Ebenentrenner - also NUTRITRACK_Jwt__Key, NUTRITRACK_Jwt__Issuer,
// NUTRITRACK_Jwt__Audience, NUTRITRACK_Cors__Origins__0. Der Praefix wird beim Einlesen
// abgeschnitten; im Code heissen die Schluessel weiterhin Jwt:Key usw. Eigener Praefix statt
// des praefixlosen Defaults, damit eine fremde PATH- oder LANG-Variable nie versehentlich
// einen Konfigurationswert ueberschreibt.
builder.Configuration.AddEnvironmentVariables("NUTRITRACK_");

// ---------------------------------------------------------------------------------------
// Datenwurzel und Datenbankdatei
// ---------------------------------------------------------------------------------------
// Im Container ist /data ein Bind-Mount (siehe docker-compose.yml), lokal reicht ./data neben
// dem ContentRoot. Bewusst eine eigene Variable statt eines fertigen Connection-Strings:
// der Betrieb soll einen Ordner angeben, keinen SQLite-Dialekt kennen muessen.
var dataRoot = Environment.GetEnvironmentVariable("NUTRITRACK_DATA_ROOT") is { Length: > 0 } configuredRoot
    ? configuredRoot
    : Path.Combine(builder.Environment.ContentRootPath, "data");
dataRoot = Path.GetFullPath(dataRoot);

// Microsoft.Data.Sqlite legt die Datei an, das Verzeichnis aber nicht. Fehlt es, scheitert
// erst der erste Verbindungsaufbau mit "unable to open database file" - der Start selbst
// saehe gesund aus.
Directory.CreateDirectory(dataRoot);

var databasePath = Path.Combine(dataRoot, "nutritrack.db");

// "Default Timeout" ist bei Microsoft.Data.Sqlite der busy_timeout in Sekunden. SQLite hat
// genau einen Schreiber, und Identity schreibt bei jedem fehlgeschlagenen Login in
// AspNetUsers (lockoutOnFailure). 30 s warten ist billiger als ein sporadisches
// "database is locked" als 500er. "Foreign Keys=True" erzwingt den FK MealEntry -> FoodItem,
// den SQLite sonst pro Verbindung stillschweigend ignoriert.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") is { Length: > 0 } overrideCs
    ? overrideCs
    : $"Data Source={databasePath};Foreign Keys=True;Default Timeout=30";

// ---------------------------------------------------------------------------------------
// Secrets: lieber gar nicht starten als mit einem Default, mit dem jeder Tokens faelschen kann
// ---------------------------------------------------------------------------------------
var jwtKey = builder.Configuration["Jwt:Key"];
var jwtIssuer = builder.Configuration["Jwt:Issuer"];
var jwtAudience = builder.Configuration["Jwt:Audience"];

if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < 32)
{
    Console.Error.WriteLine(
        "FATAL: Jwt:Key fehlt oder ist kuerzer als 32 Zeichen. HMAC-SHA256 braucht mindestens " +
        "256 Bit Schluessel. Setze die Umgebungsvariable NUTRITRACK_Jwt__Key (Server: in der " +
        ".env neben der docker-compose.yml; lokal: dotnet user-secrets set \"Jwt:Key\" \"...\"). " +
        "Erzeugen z. B. mit: openssl rand -base64 48");
    return 1;
}

// Die Laengenpruefung allein reicht nicht: ein Platzhalter aus der Doku kann laenger als 32
// Zeichen sein und kaeme damit durch (`cp .env.example .env` und das Ausfuellen vergessen).
// Ein Schluessel, der im oeffentlichen Repo steht, ist kein Geheimnis - mit ihm signiert jeder
// gueltige Tokens fuer beliebige UserIds. Deshalb hier eine ausdrueckliche Sperrliste; wer einen
// dieser Werte eintraegt, hat garantiert nicht selbst gewuerfelt.
string[] forbiddenJwtKeys =
[
    "hier-einen-eigenen-zufallswert-eintragen-mindestens-32-zeichen",
    "change-me-change-me-change-me-change-me",
    "super-secret-key-super-secret-key-1234"
];

if (forbiddenJwtKeys.Contains(jwtKey.Trim(), StringComparer.OrdinalIgnoreCase))
{
    Console.Error.WriteLine(
        "FATAL: Jwt:Key ist der Platzhalter aus der Dokumentation und steht damit im oeffentlichen " +
        "Repository - jeder koennte Tokens fuer fremde Konten signieren. Trage in der .env einen " +
        "eigenen Zufallswert ein, z. B. erzeugt mit: openssl rand -base64 48");
    return 1;
}

if (string.IsNullOrWhiteSpace(jwtIssuer) || string.IsNullOrWhiteSpace(jwtAudience))
{
    Console.Error.WriteLine(
        "FATAL: Jwt:Issuer und Jwt:Audience muessen gesetzt sein (NUTRITRACK_Jwt__Issuer / " +
        "NUTRITRACK_Jwt__Audience). Die Token-Validierung prueft beide Werte; leer gelassen " +
        "wuerde jedes fremd signierte Token mit passendem Key akzeptiert.");
    return 1;
}

builder.Services.AddOpenApi();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<AiMealAssistant>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<AiRateLimiter>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<OpenFoodFactsThrottle>();

builder.Services.AddHttpClient<OpenFoodFactsService>(client =>
{
    // OpenFoodFacts verlangt einen User-Agent der Form "AppName/Version (ContactEmail)" und
    // behaelt sich vor, Aufrufer ohne erkennbare Kennung als Bot zu behandeln
    // (openfoodfacts.github.io/openfoodfacts-server/api/). Die Adresse steht bewusst in der
    // Konfiguration statt im Code: sie gehoert dem Betreiber dieser Instanz, nicht dem Projekt,
    // und sie hat in einem oeffentlichen Repo nichts verloren.
    var contact = builder.Configuration["OpenFoodFacts:ContactEmail"];
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        string.IsNullOrWhiteSpace(contact) ? "NutriTrack/1.0" : $"NutriTrack/1.0 ({contact})");
    // OpenFoodFacts ist ein fremder Dienst und der einzige Weg, eine Mahlzeit anzulegen.
    // Der HttpClient-Default von 100 s bedeutet: haengt der Dienst, steht die Suche im
    // Frontend anderthalb Minuten ohne Rueckmeldung. Lieber frueh scheitern.
    client.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddHttpClient<GeminiService>(client =>
{
    // Ohne Deckel wartet der Nutzer im Zweifel 100 Sekunden auf eine Suche, die schon tot ist.
    // 15 s waren zu knapp: der erste echte Aufruf am 2026-09-12 lief in den Deckel, weil das
    // Modell ausgiebig "nachdachte". Mit thinking_level=low ist das entschaerft, aber die
    // Antwortzeit schwankt weiterhin - ein erfolgreicher Aufruf nach 20 s ist dem Nutzer lieber
    // als ein Abbruch nach 15. Auch 25 s reichten im Betrieb nicht: laengere Eingaben und
    // Lastspitzen bei Google liefen weiter in den Deckel ("Gemini hat nicht rechtzeitig
    // geantwortet"). 45 s ist die Obergrenze dessen, was mit einer Oberflaeche, die "Denkt
    // nach..." zeigt, noch zumutbar ist - darueber gehoert die Erfassung in den Hintergrund.
    // Ueber Gemini__TimeoutSeconds (docker-compose.yml) ohne Neubau nachstellbar.
    var seconds = builder.Configuration.GetValue("Gemini:TimeoutSeconds", 45);
    client.Timeout = TimeSpan.FromSeconds(seconds);
});

// decimal ist unter SQLite nicht nativ; die Spalten sind in den Entity-Konfigurationen
// bewusst als TEXT deklariert (Begruendung in SqliteConventions). Eine ConfigureWarnings-
// Unterdrueckung ist dafuer nicht noetig: die frueheren Provider-Versionen warnten hier bei
// jedem Modellaufbau, EF 10 kennt SqliteEventId.DecimalTypeDefaultWarning nicht mehr.
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));

builder.Services.AddIdentity<IdentityUser, IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddDefaultTokenProviders();

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtIssuer,
        ValidAudience = jwtAudience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
    };

    // Signatur, Aussteller und Ablauf sagen nur, dass das Token echt und noch nicht abgelaufen
    // ist - nicht, ob es noch gelten SOLL. Ohne diesen Abgleich waere ein einmal abgegriffenes
    // Token bis zum Ablauf brauchbar und der Betrieb haette keinen Handgriff dagegen ausser
    // Jwt:Key zu tauschen (was alle Anmeldungen aller Nutzer kippt). Identity prueft den
    // SecurityStamp von sich aus nur im Cookie-Handler (SecurityStampValidator), nicht im
    // JwtBearer-Handler - hier steht deshalb die Handarbeit.
    // Kosten: ein Primaerschluessel-Lesezugriff pro authentifiziertem Request. Bei SQLite auf
    // lokaler SSD ist das ein Bruchteil dessen, was die eigentliche Abfrage danach kostet.
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = async context =>
        {
            var userId = context.Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var tokenStamp = context.Principal?.FindFirst(TokenService.SecurityStampClaimType)?.Value;

            // Tokens aus der Zeit vor dieser Pruefung tragen den Claim nicht. Sie werden
            // abgelehnt statt durchgewunken - sonst waere die Sperre mit einem alten Token
            // einfach zu umgehen. Der Nutzer meldet sich einmalig neu an.
            if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(tokenStamp))
            {
                context.Fail("Token ohne Sicherheitsstempel.");
                return;
            }

            var userManager = context.HttpContext.RequestServices
                .GetRequiredService<UserManager<IdentityUser>>();

            var user = await userManager.FindByIdAsync(userId);

            // Nutzer geloescht, oder der Stempel wurde seit dem Ausstellen weitergedreht
            // (Logout, Passwortwechsel): Token ist entwertet.
            if (user is null ||
                !string.Equals(await userManager.GetSecurityStampAsync(user), tokenStamp, StringComparison.Ordinal))
            {
                context.Fail("Token wurde entwertet.");
            }
        }
    };
});

builder.Services.AddAuthorization();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        // Im Container liefert dieselbe Anwendung das Vite-Bundle aus wwwroot aus, dann ist
        // jeder Aufruf Same-Origin und die Liste bleibt leer. Nur beim lokalen Entwickeln
        // laeuft Vite auf einem eigenen Port (appsettings.Development.json).
        policy.WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [])
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

var app = builder.Build();

// ---------------------------------------------------------------------------------------
// Schema-Abgleich: genau einmal, synchron, vor dem ersten Request
// ---------------------------------------------------------------------------------------
// Bewusst Migrate() und nicht MigrateAsync() in einem HostedService: SQLite hat einen einzigen
// Schreiber, zwei parallel laufende Migrationen sperren sich gegenseitig aus und hinterlassen
// eine halb angelegte __EFMigrationsHistory. Dieser Block ist die einzige Stelle im Repo, die
// das Schema anfasst.
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    try
    {
        // Ohne Migrationen im Assembly legt Migrate() nur __EFMigrationsHistory an. Die App
        // liefe dann an, /api/health waere gruen, und erst der erste Login schluge mit
        // "no such table: AspNetUsers" fehl - das sieht wie ein Auth-Bug aus statt wie ein
        // fehlendes Schema. Deshalb hier hart abbrechen.
        if (!db.Database.GetMigrations().Any())
        {
            logger.LogCritical(
                "Keine EF-Migrationen im Assembly NutriTrack.Infrastructure gefunden. Das Schema " +
                "kann nicht angelegt werden. Erzeugen mit: dotnet ef migrations add Initial " +
                "-p src/NutriTrack.Infrastructure -s src/NutriTrack.Infrastructure");
            return 1;
        }

        var pending = db.Database.GetPendingMigrations().ToList();
        if (pending.Count > 0)
            logger.LogInformation("Wende {Count} Migration(en) an: {Migrations}", pending.Count, string.Join(", ", pending));

        db.Database.Migrate();

        // WAL wird in der Datei selbst vermerkt, das Setzen ist also idempotent und ueberlebt
        // Neustarts. Ohne WAL blockiert jeder Leser den Schreiber - bei einem Single-File-
        // Backend der haeufigste Grund fuer "database is locked".
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");

        logger.LogInformation("Datenbank bereit: {DatabasePath}", databasePath);
    }
    catch (Exception ex)
    {
        logger.LogCritical(ex,
            "Datenbank-Migration fehlgeschlagen fuer {DatabasePath}. Haeufigste Ursache: das " +
            "Datenverzeichnis gehoert nicht dem Container-Nutzer (SQLITE_READONLY) oder es liegt " +
            "eine aeltere Datei mit fremdem Schema darin. Die Anwendung wird beendet.",
            databasePath);
        return 1;
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// HTTPS-Redirect nur beim lokalen Entwickeln. Im Container lauscht Kestrel ausschliesslich auf
// http://+:8080, es gibt weder Zertifikat noch HTTPS-Port: die Middleware faende kein
// Redirect-Ziel, loggte bei jedem Request eine Warnung und antwortete im schlechtesten Fall
// mit 307 auf einen Port, den niemand bedient. TLS terminiert bei Bedarf Caddy im
// docker-compose.https.yml-Overlay, nicht diese Anwendung.
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseCors("AllowFrontend");

// Ein einziges Options-Objekt fuer UseStaticFiles UND MapFallbackToFile - sonst bekaeme das
// ueber den Fallback ausgelieferte index.html keine Cache-Header und der Browser serviert
// nach einem Deploy das alte Bundle gegen die neue API.
var spaFiles = new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        var headers = ctx.Context.Response.Headers;
        if (ctx.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            headers.CacheControl = "no-cache";
        else if (ctx.Context.Request.Path.StartsWithSegments("/assets"))
            // Vite haengt an jeden Dateinamen unter /assets einen Inhalts-Hash; ein geaenderter
            // Inhalt bekommt also einen neuen Namen und darf deshalb ewig gecacht werden.
            headers.CacheControl = "public, max-age=31536000, immutable";
    }
};

app.UseDefaultFiles();
app.UseStaticFiles(spaFiles);

app.UseAuthentication();
app.UseAuthorization();

// Anonym und ohne Abfrage der Nutzdaten: der Docker-Healthcheck laeuft alle 30 s. CanConnect
// oeffnet die Datei und prueft damit genau das, was im Betrieb tatsaechlich kaputtgeht
// (Mount weg, Rechte falsch), ohne auf ein Schreib-Lock zu warten - eine echte Abfrage koennte
// haengen und den Container ueber autoheal in eine Restartschleife schicken.
// GET *und* HEAD: Endpoint-Routing registriert bei MapGet ausschliesslich GET, und ein HEAD auf
// /api/health liefe in den Catch-all `/api/{*rest}` weiter unten (404). Monitoring-Werkzeuge
// pruefen aber gern mit HEAD (`wget --spider`, curl -I) - fuer die waere der gesunde Dienst dann
// dauerhaft "kaputt". Kestrel verwirft den Body einer HEAD-Antwort selbst, der Handler bleibt
// derselbe.
app.MapMethods("/api/health", ["GET", "HEAD"], async (AppDbContext db, CancellationToken cancellationToken) =>
{
    var databaseReachable = await db.Database.CanConnectAsync(cancellationToken);

    return databaseReachable
        ? Results.Ok(new { Status = "ok", Database = "ok" })
        : Results.Json(new { Status = "degraded", Database = "unreachable" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
}).AllowAnonymous().WithTags("System");

app.MapAuthEndpoints();
app.MapFoodEndpoints();
app.MapMealEndpoints();
app.MapGoalsEndpoints();
app.MapAiEndpoints();

// Ohne Kontaktadresse laeuft NutriTrack weiter, aber OpenFoodFacts darf uns dann jederzeit als
// anonymen Bot einstufen. Das ist eine Betriebsentscheidung, kein Programmfehler - deshalb ein
// Hinweis im Log und kein Startabbruch.
if (string.IsNullOrWhiteSpace(app.Configuration["OpenFoodFacts:ContactEmail"]))
    app.Logger.LogWarning(
        "OpenFoodFacts:ContactEmail ist nicht gesetzt. OpenFoodFacts verlangt einen User-Agent " +
        "der Form \"NutriTrack/1.0 (adresse@example.com)\" und kann Aufrufe ohne Kennung sperren.");

// Unbekannte /api-Pfade muessen 404 bleiben. Der Catch-all steht in der Routen-Rangfolge unter
// jedem konkreten Endpunkt (Literale schlagen Catch-all), greift aber vor dem SPA-Fallback -
// sonst bekaeme ein Tippfehler in der API-URL das index.html mit Status 200 zurueck und das
// Frontend liefe in einen JSON-Parsefehler statt in eine saubere Fehlermeldung.
app.Map("/api/{*rest}", () => Results.NotFound());

// Muss die letzte Route bleiben: Deep-Links wie /goals oder /diary/2026-09-12 sind
// Client-Routen und existieren serverseitig nicht.
app.MapFallbackToFile("index.html", spaFiles);

app.Run();

return 0;
