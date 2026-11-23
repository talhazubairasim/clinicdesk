using System.Net;
using System.Net.Http.Json;
using ClinicDesk.Api.Features;
using ClinicDesk.Api.Infrastructure;
using ClinicDesk.Api.Jobs;
using ClinicDesk.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace ClinicDesk.Api.Tests;

[Collection("api")]
public class AccountTests(ApiFixture app)
{
    [Fact]
    public async Task Signup_login_and_validation()
    {
        var clinic = await app.NewClinicAsync();
        var anon = app.CreateClient();

        var dup = await anon.PostAsJsonAsync("/api/clinics", new SignupRequest("X", clinic.Slug, "UTC", "X", "x@x.dev", "Passw0rd!"), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        var bad = await anon.PostAsJsonAsync("/api/clinics", new SignupRequest("", "No Spaces", "UTC", "", "nope", "short"), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var body = await bad.Content.ReadAsStringAsync();
        Assert.Contains("Slug", body);
        Assert.Contains("Password", body);

        var badZone = await anon.PostAsJsonAsync("/api/clinics", new SignupRequest("X", $"z{clinic.Slug}"[..20], "Mars/Base", "X", "x@x.dev", "Passw0rd!"), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Conflict, badZone.StatusCode);

        var login = await clinic.Post<SessionDto>("/api/auth/login", new LoginRequest(clinic.Slug.ToUpper(), "OWNER@" + clinic.Slug + ".dev", "Passw0rd!"));
        Assert.Equal("Owner", login.Role);
        Assert.Equal(clinic.Owner.TenantId, login.TenantId);

        var wrong = await anon.PostAsJsonAsync("/api/auth/login", new LoginRequest(clinic.Slug, $"owner@{clinic.Slug}.dev", "nope"), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        var otherClinic = await anon.PostAsJsonAsync("/api/auth/login", new LoginRequest("no-such-clinic", $"owner@{clinic.Slug}.dev", "Passw0rd!"), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, otherClinic.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/patients")).StatusCode);
        var info = await clinic.Get<ClinicDto>("/api/clinic");
        Assert.Equal(Plan.Free, info.Plan);
        Assert.Equal(1, info.Usage.MaxPractitioners);
    }

    [Fact]
    public async Task Roles_limit_what_staff_can_do()
    {
        var clinic = await app.NewClinicAsync();
        var doctor = await clinic.AddPractitioner();
        var patient = await clinic.AddPatient();
        var desk = await clinic.AddStaff(StaffRole.Receptionist);
        var medic = await clinic.AddStaff(StaffRole.Practitioner);

        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Client(desk).GetAsync("/api/staff")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Client(desk).PutAsJsonAsync("/api/clinic/plan", new ChangePlanRequest(Plan.Pro), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Client(medic).PostAsJsonAsync("/api/appointments",
            new BookRequest(patient.Id, doctor.Id, clinic.Tomorrow(10), null, null), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Client(medic).GetAsync("/api/invoices")).StatusCode);

        var booked = await clinic.Post<AppointmentDto>("/api/appointments", new BookRequest(patient.Id, doctor.Id, clinic.Tomorrow(10), null, null), desk);
        Assert.Equal(HttpStatusCode.Forbidden, (await clinic.Client(desk).PostAsync($"/api/appointments/{booked.Id}/complete", null)).StatusCode);
        Assert.Equal(1, (await clinic.Get<Paged<PatientView>>("/api/patients", medic)).Total);

        // Deactivated staff can no longer sign in; the owner can't be removed.
        var team = await clinic.Get<List<StaffDto>>("/api/staff");
        Assert.Equal(3, team.Count);
        var deskId = team.Single(s => s.Role == StaffRole.Receptionist).Id;
        Assert.Equal(HttpStatusCode.NoContent, (await clinic.Client().DeleteAsync($"/api/staff/{deskId}")).StatusCode);
        var relogin = await app.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(clinic.Slug, desk.Email, "Passw0rd!"), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Unauthorized, relogin.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await clinic.Client().DeleteAsync($"/api/staff/{clinic.Owner.UserId}")).StatusCode);
    }
}

[Collection("api")]
public class TenancyTests(ApiFixture app)
{
    [Fact]
    public async Task Clinics_never_see_each_others_data()
    {
        var a = await app.NewClinicAsync();
        var b = await app.NewClinicAsync();
        var patientA = await a.AddPatient("Alice");
        var doctorA = await a.AddPractitioner();
        var doctorB = await b.AddPractitioner();
        await a.Book(patientA.Id, doctorA.Id, a.Tomorrow(9));

        var listB = await b.Get<Paged<PatientView>>("/api/patients");
        Assert.Equal(0, listB.Total);
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client().GetAsync($"/api/patients/{patientA.Id}")).StatusCode);
        Assert.Empty(await b.Get<List<AppointmentDto>>("/api/appointments"));
        Assert.Single(await a.Get<List<AppointmentDto>>("/api/appointments"));

        // B can't book A's patient, or with A's practitioner, or touch A's appointment.
        var res = await b.Client().PostAsJsonAsync("/api/appointments", new BookRequest(patientA.Id, doctorB.Id, b.Tomorrow(9), null, null), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        var appointmentA = (await a.Get<List<AppointmentDto>>("/api/appointments"))[0];
        Assert.Equal(HttpStatusCode.NotFound, (await b.Client().PostAsJsonAsync($"/api/appointments/{appointmentA.Id}/cancel", new CancelRequest("x"), ApiFixture.Json)).StatusCode);

        // Same email may exist in two clinics; staff and audit stay separate.
        var audit = await b.Get<List<AuditEntry>>("/api/clinic/audit");
        Assert.All(audit, e => Assert.Equal(b.Owner.TenantId, e.TenantId));
        Assert.Contains(await a.Get<List<AuditEntry>>("/api/clinic/audit"), e => e.Entity == nameof(Appointment) && e.Action == "Added");
    }
}

[Collection("api")]
public class BookingTests(ApiFixture app)
{
    [Fact]
    public async Task Slots_booking_rules_and_plan_limits()
    {
        var clinic = await app.NewClinicAsync();
        var doctor = await clinic.AddPractitioner();
        var patient = await clinic.AddPatient();
        var day = DateOnly.FromDateTime(clinic.Tomorrow(0).UtcDateTime);

        var slots = await clinic.Get<List<SlotView>>($"/api/appointments/slots?practitionerId={doctor.Id}&date={day:yyyy-MM-dd}");
        Assert.Equal(16, slots.Count);

        var booked = await clinic.Book(patient.Id, doctor.Id, clinic.Tomorrow(10));
        Assert.Equal(clinic.Tomorrow(10, 30), booked.EndsAt);
        Assert.Equal("Pat Ient", booked.PatientName);
        Assert.Equal(15, (await clinic.Get<List<SlotView>>($"/api/appointments/slots?practitionerId={doctor.Id}&date={day:yyyy-MM-dd}")).Count);

        async Task<HttpStatusCode> Try(DateTimeOffset at, DateTimeOffset? end = null) =>
            (await clinic.Client().PostAsJsonAsync("/api/appointments", new BookRequest(patient.Id, doctor.Id, at, end, null), ApiFixture.Json)).StatusCode;
        Assert.Equal(HttpStatusCode.Conflict, await Try(clinic.Tomorrow(10, 15)));                 // overlaps
        Assert.Equal(HttpStatusCode.Conflict, await Try(clinic.Tomorrow(8, 30)));                  // before hours
        Assert.Equal(HttpStatusCode.Conflict, await Try(clinic.Tomorrow(16, 45)));                 // runs past 17:00
        Assert.Equal(HttpStatusCode.Conflict, await Try(clinic.Tomorrow(10).AddDays(-2)));         // in the past
        Assert.Equal(HttpStatusCode.BadRequest, await Try(clinic.Tomorrow(12), clinic.Tomorrow(11)));

        // Cancelling frees the slot.
        await clinic.Post<AppointmentDto>($"/api/appointments/{booked.Id}/cancel", new CancelRequest("Patient unwell"));
        Assert.Equal(HttpStatusCode.Created, await Try(clinic.Tomorrow(10, 15), clinic.Tomorrow(10, 45)));

        // Free plan: one practitioner. Upgrading lifts the limit and charges pro rata.
        var second = await clinic.Client().PostAsJsonAsync("/api/practitioners", new PractitionerRequest("Dr. Two", "Dentist", 20, Clinic.EveryDay()), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.PaymentRequired, second.StatusCode);
        var upgraded = await clinic.Ok<ClinicDto>(await clinic.Client().PutAsJsonAsync("/api/clinic/plan", new ChangePlanRequest(Plan.Pro), ApiFixture.Json));
        Assert.Equal(Plan.Pro, upgraded.Plan);
        Assert.Single(upgraded.Charges);
        await clinic.AddPractitioner("Dr. Two");
        var downgrade = await clinic.Client().PutAsJsonAsync("/api/clinic/plan", new ChangePlanRequest(Plan.Free), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Conflict, downgrade.StatusCode);
    }

