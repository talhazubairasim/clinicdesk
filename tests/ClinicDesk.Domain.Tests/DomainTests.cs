using ClinicDesk.Domain;

namespace ClinicDesk.Domain.Tests;

public class ScheduleTests
{
    private static readonly TimeZoneInfo Karachi = TimeZoneInfo.FindSystemTimeZoneById("Asia/Karachi"); // UTC+5, no DST
    private static readonly DateOnly Monday = new(2026, 11, 23);

    private static Practitioner Doctor(int slot = 30) => new("Dr. A", "GP", slot, new[]
    {
        new WorkingHours(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(12, 0)),
        new WorkingHours(DayOfWeek.Monday, new TimeOnly(14, 0), new TimeOnly(15, 0)),
    });

    [Fact]
    public void Slots_follow_working_hours_in_clinic_time()
    {
        var slots = Schedule.SlotsOn(Doctor(), Monday, Karachi);
        Assert.Equal(8, slots.Count); // 6 in the morning + 2 in the afternoon
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 4, 0, 0, TimeSpan.Zero), slots[0].Start); // 09:00 PKT
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero), slots[^1].End);  // 15:00 PKT
        Assert.Empty(Schedule.SlotsOn(Doctor(), Monday.AddDays(1), Karachi));
    }

    [Fact]
    public void Slots_that_do_not_fit_are_dropped()
    {
        Assert.Equal(5, Schedule.SlotsOn(Doctor(45), Monday, Karachi).Count); // 09:00–12:00 → 4, 14:00–15:00 → 1 (14:45 would overrun)
    }

    [Fact]
    public void Free_slots_exclude_past_and_booked_times_but_not_cancelled_ones()
    {
        var doctor = Doctor();
        var now = new DateTimeOffset(2026, 11, 23, 4, 10, 0, TimeSpan.Zero); // 09:10 PKT
        var earlier = now.AddDays(-1);
        var booked = new Appointment(Guid.NewGuid(), doctor.Id, now.AddMinutes(20), now.AddMinutes(50), "", earlier); // 09:30–10:00
        var cancelled = new Appointment(Guid.NewGuid(), doctor.Id, now.AddMinutes(50), now.AddMinutes(80), "", earlier); // 10:00–10:30
        cancelled.Cancel("patient called", earlier);

        var free = Schedule.FreeSlots(doctor, Monday, Karachi, new[] { booked, cancelled }, now);
        Assert.Equal(new DateTimeOffset(2026, 11, 23, 5, 0, 0, TimeSpan.Zero), free[0].Start); // 10:00 — cancelled slot is free again
        Assert.DoesNotContain(free, s => s.Start == booked.StartsAt);
        Assert.Equal(6, free.Count);
    }

    [Theory]
    [InlineData(9, 0, 9, 30, true)]
    [InlineData(11, 30, 12, 0, true)]
    [InlineData(11, 45, 12, 15, false)]
    [InlineData(8, 30, 9, 0, false)]
    [InlineData(12, 0, 14, 0, false)]
    public void Working_hours_are_checked_in_clinic_time(int sh, int sm, int eh, int em, bool expected)
    {
        var start = Schedule.ToUtc(Monday, new TimeOnly(sh, sm), Karachi);
        var end = Schedule.ToUtc(Monday, new TimeOnly(eh, em), Karachi);
        Assert.Equal(expected, Schedule.WithinWorkingHours(Doctor(), start, end, Karachi));
    }

    [Fact]
    public void Schedules_reject_bad_hours()
    {
        Assert.Throws<DomainException>(() => new Practitioner("A", "B", 30, new[] { new WorkingHours(DayOfWeek.Monday, new TimeOnly(10, 0), new TimeOnly(9, 0)) }));
        Assert.Throws<DomainException>(() => new Practitioner("A", "B", 30, new[]
        {
            new WorkingHours(DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(12, 0)),
            new WorkingHours(DayOfWeek.Monday, new TimeOnly(11, 0), new TimeOnly(13, 0)),
        }));
        Assert.Throws<DomainException>(() => new Practitioner("A", "B", 2, Array.Empty<WorkingHours>()));
    }
}

public class AppointmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 4, 0, 0, TimeSpan.Zero);
    private static Appointment Booked(int inMinutes = 60) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Now.AddMinutes(inMinutes), Now.AddMinutes(inMinutes + 30), "Checkup", Now);

    [Fact]
    public void Happy_path_check_in_then_complete()
    {
        var a = Booked();
        a.CheckIn(Now);
        a.Complete(Now.AddMinutes(90));
        Assert.Equal(AppointmentStatus.Completed, a.Status);
    }

    [Fact]
    public void Invalid_transitions_are_rejected()
    {
        Assert.Throws<DomainException>(() => new Appointment(Guid.NewGuid(), Guid.NewGuid(), Now.AddMinutes(-5), Now.AddMinutes(25), "", Now));
        Assert.Throws<DomainException>(() => new Appointment(Guid.NewGuid(), Guid.NewGuid(), Now.AddMinutes(30), Now.AddMinutes(30), "", Now));
        Assert.Throws<DomainException>(() => Booked().Complete(Now));             // not checked in
        Assert.Throws<DomainException>(() => Booked(300).CheckIn(Now));          // more than 2h early
        Assert.Throws<DomainException>(() => Booked().MarkNoShow(Now));          // hasn't started
        Assert.Throws<DomainException>(() => Booked().Cancel(" ", Now));         // reason required

        var done = Booked();
        done.CheckIn(Now);
        done.Complete(Now);
        Assert.Throws<DomainException>(() => done.Cancel("late", Now));
    }

    [Fact]
    public void No_show_after_start_and_cancel_records_reason()
    {
        var a = Booked();
        a.MarkNoShow(Now.AddMinutes(61));
        Assert.Equal(AppointmentStatus.NoShow, a.Status);

        var b = Booked();
        b.Cancel("  Patient travelling  ", Now);
        Assert.Equal("Patient travelling", b.CancellationReason);
        Assert.True(Booked().Overlaps(Now.AddMinutes(80), Now.AddMinutes(120)));
        Assert.False(Booked().Overlaps(Now.AddMinutes(90), Now.AddMinutes(120)));
    }
}

