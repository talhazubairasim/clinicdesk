using ClinicDesk.Api.Infrastructure;
using ClinicDesk.Domain;
using FluentValidation;
using Microsoft.EntityFrameworkCore;

namespace ClinicDesk.Api.Features;

public record LineRequest(string Description, int Quantity, decimal UnitPrice);
public record CreateInvoiceRequest(Guid PatientId, Guid? AppointmentId, decimal TaxRate, List<LineRequest> Lines);
public record IssueRequest(int? DueInDays);
public record PaymentRequest(decimal Amount, PaymentMethod Method, string? Reference);

public class CreateInvoiceValidator : AbstractValidator<CreateInvoiceRequest>
{
    public CreateInvoiceValidator()
    {
        RuleFor(x => x.PatientId).NotEmpty();
        RuleFor(x => x.TaxRate).InclusiveBetween(0, 0.5m);
        RuleFor(x => x.Lines).NotEmpty().WithMessage("Add at least one line.");
        RuleForEach(x => x.Lines).ChildRules(l =>
        {
            l.RuleFor(x => x.Description).NotEmpty().MaximumLength(200);
            l.RuleFor(x => x.Quantity).InclusiveBetween(1, 1000);
            l.RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0);
        });
    }
}

public static class Invoices
{
    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api/invoices").WithTags("Invoices").RequireAuthorization(Policies.FrontDesk);
        g.MapGet("/", async (ClinicDbContext db, InvoiceStatus? status, Guid? patientId) =>
        {
            var q = db.Invoices.AsQueryable();
            if (status is { } s) q = q.Where(i => i.Status == s);
            if (patientId is { } p) q = q.Where(i => i.PatientId == p);
            return await q.OrderByDescending(i => i.CreatedAt).Take(500).ToListAsync();
        });
        g.MapGet("/{id:guid}", async (Guid id, ClinicDbContext db) => await db.Invoices.FindAsync(id) is { } i ? Results.Ok(i) : Results.NotFound());
        g.MapPost("/", Create).Validated<CreateInvoiceRequest>();
        g.MapPost("/{id:guid}/issue", Issue);
        g.MapPost("/{id:guid}/payments", async (Guid id, PaymentRequest req, ClinicDbContext db, TimeProvider clock) =>
            await Change(id, db, i => i.RecordPayment(req.Amount, req.Method, clock.GetUtcNow(), req.Reference)));
        g.MapPost("/{id:guid}/void", async (Guid id, ClinicDbContext db) => await Change(id, db, i => i.Void()));
    }

    private static async Task<IResult> Create(CreateInvoiceRequest req, ClinicDbContext db, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        if (!await db.Patients.AnyAsync(p => p.Id == req.PatientId))
            return Results.Problem(statusCode: 404, title: "Patient not found");
        if (req.AppointmentId is { } appointmentId)
        {
            var appointment = await db.Appointments.FindAsync(appointmentId);
            if (appointment is null || appointment.PatientId != req.PatientId)
                throw new DomainException("The appointment doesn't belong to this patient.");
            if (appointment.Status != AppointmentStatus.Completed)
                throw new DomainException("Only completed appointments can be invoiced.");
            if (await db.Invoices.AnyAsync(i => i.AppointmentId == appointmentId && i.Status != InvoiceStatus.Void))
                throw new DomainException("This appointment has already been invoiced.");
        }

        var year = now.Year;
        var prefix = $"INV-{year}-";
        var count = await db.Invoices.CountAsync(i => i.Number.StartsWith(prefix));
        var invoice = new Invoice($"{prefix}{count + 1:D4}", req.PatientId, req.AppointmentId, req.TaxRate, now);
        foreach (var line in req.Lines) invoice.AddLine(line.Description, line.Quantity, line.UnitPrice);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        return Results.Created($"/api/invoices/{invoice.Id}", invoice);
    }

    private static async Task<IResult> Issue(Guid id, IssueRequest? req, ClinicDbContext db, TimeProvider clock, IEmailSender email, TenantContext t)
    {
        var invoice = await db.Invoices.FindAsync(id);
        if (invoice is null) return Results.NotFound();
        invoice.Issue(clock.GetUtcNow(), req?.DueInDays ?? 14);
        await db.SaveChangesAsync();

        var patient = await db.Patients.FindAsync(invoice.PatientId);
        if (patient?.Email is { } to)
        {
            var clinic = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == t.TenantId);
            await email.SendAsync(new EmailMessage(to, $"Invoice {invoice.Number} from {clinic.Name}",
                $"Dear {patient.FirstName},\n\nYour invoice {invoice.Number} for {invoice.Total:0.00} is due on {invoice.DueAt:yyyy-MM-dd}.\n\n{clinic.Name}"));
        }
        return Results.Ok(invoice);
    }

    private static async Task<IResult> Change(Guid id, ClinicDbContext db, Action<Invoice> change)
    {
        var invoice = await db.Invoices.FindAsync(id);
        if (invoice is null) return Results.NotFound();
        change(invoice);
        await db.SaveChangesAsync();
        return Results.Ok(invoice);
    }
}
