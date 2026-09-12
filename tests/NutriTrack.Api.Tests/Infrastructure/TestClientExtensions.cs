using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace NutriTrack.Api.Tests.Infrastructure;

public static class TestClientExtensions
{
    /// <summary>
    /// Erfuellt sowohl die Identity-Default-Regeln (Gross/Klein/Ziffer/Sonderzeichen) als auch
    /// die MinLength(8) des RegisterRequest.
    /// </summary>
    public const string DefaultPassword = "Test1234!pass";

    public static string NewEmail(string prefix = "user") => $"{prefix}-{Guid.NewGuid():N}@nutritrack.test";

    public static async Task<(HttpClient Client, string Email, string UserId)> CreateUserAsync(
        this NutriTrackApiFactory factory,
        string? email = null,
        string password = DefaultPassword)
    {
        email ??= NewEmail();

        var client = factory.CreateClient();
        var registerResponse = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password,
            confirmPassword = password
        });

        Assert.Equal(System.Net.HttpStatusCode.Created, registerResponse.StatusCode);

        var auth = await registerResponse.Content.ReadFromJsonAsync<AuthResponseDto>();
        Assert.NotNull(auth);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Token);

        var me = await client.GetFromJsonAsync<MeResponseDto>("/api/auth/me");
        Assert.NotNull(me);

        return (client, email, me!.UserId);
    }

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response)
    {
        var raw = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }
}
