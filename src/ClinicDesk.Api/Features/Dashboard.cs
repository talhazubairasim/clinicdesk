using ClinicDesk.Api.Infrastructure;
using ClinicDesk.Domain;
using Microsoft.EntityFrameworkCore;

namespace ClinicDesk.Api.Features;

public record DashboardDto(
    DateOnly Today,
    Dictionary<string, int> TodayByStatus,
    IReadOnlyList<AppointmentDto> Upcoming,
    int Patients,
    decimal CollectedThisMonth,
    decimal Outstanding,
    int OverdueInvoices,
    UsageDto Usage);

public static class Dashboard
{
    public static void Map(WebApplication app) =>
        app.MapGet("/api/dashboard", Get).WithTags("Dashboard").RequireAuthorization(Policies.Staff);

    private static async Task<DashboardDto> Get(ClinicDbContext db, TenantContext t, TimeProvider clock)
    {
        var now = clock.GetUtcNow();
        var tenant = await db.Tenants.AsNoTracking().SingleAsync(x => x.Id == t.TenantId);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, tenant.Zone).Date);
        var dayStart = Schedule.ToUtc(today, TimeOnly.MinValue, tenant.Zone);
        var dayEnd = Schedule.ToUtc(today.AddDays(1), TimeOnly.MinValue, tenant.Zone);

        var todays = await db.Appointments.Where(a => a.StartsAt >= dayStart && a.StartsAt < dayEnd)
            .GroupBy(a => a.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync();
        var upcoming = await Appointments.Project(db, db.Appointments
            .Where(a => a.StartsAt >= now && a.Status == AppointmentStatus.Scheduled).OrderBy(a => a.StartsAt).Take(5)).ToListAsync();

        // Lines and payments live in jsonb, so money is summed in memory over the clinic's live invoices.
        var (monthStart, monthEnd) = Clinics.MonthOf(tenant, now);
        var invoices = await db.Invoices.AsNoTracking()
            .Where(i => i.Status != InvoiceStatus.Draft && i.Status != InvoiceStatus.Void).ToListAsync();

        return new DashboardDto(
            today,
            todays.ToDictionary(x => x.Key.ToString(), x => x.Count),
            upcoming,
            await db.Patients.CountAsync(),
            invoices.SelectMany(i => i.Payments).Where(p => p.PaidAt >= monthStart && p.PaidAt < monthEnd).Sum(p => p.Amount),
            invoices.Sum(i => i.Balance),
            invoices.Count(i => i.IsOverdue(now)),
            await Clinics.Usage(db, tenant, now));
    }
}
