using ClinicDesk.Domain;
using Microsoft.EntityFrameworkCore;

namespace ClinicDesk.Api.Infrastructure;

/// <summary>Creates the "demo" clinic (owner demo@clinicdesk.dev / Demo#2025) for Docker Compose.</summary>
public static class DemoSeed
{
    public const string Slug = "demo";

    public static async Task RunAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<ClinicDbContext>();
        if (await db.Tenants.AnyAsync(t => t.Slug == Slug)) return;

        var clock = sp.GetRequiredService<TimeProvider>();
        var passwords = sp.GetRequiredService<Passwords>();
        var now = clock.GetUtcNow();

        var tenant = Tenant.Create("Sunrise Family Clinic", Slug, "Asia/Karachi", now);
        tenant.ChangePlan(Plan.Pro, now);
        sp.GetRequiredService<TenantContext>().Set(tenant.Id);
        db.Tenants.Add(tenant);

        foreach (var (email, name, role) in new[]
        {
            ("demo@clinicdesk.dev", "Ayesha Khan", StaffRole.Owner),
            ("frontdesk@clinicdesk.dev", "Bilal Ahmed", StaffRole.Receptionist),
            ("dr.raza@clinicdesk.dev", "Dr. Sana Raza", StaffRole.Practitioner),
        })
        {
            var member = new StaffMember(email, name, role);
            passwords.Set(member, "Demo#2025");
            db.Staff.Add(member);
        }

        var weekdays = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday };
        var raza = new Practitioner("Dr. Sana Raza", "General Practice", 30,
            weekdays.Select(d => new WorkingHours(d, new TimeOnly(9, 0), new TimeOnly(17, 0))));
        var malik = new Practitioner("Dr. Omar Malik", "Pediatrics", 20,
            weekdays.Take(5).SelectMany(d => new[] { new WorkingHours(d, new TimeOnly(10, 0), new TimeOnly(13, 0)), new WorkingHours(d, new TimeOnly(15, 0), new TimeOnly(18, 0)) }));
        db.Practitioners.AddRange(raza, malik);

        var patients = new[]
        {
            ("Hamza", "Iqbal", new DateOnly(1988, 4, 12), "+92 300 1234567", "hamza@example.com"),
            ("Fatima", "Shah", new DateOnly(1995, 9, 3), "+92 321 7654321", "fatima@example.com"),
            ("Zainab", "Ali", new DateOnly(2019, 1, 22), "+92 333 1112223", null),
            ("Usman", "Tariq", new DateOnly(1972, 11, 30), "+92 345 9998887", "usman@example.com"),
        }.Select(p => new Patient { FirstName = p.Item1, LastName = p.Item2, DateOfBirth = p.Item3, Phone = p.Item4, Email = p.Item5, CreatedAt = now }).ToList();
        db.Patients.AddRange(patients);

        // A few bookings in the next working days, taken from the real free slots.
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, tenant.Zone).Date).AddDays(1);
        var booked = new List<Appointment>();
        for (int i = 0; booked.Count < 6 && i < 14; i++, day = day.AddDays(1))
        {
            foreach (var (doctor, patient) in new[] { (raza, patients[0]), (raza, patients[1]), (malik, patients[2]), (raza, patients[3]) })
            {
                var slot = Schedule.FreeSlots(doctor, day, tenant.Zone, booked, now).Skip(booked.Count).FirstOrDefault();
                if (slot is null) continue;
                booked.Add(new Appointment(patient.Id, doctor.Id, slot.Start, slot.End, "Consultation", now));
            }
        }
        db.Appointments.AddRange(booked.Take(6));
        await db.SaveChangesAsync();
    }
}
