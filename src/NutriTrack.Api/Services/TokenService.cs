using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using NutriTrack.Api.Contracts.Auth;

namespace NutriTrack.Api.Services;

public class TokenService(IConfiguration configuration)
{
    /// <summary>
    /// Traegt den Identity-SecurityStamp des Nutzers. Er ist der einzige Hebel, mit dem ein
    /// bereits ausgestelltes Token vorzeitig entwertet werden kann: JWTs sind zustandslos, es
    /// gibt weder Sperrliste noch Sitzung. Program.cs prueft den Wert bei JEDEM Request gegen
    /// die Datenbank (JwtBearerEvents.OnTokenValidated); dreht Identity den Stempel weiter
    /// (Logout, Passwortwechsel, UserManager.UpdateSecurityStampAsync), sind alle aelteren
    /// Tokens dieses Nutzers sofort ungueltig.
    /// </summary>
    public const string SecurityStampClaimType = "security_stamp";

    /// <summary>
    /// Gueltigkeitsdauer in Stunden. Frueher fest 7 Tage - ein abgegriffenes Token (fremder
    /// Browser, XSS, localStorage) war damit eine Woche lang brauchbar. Ueber
    /// NUTRITRACK_Jwt__TokenLifetimeHours anpassbar, falls der Betrieb es anders braucht.
    /// </summary>
    private const int DefaultTokenLifetimeHours = 12;

    public AuthResponse GenerateToken(IdentityUser user)
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(configuration["Jwt:Key"]!));

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Email, user.Email!),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(SecurityStampClaimType, user.SecurityStamp ?? string.Empty)
        };

        var lifetimeHours = int.TryParse(configuration["Jwt:TokenLifetimeHours"], out var configured) && configured > 0
            ? configured
            : DefaultTokenLifetimeHours;

        var expiresAt = DateTime.UtcNow.AddHours(lifetimeHours);

        var token = new JwtSecurityToken(
            issuer: configuration["Jwt:Issuer"],
            audience: configuration["Jwt:Audience"],
            claims: claims,
            expires: expiresAt,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new AuthResponse
        {
            Token = new JwtSecurityTokenHandler().WriteToken(token),
            ExpiresAt = expiresAt,
            Email = user.Email!
        };
    }
}
