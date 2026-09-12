namespace NutriTrack.Api.Tests.Infrastructure;

/// <summary>
/// Der Content-Root muss auf das API-Projekt zeigen, damit appsettings.json gefunden wird.
/// Er wird hier explizit aus dem Testausgabeverzeichnis hergeleitet statt sich auf das von
/// Microsoft.AspNetCore.Mvc.Testing generierte Manifest zu verlassen.
/// </summary>
public static class ApiContentRoot
{
    public static string Path { get; } = Resolve();

    private static string Resolve()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = System.IO.Path.Combine(directory.FullName, "src", "NutriTrack.Api");
            if (Directory.Exists(candidate) && File.Exists(System.IO.Path.Combine(candidate, "NutriTrack.Api.csproj")))
                return candidate;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Das API-Projektverzeichnis wurde ausgehend von '{AppContext.BaseDirectory}' nicht gefunden.");
    }
}
