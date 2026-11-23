using ClinicDesk.Api.Infrastructure;
using ClinicDesk.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ClinicDesk.Api.Features;

public record StaffDto(Guid Id, string Email, string FullName, StaffRole Role, bool Active);
public record AddStaffRequest(string Email, string FullName, StaffRole Role, string Password);
public record PractitionerRequest(string Name, string Specialty, int SlotMinutes, List<WorkingHours> Hours);
public record PatientRequest(string FirstName, string LastName, DateOnly DateOfBirth, string Phone, string? Email, string? Notes);
public record Paged<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

public class AddStaffValidator : AbstractValidator<AddStaffRequest>
{
    public AddStaffValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.FullName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Role).IsInEnum().NotEqual(StaffRole.Owner).WithMessage("A clinic has exactly one owner.");
        RuleFor(x => x.Password).NotEmpty().MinimumLength(8);
    }
}

public class PractitionerValidator : AbstractValidator<PractitionerRequest>
{
    public PractitionerValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Specialty).NotEmpty().MaximumLength(120);
        RuleFor(x => x.SlotMinutes).InclusiveBetween(5, 240);
        RuleFor(x => x.Hours).NotNull();
    }
}

public class PatientValidator : AbstractValidator<PatientRequest>
{
    public PatientValidator(TimeProvider clock)
    {
        RuleFor(x => x.FirstName).NotEmpty().MaximumLength(80);
        RuleFor(x => x.LastName).NotEmpty().MaximumLength(80);
        RuleFor(x => x.Phone).NotEmpty().Matches(@"^\+?[0-9 ()-]{7,20}$").WithMessage("Enter a valid phone number.");
        RuleFor(x => x.Email).EmailAddress().When(x => !string.IsNullOrEmpty(x.Email));
        RuleFor(x => x.DateOfBirth)
            .Must(d => d <= DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime)).WithMessage("Date of birth can't be in the future.")
            .Must(d => d.Year >= 1900).WithMessage("Enter a real date of birth.");
        RuleFor(x => x.Notes).MaximumLength(2000);
    }
}

public static class People
{
    public static void Map(WebApplication app)
    {
        var staff = app.MapGroup("/api/staff").WithTags("Staff").RequireAuthorization(Policies.Admin);
        staff.MapGet("/", async (ClinicDbContext db) => await db.Staff.OrderBy(s => s.FullName)
            .Select(s => new StaffDto(s.Id, s.Email, s.FullName, s.Role, s.Active)).ToListAsync());
        staff.MapPost("/", AddStaff).Validated<AddStaffRequest>();
        staff.MapDelete("/{id:guid}", async (Guid id, ClinicDbContext db) =>
        {
            var member = await db.Staff.FindAsync(id);
            if (member is null) return Results.NotFound();
            member.Deactivate();
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        var practitioners = app.MapGroup("/api/practitioners").WithTags("Practitioners");
        practitioners.MapGet("/", async (ClinicDbContext db) => await db.Practitioners.OrderBy(p => p.Name).ToListAsync())
            .RequireAuthorization(Policies.Staff);
        practitioners.MapPost("/", AddPractitioner).Validated<PractitionerRequest>().RequireAuthorization(Policies.Admin);
        practitioners.MapPut("/{id:guid}", async (Guid id, PractitionerRequest req, ClinicDbContext db) =>
        {
            var p = await db.Practitioners.FindAsync(id);
            if (p is null) return Results.NotFound();
            p.Name = req.Name.Trim();
            p.Specialty = req.Specialty.Trim();
            p.SetSchedule(req.SlotMinutes, req.Hours);
            await db.SaveChangesAsync();
            return Results.Ok(p);
        }).Validated<PractitionerRequest>().RequireAuthorization(Policies.Admin);

        var patients = app.MapGroup("/api/patients").WithTags("Patients").RequireAuthorization(Policies.Staff);
        patients.MapGet("/", SearchPatients);
        patients.MapGet("/{id:guid}", async (Guid id, ClinicDbContext db) =>
            await db.Patients.FindAsync(id) is { } p ? Results.Ok(p) : Results.NotFound());
        patients.MapPost("/", async (PatientRequest req, ClinicDbContext db, TimeProvider clock) =>
        {
            var p = new Patient { CreatedAt = clock.GetUtcNow() };
            Apply(p, req);
            db.Patients.Add(p);
            await db.SaveChangesAsync();
            return Results.Created($"/api/patients/{p.Id}", p);
        }).Validated<PatientRequest>();
        patients.MapPut("/{id:guid}", async (Guid id, PatientRequest req, ClinicDbContext db) =>
        {
            var p = await db.Patients.FindAsync(id);
            if (p is null) return Results.NotFound();
            Apply(p, req);
            await db.SaveChangesAsync();
            return Results.Ok(p);
        }).Validated<PatientRequest>();
    }

    private static async Task<IResult> AddStaff(AddStaffRequest req, ClinicDbContext db, Passwords passwords)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        if (await db.Staff.AnyAsync(s => s.Email == email))
            return Results.Problem(statusCode: 409, title: "Conflict", detail: $"{email} is already on the team.");
        var member = new StaffMember(email, req.FullName, req.Role);
        passwords.Set(member, req.Password);
        db.Staff.Add(member);
        await db.SaveChangesAsync();
        return Results.Created($"/api/staff/{member.Id}", new StaffDto(member.Id, member.Email, member.FullName, member.Role, member.Active));
    }

    private static async Task<IResult> AddPractitioner(PractitionerRequest req, ClinicDbContext db, TenantContext t)
    {
        var tenant = await db.Tenants.SingleAsync(x => x.Id == t.TenantId);
        var count = await db.Practitioners.CountAsync();
        if (count >= tenant.Limits.MaxPractitioners)
            return Results.Problem(statusCode: 402, title: "Plan limit reached",
                detail: $"The {tenant.Plan} plan includes {tenant.Limits.MaxPractitioners} practitioner(s). Upgrade to add more.");
        var p = new Practitioner(req.Name, req.Specialty, req.SlotMinutes, req.Hours);
        db.Practitioners.Add(p);
        await db.SaveChangesAsync();
        return Results.Created($"/api/practitioners/{p.Id}", p);
    }

    private static async Task<Paged<Patient>> SearchPatients(ClinicDbContext db, string? search, int page = 1, int pageSize = 20)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var q = db.Patients.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = $"%{search.Trim()}%";
            q = q.Where(p => EF.Functions.ILike(p.FirstName + " " + p.LastName, term)
                          || EF.Functions.ILike(p.Phone, term)
                          || (p.Email != null && EF.Functions.ILike(p.Email, term)));
        }
        var total = await q.CountAsync();
        var items = await q.OrderBy(p => p.LastName).ThenBy(p => p.FirstName).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync();
        return new Paged<Patient>(items, page, pageSize, total);
    }

    private static void Apply(Patient p, PatientRequest req)
    {
        p.FirstName = req.FirstName.Trim();
        p.LastName = req.LastName.Trim();
        p.DateOfBirth = req.DateOfBirth;
        p.Phone = req.Phone.Trim();
        p.Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim();
        p.Notes = req.Notes?.Trim();
    }
}
