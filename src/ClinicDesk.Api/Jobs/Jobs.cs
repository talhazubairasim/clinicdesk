using ClinicDesk.Api.Infrastructure;
using ClinicDesk.Domain;
using Microsoft.EntityFrameworkCore;

namespace ClinicDesk.Api.Jobs;

/// <summary>Emails patients 24 hours before their appointment, once.</summary>
public class ReminderJob(ClinicDbContext db, IEmailSender email, TimeProvider clock)
{
    public static readonly TimeSpan Lead = TimeSpan.FromHours(24);

    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var horizon = now + Lead;
        var due = await (from a in db.Appointments.IgnoreQueryFilters()
                         join p in db.Patients.IgnoreQueryFilters() on a.PatientId equals p.Id
                         join d in db.Practitioners.IgnoreQueryFilters() on a.PractitionerId equals d.Id
                         join t in db.Tenants on a.TenantId equals t.Id
                         where a.Status == AppointmentStatus.Scheduled && a.ReminderSentAt == null
                               && a.StartsAt > now && a.StartsAt <= horizon && p.Email != null
                         select new { Appointment = a, Patient = p, Practitioner = d.Name, Clinic = t })
                        .Take(200).ToListAsync(ct);

        foreach (var x in due)
        {
            var local = TimeZoneInfo.ConvertTime(x.Appointment.StartsAt, x.Clinic.Zone);
            await email.SendAsync(new EmailMessage(x.Patient.Email!, $"Reminder: your appointment at {x.Clinic.Name}",
                $"Dear {x.Patient.FirstName},\n\nThis is a reminder of your appointment with {x.Practitioner} on " +
                $"{local:dddd d MMMM 'at' HH:mm}.\n\n{x.Clinic.Name}"), ct);
            x.Appointment.ReminderSentAt = now;
        }
        await db.SaveChangesAsync(ct);
        return due.Count;
    }
}

/// <summary>Renews subscriptions whose period has ended (payment is simulated).</summary>
public class BillingJob(ClinicDbContext db, TimeProvider clock, ILogger<BillingJob> log)
{
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var due = await db.Tenants.Where(t => t.CurrentPeriodEnd <= now && t.Status != SubscriptionStatus.Cancelled).ToListAsync(ct);
        foreach (var tenant in due)
            foreach (var charge in tenant.Renew(now))
                log.LogInformation("Charged {Clinic} {Amount} for {Plan}", tenant.Slug, charge.Amount, charge.Plan);
        await db.SaveChangesAsync(ct);
        return due.Count;
    }
}

/// <summary>Runs the jobs every minute. Disabled in tests, which call the jobs directly.</summary>
public class JobRunner(IServiceScopeFactory scopes, ILogger<JobRunner> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ReminderJob>().RunAsync(stoppingToken);
                await scope.ServiceProvider.GetRequiredService<BillingJob>().RunAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Background jobs failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
