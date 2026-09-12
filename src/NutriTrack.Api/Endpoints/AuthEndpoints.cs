using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using NutriTrack.Api.Contracts.Auth;
using NutriTrack.Api.Services;

namespace NutriTrack.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/register", async (
            RegisterRequest request,
            UserManager<IdentityUser> userManager,
            TokenService tokenService) =>
        {
            // Minimal APIs werten die DataAnnotations des RegisterRequest nicht von selbst aus.
            // Ohne diese ausdrueckliche Pruefung entsteht ein Konto mit ungueltiger E-Mail oder mit
            // ConfirmPassword != Password; von den Annotationen greift faktisch nur MinLength(8),
            // und auch das nur zufaellig ueber die Passwortpolicy von Identity.
            // Die Fehlerform ist bewusst dieselbe wie bei den Identity-Fehlern unten ("errors" als
            // Array), damit das Frontend nur einen Fall auswerten muss.
            var validationResults = new List<ValidationResult>();
            if (!Validator.TryValidateObject(
                    request, new ValidationContext(request), validationResults, validateAllProperties: true))
                return Results.BadRequest(new { Errors = validationResults.Select(r => r.ErrorMessage) });

            var existingUser = await userManager.FindByEmailAsync(request.Email);
            if (existingUser is not null)
                return Results.Conflict(new { Error = "A user with this email already exists." });

            var user = new IdentityUser
            {
                UserName = request.Email,
                Email = request.Email
            };

            var result = await userManager.CreateAsync(user, request.Password);
            if (!result.Succeeded)
                return Results.BadRequest(new { Errors = result.Errors.Select(e => e.Description) });

            var response = tokenService.GenerateToken(user);
            return Results.Created($"/api/auth/me", response);
        });

        group.MapPost("/login", async (
            LoginRequest request,
            UserManager<IdentityUser> userManager,
            SignInManager<IdentityUser> signInManager,
            TokenService tokenService) =>
        {
            var user = await userManager.FindByEmailAsync(request.Email);
            if (user is null)
                return Results.Unauthorized();

            var result = await signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
            if (!result.Succeeded)
                return Results.Unauthorized();

            var response = tokenService.GenerateToken(user);
            return Results.Ok(response);
        });

        // Ohne diesen Endpunkt gibt es serverseitig keinen einzigen Handgriff, um ein
        // ausgestelltes Token zu entwerten: Abmelden im Browser loescht nur den localStorage,
        // das Token selbst bliebe bis zum Ablauf gueltig. UpdateSecurityStampAsync dreht den
        // Stempel weiter, den Program.cs bei jedem Request gegen das Token prueft - damit sind
        // ALLE Tokens dieses Nutzers sofort ungueltig, auch die auf anderen Geraeten. Genau das
        // will man, wenn man abmeldet, weil das Token in fremde Haende geraten sein koennte.
        group.MapPost("/logout", async (ClaimsPrincipal principal, UserManager<IdentityUser> userManager) =>
        {
            var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userId is null)
                return Results.Unauthorized();

            var user = await userManager.FindByIdAsync(userId);
            if (user is null)
                return Results.NoContent();

            await userManager.UpdateSecurityStampAsync(user);

            return Results.NoContent();
        }).RequireAuthorization();

        group.MapGet("/me", (ClaimsPrincipal user) =>
        {
            var userId = user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            var email = user.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;

            return Results.Ok(new { UserId = userId, Email = email });
        }).RequireAuthorization();
    }
}
