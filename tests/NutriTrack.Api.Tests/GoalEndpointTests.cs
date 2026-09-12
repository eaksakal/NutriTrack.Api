using System.Net;
using System.Net.Http.Json;
using NutriTrack.Api.Tests.Infrastructure;

namespace NutriTrack.Api.Tests;

public class GoalEndpointTests(NutriTrackApiFactory factory) : IClassFixture<NutriTrackApiFactory>
{
    [Fact]
    public async Task Get_WithoutGoals_ReturnsNotFound()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.GetAsync("/api/goals");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Put_CreatesGoalsAndGetReturnsThem()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var put = await client.PutAsJsonAsync("/api/goals",
            new UpsertGoalsRequestDto(2200m, 150m, 220m, 70m));

        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var created = await put.Content.ReadFromJsonAsync<GoalsResponseDto>();
        Assert.NotNull(created);
        Assert.NotEqual(Guid.Empty, created!.Id);
        Assert.Equal(2200m, created.CalorieGoal);
        Assert.Equal(150m, created.ProteinGoal);
        Assert.Equal(220m, created.CarbohydrateGoal);
        Assert.Equal(70m, created.FatGoal);

        var get = await client.GetAsync("/api/goals");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var fetched = await get.Content.ReadFromJsonAsync<GoalsResponseDto>();
        Assert.NotNull(fetched);
        Assert.Equal(created.Id, fetched!.Id);
        Assert.Equal(2200m, fetched.CalorieGoal);
        Assert.Equal(150m, fetched.ProteinGoal);
        Assert.Equal(220m, fetched.CarbohydrateGoal);
        Assert.Equal(70m, fetched.FatGoal);
    }

    [Fact]
    public async Task Put_UsesCamelCaseOnTheWire()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.PutAsJsonAsync("/api/goals",
            new UpsertGoalsRequestDto(1800m, 120m, 180m, 60m));
        var json = await response.ReadJsonAsync();

        Assert.Equal(1800m, json.GetProperty("calorieGoal").GetDecimal());
        Assert.Equal(120m, json.GetProperty("proteinGoal").GetDecimal());
        Assert.Equal(180m, json.GetProperty("carbohydrateGoal").GetDecimal());
        Assert.Equal(60m, json.GetProperty("fatGoal").GetDecimal());
        Assert.True(json.TryGetProperty("id", out _));
        Assert.True(json.TryGetProperty("updatedAt", out _));
    }

    [Fact]
    public async Task Put_Twice_UpdatesInsteadOfCreatingSecondRecord()
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var first = await client.PutAsJsonAsync("/api/goals",
            new UpsertGoalsRequestDto(2000m, 140m, 200m, 65m));
        var initial = await first.Content.ReadFromJsonAsync<GoalsResponseDto>();

        var second = await client.PutAsJsonAsync("/api/goals",
            new UpsertGoalsRequestDto(2500m, 180m, 260m, 80m));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var updated = await second.Content.ReadFromJsonAsync<GoalsResponseDto>();
        Assert.NotNull(updated);

        // Gleiche Id => ein Datensatz pro User, kein Duplikat
        Assert.Equal(initial!.Id, updated!.Id);
        Assert.Equal(2500m, updated.CalorieGoal);
        Assert.Equal(180m, updated.ProteinGoal);
        Assert.Equal(260m, updated.CarbohydrateGoal);
        Assert.Equal(80m, updated.FatGoal);

        var fetched = await client.GetFromJsonAsync<GoalsResponseDto>("/api/goals");
        Assert.Equal(initial.Id, fetched!.Id);
        Assert.Equal(2500m, fetched.CalorieGoal);
    }

    [Theory]
    [InlineData(0, 150, 220, 70)]
    [InlineData(-1, 150, 220, 70)]
    [InlineData(2200, 0, 220, 70)]
    [InlineData(2200, 150, 0, 70)]
    [InlineData(2200, 150, 220, 0)]
    [InlineData(2200, 150, 220, -70)]
    public async Task Put_WithNonPositiveValues_ReturnsBadRequest(
        decimal calories, decimal protein, decimal carbohydrates, decimal fat)
    {
        var (client, _, _) = await factory.CreateUserAsync();

        var response = await client.PutAsJsonAsync("/api/goals",
            new UpsertGoalsRequestDto(calories, protein, carbohydrates, fat));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Goals_AreIsolatedPerUser()
    {
        var (alice, _, _) = await factory.CreateUserAsync();
        var (bob, _, _) = await factory.CreateUserAsync();

        await alice.PutAsJsonAsync("/api/goals", new UpsertGoalsRequestDto(2400m, 160m, 240m, 75m));

        var bobBefore = await bob.GetAsync("/api/goals");
        Assert.Equal(HttpStatusCode.NotFound, bobBefore.StatusCode);

        await bob.PutAsJsonAsync("/api/goals", new UpsertGoalsRequestDto(1600m, 100m, 150m, 50m));

        var aliceGoals = await alice.GetFromJsonAsync<GoalsResponseDto>("/api/goals");
        var bobGoals = await bob.GetFromJsonAsync<GoalsResponseDto>("/api/goals");

        Assert.Equal(2400m, aliceGoals!.CalorieGoal);
        Assert.Equal(1600m, bobGoals!.CalorieGoal);
        Assert.NotEqual(aliceGoals.Id, bobGoals.Id);
    }

    [Fact]
    public async Task Put_Concurrently_UpsertsOnceInsteadOfFailing()
    {
        // Zwei Browsertabs oder ein Doppelklick: mehrere PUTs desselben Nutzers ueberholen sich.
        // Das ungeschuetzte Read-dann-Insert lief dabei gegen IX_UserGoals_UserId und kam als 500
        // heraus. Richtig ist: der Verlierer des Wettlaufs aktualisiert den vorhandenen Zielsatz.
        var (client, _, _) = await factory.CreateUserAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            client.PutAsJsonAsync("/api/goals", new UpsertGoalsRequestDto(2100m, 145m, 210m, 68m))));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));

        var bodies = await Task.WhenAll(
            responses.Select(response => response.Content.ReadFromJsonAsync<GoalsResponseDto>()));

        // Alle Antworten zeigen auf denselben Datensatz - sonst haette der Nutzer zwei Zielsaetze.
        var ids = bodies.Select(body => body!.Id).Distinct().ToList();
        Assert.Single(ids);

        var fetched = await client.GetFromJsonAsync<GoalsResponseDto>("/api/goals");
        Assert.Equal(ids[0], fetched!.Id);
        Assert.Equal(2100m, fetched.CalorieGoal);
        Assert.Equal(145m, fetched.ProteinGoal);
        Assert.Equal(210m, fetched.CarbohydrateGoal);
        Assert.Equal(68m, fetched.FatGoal);
    }

    [Fact]
    public async Task Put_WithoutToken_ReturnsUnauthorized()
    {
        var client = factory.CreateClient();

        var response = await client.PutAsJsonAsync("/api/goals",
            new UpsertGoalsRequestDto(2000m, 140m, 200m, 65m));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
