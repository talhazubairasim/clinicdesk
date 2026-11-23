using ClinicDesk.Api.Infrastructure;
using ClinicDesk.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ClinicDesk.Api.Features;

public record SignupRequest(string ClinicName, string Slug, string TimeZone, string OwnerName, string Email, string Password);
public record LoginRequest(string Clinic, string Email, string Password);
public record ChangePlanRequest(Plan Plan);
public record UsageDto(int Practitioners, int MaxPractitioners, int AppointmentsThisMonth, int MaxAppointmentsPerMonth);
public record ClinicDto(Guid Id, string Name, string Slug, string TimeZone, Plan Plan, SubscriptionStatus Status,
    DateTimeOffset CurrentPeriodEnd, decimal MonthlyPrice, UsageDto Usage, IReadOnlyList<SubscriptionCharge> Charges);

public class SignupValidator : AbstractValidator<SignupRequest>
{
    public SignupValidator()
    {
        RuleFor(x => x.ClinicName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Slug).NotEmpty().Matches("^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$")
            .WithMessage("Use 3–40 lowercase letters, digits or hyphens.");
        RuleFor(x => x.TimeZone).NotEmpty();
        RuleFor(x => x.OwnerName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8)
            .Matches("[A-Z]").WithMessage("Password needs an uppercase letter.")
            .Matches("[0-9]").WithMessage("Password needs a digit.");
    }
}

public static class Clinics
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/clinics", Signup).Validated<SignupRequest>().AllowAnonymous().WithTags("Clinics");
        app.MapPost("/api/auth/login", Login).AllowAnonymous().WithTags("Clinics");

        var clinic = app.MapGroup("/api/clinic").WithTags("Clinics");
        clinic.MapGet("/", (ClinicDbContext db, TenantContext t, TimeProvider clock) => Describe(db, t.TenantId!.Value, clock))
            .RequireAuthorization(Policies.Staff);
        clinic.MapPut("/plan", ChangePlan).RequireAuthorization(Policies.Admin);
        clinic.MapGet("/audit", async (ClinicDbContext db, int? take) =>
                await db.Audit.OrderByDescending(a => a.Id).Take(Math.Clamp(take ?? 100, 1, 500)).ToListAsync())
            .RequireAuthorization(Policies.Admin);
    }

    private static async Task<IResult> Signup(SignupRequest req, ClinicDbContext db, TenantContext tenantContext,
        Passwords passwords, TokenService tokens, TimeProvider clock)
    {
        if (await db.Tenants.AnyAsync(t => t.Slug == req.Slug))
            return Results.Problem(statusCode: 409, title: "Conflict", detail: $"The address '{req.Slug}' is taken.");

        var tenant = Tenant.Create(req.ClinicName, req.Slug, req.TimeZone, clock.GetUtcNow());
        var owner = new StaffMember(req.Email, req.OwnerName, StaffRole.Owner);
        passwords.Set(owner, req.Password);
        tenantContext.Set(tenant.Id, owner.Id);
        db.Tenants.Add(tenant);
        db.Staff.Add(owner);
        await db.SaveChangesAsync();
        return Results.Created("/api/clinic", tokens.Issue(owner, tenant));
    }

    private static async Task<IResult> Login(LoginRequest req, ClinicDbContext db, Passwords passwords, TokenService tokens)
    {
        var tenant = await db.Tenants.SingleOrDefaultAsync(t => t.Slug == req.Clinic.Trim().ToLower());
        var email = req.Email.Trim().ToLowerInvariant();
        var user = tenant is null ? null : await db.Staff.IgnoreQueryFilters()
            .SingleOrDefaultAsync(s => s.TenantId == tenant.Id && s.Email == email && s.Active);
        if (tenant is null || user is null || !passwords.Verify(user, req.Password))
            return Results.Problem(statusCode: 401, title: "Invalid clinic, email or password.");
        return Results.Ok(tokens.Issue(user, tenant));
    }

    private static async Task<IResult> ChangePlan(ChangePlanRequest req, ClinicDbContext db, TenantContext t, TimeProvider clock)
    {
        var tenant = await db.Tenants.SingleAsync(x => x.Id == t.TenantId);
        var limits = PlanLimits.For(req.Plan);
        var practitioners = await db.Practitioners.CountAsync();
        if (practitioners > limits.MaxPractitioners)
            throw new DomainException($"The {req.Plan} plan allows {limits.MaxPractitioners} practitioner(s); you have {practitioners}.");
        tenant.ChangePlan(req.Plan, clock.GetUtcNow());
        await db.SaveChangesAsync();
        return Results.Ok(await Describe(db, tenant.Id, clock));
    }

    public static async Task<ClinicDto> Describe(ClinicDbContext db, Guid tenantId, TimeProvider clock)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == tenantId);
        var usage = await Usage(db, tenant, clock.GetUtcNow());
        return new ClinicDto(tenant.Id, tenant.Name, tenant.Slug, tenant.TimeZone, tenant.Plan, tenant.Status,
            tenant.CurrentPeriodEnd, tenant.Limits.MonthlyPrice, usage, tenant.Charges.OrderByDescending(c => c.PeriodStart).ToList());
    }

    public static async Task<UsageDto> Usage(ClinicDbContext db, Tenant tenant, DateTimeOffset now)
    {
        var (from, to) = MonthOf(tenant, now);
        return new UsageDto(
            await db.Practitioners.CountAsync(), tenant.Limits.MaxPractitioners,
            await db.Appointments.CountAsync(a => a.StartsAt >= from && a.StartsAt < to && a.Status != AppointmentStatus.Cancelled),
            tenant.Limits.MaxAppointmentsPerMonth);
    }

    /// <summary>The calendar month (in clinic time) containing <paramref name="instant"/>, as UTC bounds.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) MonthOf(Tenant tenant, DateTimeOffset instant)
    {
        var local = TimeZoneInfo.ConvertTime(instant, tenant.Zone);
        var first = new DateOnly(local.Year, local.Month, 1);
        return (Schedule.ToUtc(first, TimeOnly.MinValue, tenant.Zone), Schedule.ToUtc(first.AddMonths(1), TimeOnly.MinValue, tenant.Zone));
    }
}