    [Fact]
    public async Task Concurrent_bookings_for_the_same_slot_admit_exactly_one()
    {
        var clinic = await app.NewClinicAsync();
        await clinic.Client().PutAsJsonAsync("/api/clinic/plan", new ChangePlanRequest(Plan.Pro), ApiFixture.Json);
        var doctor = await clinic.AddPractitioner();
        var patients = new List<PatientView>();
        for (int i = 0; i < 8; i++) patients.Add(await clinic.AddPatient($"P{i}"));

        var at = clinic.Tomorrow(14);
        var results = await Task.WhenAll(patients.Select(p =>
            clinic.Client().PostAsJsonAsync("/api/appointments", new BookRequest(p.Id, doctor.Id, at, null, null), ApiFixture.Json)));

        Assert.Equal(1, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Single(await clinic.Get<List<AppointmentDto>>($"/api/appointments?practitionerId={doctor.Id}"));
    }

    [Fact]
    public async Task Slots_use_the_clinic_time_zone()
    {
        var clinic = await app.NewClinicAsync("Asia/Karachi");
        var doctor = await clinic.AddPractitioner();
        var day = DateOnly.FromDateTime(clinic.Tomorrow(0).UtcDateTime);
        var slots = await clinic.Get<List<SlotView>>($"/api/appointments/slots?practitionerId={doctor.Id}&date={day:yyyy-MM-dd}");
        Assert.Equal(clinic.Tomorrow(4), slots[0].Start.ToUniversalTime());   // 09:00 PKT
        Assert.Equal(clinic.Tomorrow(12), slots[^1].End.ToUniversalTime());   // 17:00 PKT
    }
}

[Collection("api")]
public class VisitAndBillingTests(ApiFixture app)
{
    [Fact]
    public async Task Visit_lifecycle_invoice_and_payments()
    {
        var clinic = await app.NewClinicAsync();
        var doctor = await clinic.AddPractitioner();
        var patient = await clinic.AddPatient("Maya", "maya@example.com");
        var appointment = await clinic.Book(patient.Id, doctor.Id, clinic.Tomorrow(11));

        var early = await clinic.Client().PostAsJsonAsync("/api/invoices",
            new CreateInvoiceRequest(patient.Id, appointment.Id, 0, [new LineRequest("Consultation", 1, 3000)]), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);   // not completed yet

        app.Clock.SetUtcNow(appointment.StartsAt.AddMinutes(-10));
        Assert.Equal(AppointmentStatus.CheckedIn, (await clinic.Post<AppointmentDto>($"/api/appointments/{appointment.Id}/check-in", new { })).Status);
        Assert.Equal(AppointmentStatus.Completed, (await clinic.Post<AppointmentDto>($"/api/appointments/{appointment.Id}/complete", new { })).Status);
        var again = await clinic.Client().PostAsync($"/api/appointments/{appointment.Id}/check-in", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var invoice = await clinic.Post<InvoiceView>("/api/invoices", new CreateInvoiceRequest(patient.Id, appointment.Id, 0.1m,
            [new LineRequest("Consultation", 1, 3000), new LineRequest("ECG", 1, 1500)]));
        Assert.Equal(InvoiceStatus.Draft, invoice.Status);
        Assert.Equal(4950m, invoice.Total);
        Assert.StartsWith("INV-2030-", invoice.Number);

        var duplicate = await clinic.Client().PostAsJsonAsync("/api/invoices",
            new CreateInvoiceRequest(patient.Id, appointment.Id, 0, [new LineRequest("Again", 1, 1)]), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var before = app.Emails.Sent.Count;
        invoice = await clinic.Post<InvoiceView>($"/api/invoices/{invoice.Id}/issue", new IssueRequest(7));
        Assert.Equal(InvoiceStatus.Issued, invoice.Status);
        Assert.Contains(app.Emails.Sent.Skip(before), m => m.To == "maya@example.com" && m.Subject.Contains(invoice.Number));

        invoice = await clinic.Post<InvoiceView>($"/api/invoices/{invoice.Id}/payments", new PaymentRequest(2000, PaymentMethod.Cash, null));
        Assert.Equal(InvoiceStatus.PartiallyPaid, invoice.Status);
        var over = await clinic.Client().PostAsJsonAsync($"/api/invoices/{invoice.Id}/payments", new PaymentRequest(9999, PaymentMethod.Card, null), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        invoice = await clinic.Post<InvoiceView>($"/api/invoices/{invoice.Id}/payments", new PaymentRequest(2950, PaymentMethod.Card, "txn-42"));
        Assert.Equal(InvoiceStatus.Paid, invoice.Status);
        Assert.Equal(0m, invoice.Balance);

        var dash = await clinic.Get<DashboardDto>("/api/dashboard");
        Assert.Equal(4950m, dash.CollectedThisMonth);
        Assert.Equal(0m, dash.Outstanding);
        Assert.Equal(1, dash.Patients);
        Assert.Equal(1, dash.TodayByStatus["Completed"]);

        var paid = await clinic.Get<List<InvoiceView>>("/api/invoices?status=Paid");
        Assert.Single(paid);
    }

    [Fact]
    public async Task Reminders_go_out_once_a_day_ahead_and_subscriptions_renew()
    {
        var clinic = await app.NewClinicAsync();
        var doctor = await clinic.AddPractitioner();
        var address = $"rem-{clinic.Slug}@example.com";
        var withEmail = await clinic.AddPatient("Rem", address);
        var noEmail = await clinic.AddPatient("Nomail", null);
        await clinic.Book(withEmail.Id, doctor.Id, clinic.Tomorrow(9));
        await clinic.Book(noEmail.Id, doctor.Id, clinic.Tomorrow(10));
        await clinic.Book(withEmail.Id, doctor.Id, clinic.Tomorrow(9).AddDays(3));   // too far ahead for a reminder

        // Move to 20 hours before the first appointment so it falls inside the 24-hour window.
        var target = clinic.Tomorrow(9).AddHours(-20);
        if (app.Clock.GetUtcNow() < target) app.Clock.SetUtcNow(target);

        for (int run = 0; run < 2; run++)   // the second run must not send again
        {
            using var scope = app.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ReminderJob>().RunAsync();
        }
        var reminders = app.Emails.Sent.Where(m => m.To == address).ToList();
        Assert.Single(reminders);
        Assert.Contains("Reminder", reminders[0].Subject);
        Assert.Contains("Dr. Who", reminders[0].Body);

        await clinic.Client().PutAsJsonAsync("/api/clinic/plan", new ChangePlanRequest(Plan.Pro), ApiFixture.Json);
        app.Clock.Advance(TimeSpan.FromDays(40));
        using (var scope = app.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<BillingJob>().RunAsync() >= 1);
        var info = await clinic.Get<ClinicDto>("/api/clinic");
        Assert.True(info.CurrentPeriodEnd > app.Clock.GetUtcNow());
        Assert.Contains(info.Charges, c => c.Description == "Renewal" && c.Amount == 49m);
    }
}