public class InvoiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);

    private static Invoice Draft(decimal tax = 0.17m)
    {
        var i = new Invoice("INV-2026-0001", Guid.NewGuid(), null, tax, Now);
        i.AddLine("Consultation", 1, 2000m);
        i.AddLine("Blood test", 2, 750.50m);
        return i;
    }

    [Fact]
    public void Totals_include_tax_rounded_to_cents()
    {
        var i = Draft();
        Assert.Equal(3501m, i.Subtotal);
        Assert.Equal(595.17m, i.Tax);
        Assert.Equal(4096.17m, i.Total);
    }

    [Fact]
    public void Payments_move_the_invoice_to_paid()
    {
        var i = Draft(0);
        i.Issue(Now, 7);
        Assert.Equal(Now.AddDays(7), i.DueAt);
        i.RecordPayment(1000m, PaymentMethod.Cash, Now);
        Assert.Equal(InvoiceStatus.PartiallyPaid, i.Status);
        Assert.Throws<DomainException>(() => i.RecordPayment(5000m, PaymentMethod.Card, Now));
        Assert.True(i.IsOverdue(Now.AddDays(8)));
        i.RecordPayment(2501m, PaymentMethod.Card, Now, "txn-1");
        Assert.Equal(InvoiceStatus.Paid, i.Status);
        Assert.Equal(0m, i.Balance);
        Assert.False(i.IsOverdue(Now.AddDays(30)));
        Assert.Throws<DomainException>(() => i.Void());
    }

    [Fact]
    public void Drafts_are_editable_and_issued_invoices_are_not()
    {
        var empty = new Invoice("X", Guid.NewGuid(), null, 0, Now);
        Assert.Throws<DomainException>(() => empty.Issue(Now));
        Assert.Throws<DomainException>(() => empty.AddLine("x", 0, 1));
        Assert.Throws<DomainException>(() => empty.AddLine("x", 1, -1));
        Assert.Throws<DomainException>(() => empty.RecordPayment(1, PaymentMethod.Cash, Now));
        Assert.Throws<DomainException>(() => new Invoice("X", Guid.NewGuid(), null, 0.9m, Now));

        var i = Draft();
        i.Issue(Now);
        Assert.Throws<DomainException>(() => i.AddLine("More", 1, 1));
        Assert.Throws<DomainException>(() => i.Issue(Now));
        i.Void();
        Assert.Equal(InvoiceStatus.Void, i.Status);
    }
}

public class TenantTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Signup_validates_slug_and_time_zone()
    {
        Assert.Throws<DomainException>(() => Tenant.Create("A", "Bad Slug", "UTC", Now));
        Assert.Throws<DomainException>(() => Tenant.Create("A", "ok-slug", "Mars/Olympus", Now));
        var t = Tenant.Create(" Clinic ", "ok-slug", "Asia/Karachi", Now);
        Assert.Equal("Clinic", t.Name);
        Assert.Equal(Plan.Free, t.Plan);
        Assert.Equal(Now.AddMonths(1), t.CurrentPeriodEnd);
    }

    [Fact]
    public void Upgrades_are_charged_pro_rata_and_downgrades_are_free()
    {
        var t = Tenant.Create("A", "abc", "UTC", Now);
        var halfway = Now + (t.CurrentPeriodEnd - Now) / 2;
        var charge = t.ChangePlan(Plan.Pro, halfway);
        Assert.Equal(24.50m, charge!.Amount);
        Assert.Null(t.ChangePlan(Plan.Free, halfway));
        Assert.Throws<DomainException>(() => t.ChangePlan(Plan.Free, halfway));
        Assert.Equal(1, PlanLimits.For(Plan.Free).MaxPractitioners);
    }

    [Fact]
    public void Renewal_charges_each_missed_period_or_goes_past_due()
    {
        var t = Tenant.Create("A", "abc", "UTC", Now);
        t.ChangePlan(Plan.Pro, Now);
        var charges = t.Renew(Now.AddMonths(2).AddDays(1));
        Assert.Equal(2, charges.Count);
        Assert.All(charges, c => Assert.Equal(49m, c.Amount));
        Assert.Equal(Now.AddMonths(3), t.CurrentPeriodEnd);

        Assert.Empty(t.Renew(Now.AddMonths(4), paymentSucceeds: false));
        Assert.Equal(SubscriptionStatus.PastDue, t.Status);
        t.Renew(Now.AddMonths(4));
        Assert.Equal(SubscriptionStatus.Active, t.Status);
        t.Cancel();
        Assert.Throws<DomainException>(() => t.ChangePlan(Plan.Enterprise, Now));
    }

    [Fact]
    public void Staff_owner_cannot_be_deactivated()
    {
        Assert.Throws<DomainException>(() => new StaffMember("A@X.dev", "A", StaffRole.Owner).Deactivate());
        var r = new StaffMember(" B@X.DEV ", "B", StaffRole.Receptionist);
        r.Deactivate();
        Assert.False(r.Active);
        Assert.Equal("b@x.dev", r.Email);
    }
}
