using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ClinicDesk.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;

namespace ClinicDesk.Api.Infrastructure;

public static class Claims
{
    public const string Subject = "sub";
    public const string Tenant = "tenant";
    public const string Role = "role";
    public const string Name = "name";
    public const string Email = "email";
}

public static class Policies
{
    /// <summary>Owners and admins: billing, staff, practitioners, audit.</summary>
    public const string Admin = "admin";
    /// <summary>Anyone who can see patients and the calendar.</summary>
    public const string Staff = "staff";
    /// <summary>Front desk work: booking, check-in, invoices and payments.</summary>
    public const string FrontDesk = "front-desk";
    /// <summary>Clinical work: completing visits.</summary>
    public const string Clinical = "clinical";

    public static void Register(Microsoft.AspNetCore.Authorization.AuthorizationBuilder b) => b
        .AddPolicy(Admin, p => p.RequireClaim(Claims.Tenant).RequireRole(nameof(StaffRole.Owner), nameof(StaffRole.Admin)))
        .AddPolicy(Staff, p => p.RequireClaim(Claims.Tenant))
        .AddPolicy(FrontDesk, p => p.RequireClaim(Claims.Tenant).RequireRole(nameof(StaffRole.Owner), nameof(StaffRole.Admin), nameof(StaffRole.Receptionist)))
        .AddPolicy(Clinical, p => p.RequireClaim(Claims.Tenant).RequireRole(nameof(StaffRole.Owner), nameof(StaffRole.Admin), nameof(StaffRole.Practitioner)));
}

public class JwtOptions
{
    public string Issuer { get; set; } = "clinicdesk";
    public string Audience { get; set; } = "clinicdesk";
    public string Key { get; set; } = "development-only-signing-key-change-me-0123456789";
    public int LifetimeMinutes { get; set; } = 480;

    public SymmetricSecurityKey SigningKey() => new(Encoding.UTF8.GetBytes(Key));

    public TokenValidationParameters Validation() => new()
    {
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = SigningKey(),
        NameClaimType = Claims.Name,
        RoleClaimType = Claims.Role,
        ClockSkew = TimeSpan.FromSeconds(30),
    };
}

public record SessionDto(string Token, Guid UserId, string FullName, string Email, string Role, Guid TenantId, string Clinic, string ClinicSlug);

public class TokenService(JwtOptions options, TimeProvider clock)
{
    public SessionDto Issue(StaffMember user, Tenant tenant)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var token = new JwtSecurityToken(
            options.Issuer, options.Audience,
            new[]
            {
                new Claim(Claims.Subject, user.Id.ToString()),
                new Claim(Claims.Tenant, tenant.Id.ToString()),
                new Claim(Claims.Role, user.Role.ToString()),
                new Claim(Claims.Name, user.FullName),
                new Claim(Claims.Email, user.Email),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            },
            notBefore: now, expires: now.AddMinutes(options.LifetimeMinutes),
            signingCredentials: new SigningCredentials(options.SigningKey(), SecurityAlgorithms.HmacSha256));
        return new SessionDto(new JwtSecurityTokenHandler().WriteToken(token), user.Id, user.FullName, user.Email,
            user.Role.ToString(), tenant.Id, tenant.Name, tenant.Slug);
    }
}

public class Passwords
{
    private readonly PasswordHasher<StaffMember> _hasher = new();

    public void Set(StaffMember user, string password) => user.PasswordHash = _hasher.HashPassword(user, password);

    public bool Verify(StaffMember user, string password) =>
        !string.IsNullOrEmpty(user.PasswordHash)
        && _hasher.VerifyHashedPassword(user, user.PasswordHash, password) != PasswordVerificationResult.Failed;
}
