namespace NutriTrack.Api.Tests.Infrastructure;

/// <summary>
/// Bewusst eigene DTOs statt der Produktiv-Contracts: die Tests sollen den verabredeten
/// Wire-Vertrag pruefen und nicht automatisch jeder Umbenennung im Produktivcode folgen.
/// </summary>
public sealed record GoalsResponseDto(
    Guid Id,
    decimal CalorieGoal,
    decimal ProteinGoal,
    decimal CarbohydrateGoal,
    decimal FatGoal,
    DateTime UpdatedAt);

public sealed record UpsertGoalsRequestDto(
    decimal CalorieGoal,
    decimal ProteinGoal,
    decimal CarbohydrateGoal,
    decimal FatGoal);

public sealed record AuthResponseDto(string Token, DateTime ExpiresAt, string Email);

public sealed record MeResponseDto(string UserId, string Email);
