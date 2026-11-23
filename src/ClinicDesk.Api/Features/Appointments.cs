using ClinicDesk.Api.Infrastructure;
using ClinicDesk.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ClinicDesk.Api.Features;

public record BookRequest(Guid PatientId, Guid PractitionerId, DateTimeOffset StartsAt, DateTimeOffset? EndsAt, string? Reason);
public record CancelRequest(string Reason);
public record AppointmentDto(Guid Id, Guid PatientId, string PatientName, Guid PractitionerId, string PractitionerName,
    DateTimeOffset StartsAt, DateTimeOffset EndsAt, AppointmentStatus Status, string Reason, string? CancellationReason);

public class BookValidator : AbstractValidator<BookRequest>
{
    public BookValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.PractitionerId).NotEmpty();
        RuleFor(x => x.EndsAt).GreaterThan(x => x.StartsAt).When(x => x.EndsAt.HasValue).WithMessage("End must be after start.");
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

public static class Appointments
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/appointments").WithTags("Appointments");
        g.MapGet("/", List).RequireAuthorization(Policies.Staff);
        g.MapGet("/slots", Slots).RequireAuthorization(Policies.Staff);
        g.MapPost("/", Book).Validated<BookRequest>().RequireAuthorization(Policies.FrontDesk);
        g.MapPost("/{id:guid}/check-in", (Guid id, ClinicDbContext db, TimeProvider c) => Transition(id, db, c, (a, now) => a.CheckIn(now)))
            .RequireAuthorization(Policies.FrontDesk);
        g.MapPost("/{id:guid}/complete", (Guid id, ClinicDbContext db, TimeProvider c) => Transition(id, db, c, (a, now) => a.Complete(now)))
            .RequireAuthorization(Policies.Clinical);
        g.MapPost("/{id:guid}/no-show", (Guid id, ClinicDbContext db, TimeProvider c) => Transition(id, db, c, (a, now) => a.MarkNoShow(now)))
            .RequireAuthorization(Policies.FrontDesk);
        g.MapPost("/{id:guid}/cancel", (Guid id, CancelRequest req, ClinicDbContext db, TimeProvider c) =>
            Transition(id, db, c, (a, now) => a.Cancel(req.Reason, now))).RequireAuthorization(Policies.FrontDesk);
    }

    private static async Task<IResult> List(ClinicDbContext db, DateTimeOffset? from, DateTimeOffset? to, Guid? practitionerId, Guid? patientId)
    {
        var q = db.Appointments.AsQueryable();
        if (from is { } f) q = q.Where(a => a.EndsAt > f.ToUniversalTime());
        if (to is { } t) q = q.Where(a => a.StartsAt < t.ToUniversalTime());
        if (practitionerId is { } pr) q = q.Where(a => a.PractitionerId == pr);
        if (patientId is { } pa) q = q.Where(a => a.PatientId == pa);
        return Results.Ok(await Project(db, q.OrderBy(a => a.StartsAt).Take(500)).ToListAsync());
    }

    public static IQueryable<AppointmentDto> Project(ClinicDbContext db, IQueryable<Appointment> q) =>
        from a in q
        join p in db.Patients on a.PatientId equals p.Id
        join d in db.Practitioners on a.PractitionerId equals d.Id
        select new AppointmentDto(a.Id, a.PatientId, p.FirstName + " " + p.LastName, a.PractitionerId, d.Name,
            a.StartsAt, a.EndsAt, a.Status, a.Reason, a.CancellationReason);

    private static async Task<IResult> Slots(ClinicDbContext db, TenantContext t, TimeProvider clock, Guid practitionerId, DateOnly date)
    {
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == t.TenantId);
        var practitioner = await db.Practitioners.FindAsync(practitionerId);
        if (practitioner is null) return Results.NotFound();
        var dayStart = Schedule.ToUtc(date, TimeOnly.MinValue, tenant.Zone);
        var dayEnd = Schedule.ToUtc(date.AddDays(1), TimeOnly.MinValue, tenant.Zone);
        var booked = await db.Appointments.Where(a => a.PractitionerId == practitionerId && a.StartsAt < dayEnd && a.EndsAt > dayStart).ToListAsync();
        return Results.Ok(Schedule.FreeSlots(practitioner, date, tenant.Zone, booked, clock.GetUtcNow()));
    }

    private static async Task<IResult> Book(BookRequest req, ClinicDbContext db, TenantContext t, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == t.TenantId);
        if (tenant.Status != SubscriptionStatus.Active)
            return Results.Problem(statusCode: 402, title: "Subscription inactive", detail: "Settle the clinic's subscription to book new appointments.");
        if (!await db.Patients.AnyAsync(p => p.Id == req.PatientId))
            return Results.Problem(statusCode: 404, title: "Patient not found");
        var practitioner = await db.Practitioners.FindAsync(req.PractitionerId);
        if (practitioner is null) return Results.Problem(statusCode: 404, title: "Practitioner not found");

        var start = req.StartsAt.ToUniversalTime();
        var end = (req.EndsAt ?? req.StartsAt.AddMinutes(practitioner.SlotMinutes)).ToUniversalTime();
        if (!Schedule.WithinWorkingHours(practitioner, start, end, tenant.Zone))
            throw new DomainException($"{practitioner.Name} isn't working at that time.");

        var usage = await Clinics.Usage(db, tenant, start);
        if (usage.AppointmentsThisMonth >= usage.MaxAppointmentsPerMonth)
            return Results.Problem(statusCode: 402, title: "Plan limit reached",
                detail: $"The {tenant.Plan} plan includes {usage.MaxAppointmentsPerMonth} appointments a month. Upgrade to book more.");

        // Friendly early check; the exclusion constraint in Postgres is what makes double booking impossible under concurrency.
        if (await db.Appointments.AnyAsync(a => a.PractitionerId == practitioner.Id && a.Status != AppointmentStatus.Cancelled
                                               && a.StartsAt < end && start < a.EndsAt))
            throw new DomainException("That time overlaps another appointment for this practitioner.");

        var appointment = new Appointment(req.PatientId, practitioner.Id, start, end, req.Reason ?? "", now);
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync();
        var dto = await Project(db, db.Appointments.Where(a => a.Id == appointment.Id)).SingleAsync();
        return Results.Created($"/api/appointments/{appointment.Id}", dto);
    }

    private static async Task<IResult> Transition(Guid id, ClinicDbContext db, TimeProvider clock, Action<Appointment, DateTimeOffset> change)
    {
        var appointment = await db.Appointments.FindAsync(id);
        if (appointment is null) return Results.NotFound();
        change(appointment, clock.GetUtcNow());
        await db.SaveChangesAsync();
        return Results.Ok(await Project(db, db.Appointments.Where(a => a.Id == id)).SingleAsync());
    }
}
