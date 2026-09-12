using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NutriTrack.Api.Tests.Infrastructure;

namespace NutriTrack.Api.Tests;

public class AuthEndpointTests(NutriTrackApiFactory factory) : IClassFixture<NutriTrackApiFactory>
{
    [Fact]
    public async Task Register_WithValidData_ReturnsCreatedAndToken()
    {
        var client = factory.CreateClient();
        var email = TestClientExtensions.NewEmail();

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = TestClientExtensions.DefaultPassword,
            confirmPassword = TestClientExtensions.DefaultPassword
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>();
        Assert.NotNull(auth);
        Assert.Equal(email, auth!.Email);
        Assert.False(string.IsNullOrWhiteSpace(auth.Token));
        Assert.True(auth.ExpiresAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task Register_WithExistingEmail_ReturnsConflict()
    {
        var email = TestClientExtensions.NewEmail();
        await factory.CreateUserAsync(email);

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = TestClientExtensions.DefaultPassword,
            confirmPassword = TestClientExtensions.DefaultPassword
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Register_WithWeakPassword_ReturnsBadRequest()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = TestClientExtensions.NewEmail(),
            password = "abc",
            confirmPassword = "abc"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing-at-sign.nutritrack.test")]
    [InlineData("")]
    public async Task Register_WithInvalidEmail_ReturnsBadRequest(string email)
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = TestClientExtensions.DefaultPassword,
            confirmPassword = TestClientExtensions.DefaultPassword
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Register_WithMismatchedConfirmPassword_ReturnsBadRequestAndCreatesNoAccount()
    {
        var client = factory.CreateClient();
        var email = TestClientExtensions.NewEmail();

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email,
            password = TestClientExtensions.DefaultPassword,
            confirmPassword = "Other1234!pass"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Kein halb angelegtes Konto: mit dem ersten Passwort darf man sich nicht anmelden koennen.
        var login = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email,
            password = TestClientExtensions.DefaultPassword
        });

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task Register_ValidationError_UsesSameErrorsArrayAsIdentityErrors()
    {
        // Das Frontend wertet genau eine Fehlerform aus; die Eingabepruefung muss deshalb
        // dasselbe "errors"-Array liefern wie die Identity-Fehler (z. B. schwaches Passwort).
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/register", new
        {
            email = "not-an-email",
            password = TestClientExtensions.DefaultPassword,
            confirmPassword = TestClientExtensions.DefaultPassword
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var json = await response.ReadJsonAsync();
        Assert.True(json.TryGetProperty("errors", out var errors));
        Assert.Equal(JsonValueKind.Array, errors.ValueKind);
        Assert.NotEmpty(errors.EnumerateArray());
    }

    [Fact]
    public async Task Login_WithCorrectCredentials_ReturnsToken()
    {
        var email = TestClientExtensions.NewEmail();
        await factory.CreateUserAsync(email);

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email,
            password = TestClientExtensions.DefaultPassword
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>();
        Assert.NotNull(auth);
        Assert.Equal(email, auth!.Email);
        Assert.False(string.IsNullOrWhiteSpace(auth.Token));
    }

    [Fact]
    public async Task Login_WithWrongPassword_ReturnsUnauthorized()
    {
        var email = TestClientExtensions.NewEmail();
        await factory.CreateUserAsync(email);

        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email,
            password = "Wrong1234!pass"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Login_WithUnknownEmail_ReturnsUnauthorized()
    {
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            email = TestClientExtensions.NewEmail("ghost"),
            password = TestClientExtensions.DefaultPassword
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithToken_ReturnsUserIdAndEmail()
    {
        var email = TestClientExtensions.NewEmail();
        var (client, _, userId) = await factory.CreateUserAsync(email);

        var response = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.ReadJsonAsync();
        Assert.Equal(email, json.GetProperty("email").GetString());
        Assert.Equal(userId, json.GetProperty("userId").GetString());
        Assert.False(string.IsNullOrWhiteSpace(userId));
    }

    [Fact]
    public async Task Me_WithoutToken_ReturnsUnauthorized()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_WithGarbageToken_ReturnsUnauthorized()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/meals")]
    [InlineData("/api/meals/summary")]
    [InlineData("/api/goals")]
    [InlineData("/api/food/search?query=oats")]
    public async Task ProtectedEndpoints_WithoutToken_ReturnUnauthorized(string url)
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_IsAnonymousAndReportsDatabase()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var json = await response.ReadJsonAsync();
        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.Equal("ok", json.GetProperty("database").GetString());
    }

    [Fact]
    public async Task Logout_InvalidatesIssuedToken()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        var logout = await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        // Dasselbe Token, derselbe Header - es ist jetzt serverseitig entwertet.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/meals")).StatusCode);
    }

    [Fact]
    public async Task Logout_DoesNotInvalidateTokensOfOtherUsers()
    {
        var (client, _, _) = await factory.CreateUserAsync();
        var (other, _, _) = await factory.CreateUserAsync();

        await client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Login_AfterLogout_ReturnsUsableToken()
    {
        var email = TestClientExtensions.NewEmail();
        var (client, _, _) = await factory.CreateUserAsync(email);
        await client.PostAsync("/api/auth/logout", null);

        var fresh = factory.CreateClient();
        var response = await fresh.PostAsJsonAsync("/api/auth/login", new
        {
            email,
            password = TestClientExtensions.DefaultPassword
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var auth = await response.Content.ReadFromJsonAsync<AuthResponseDto>();
        fresh.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Token);

        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Health_AnswersHeadRequest()
    {
        // Der Docker-Healthcheck benutzt GET; Monitoring-Werkzeuge pruefen aber gern mit HEAD.
        // Vor der Korrektur landete HEAD im Catch-all /api/{*rest} und ergab 404.
        var client = factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/api/health"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UnknownApiPath_ReturnsNotFoundInsteadOfSpaFallback()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.GetAsync("/api/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
