using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClinicDesk.Api.Features;
using ClinicDesk.Api.Infrastructure;
using ClinicDesk.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;

namespace ClinicDesk.Api.Tests;

/// <summary>One real Postgres (Testcontainers) and one app for the whole suite. Every test signs up its own clinic.</summary>
public class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _db = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();

    /// <summary>The business clock: a Monday, midnight UTC. Tests only move it forward.</summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2030, 1, 7, 0, 0, 0, TimeSpan.Zero));

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task InitializeAsync() => await _db.StartAsync();

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _db.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Clinic", _db.GetConnectionString());
        builder.UseSetting("Jobs:Enabled", "false");
        builder.UseSetting("Seed:Demo", "false");
        builder.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(Clock));
    }

    public EmailSender Emails => Services.GetRequiredService<EmailSender>();

    /// <summary>Signs up a new clinic open 7 days a week, 09:00–17:00 in <paramref name="timeZone"/>.</summary>
    public async Task<Clinic> NewClinicAsync(string timeZone = "UTC")
    {
        var slug = $"c{Guid.NewGuid():N}"[..20];
        var anon = CreateClient();
        var res = await anon.PostAsJsonAsync("/api/clinics",
            new SignupRequest("Test Clinic", slug, timeZone, "Olivia Owner", $"owner@{slug}.dev", "Passw0rd!"), Json);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var session = (await res.Content.ReadFromJsonAsync<SessionDto>(Json))!;
        return new Clinic(this, slug, session);
    }
}

[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ApiFixture>;

public record Clinic(ApiFixture App, string Slug, SessionDto Owner)
{
    public HttpClient Client(SessionDto? as_ = null)
    {
        var c = App.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (as_ ?? Owner).Token);
        return c;
    }

    public async Task<T> Ok<T>(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        return (await res.Content.ReadFromJsonAsync<T>(ApiFixture.Json))!;
    }

    public async Task<T> Post<T>(string url, object body, SessionDto? as_ = null) =>
        await Ok<T>(await Client(as_).PostAsJsonAsync(url, body, ApiFixture.Json));

    public async Task<T> Get<T>(string url, SessionDto? as_ = null) => await Ok<T>(await Client(as_).GetAsync(url));

    public static List<WorkingHours> EveryDay(int from = 9, int to = 17) =>
        Enum.GetValues<DayOfWeek>().Select(d => new WorkingHours(d, new TimeOnly(from, 0), new TimeOnly(to, 0))).ToList();

    public Task<PractitionerView> AddPractitioner(string name = "Dr. Who", int slot = 30) =>
        Post<PractitionerView>("/api/practitioners", new PractitionerRequest(name, "GP", slot, EveryDay()));

    public Task<PatientView> AddPatient(string first = "Pat", string? email = "pat@example.com") =>
        Post<PatientView>("/api/patients", new PatientRequest(first, "Ient", new DateOnly(1990, 5, 1), "+92 300 1234567", email, null));

    public async Task<SessionDto> AddStaff(StaffRole role)
    {
        var email = $"{role.ToString().ToLower()}@{Slug}.dev";
        await Post<StaffDto>("/api/staff", new AddStaffRequest(email, $"{role} Person", role, "Passw0rd!"));
        return await Post<SessionDto>("/api/auth/login", new LoginRequest(Slug, email, "Passw0rd!"));
    }

    /// <summary>Tomorrow (relative to the fake clock) at the given UTC hour.</summary>
    public DateTimeOffset Tomorrow(int hour, int minute = 0)
    {
        var d = App.Clock.GetUtcNow().UtcDateTime.Date.AddDays(1);
        return new DateTimeOffset(d.Year, d.Month, d.Day, hour, minute, 0, TimeSpan.Zero);
    }

    public async Task<AppointmentDto> Book(Guid patient, Guid practitioner, DateTimeOffset at) =>
        await Post<AppointmentDto>("/api/appointments", new BookRequest(patient, practitioner, at, null, "Checkup"));
}

public record PractitionerView(Guid Id, string Name, int SlotMinutes);
public record PatientView(Guid Id, string FirstName, string LastName, string FullName, string? Email);
public record InvoiceView(Guid Id, string Number, InvoiceStatus Status, decimal Total, decimal Balance, decimal AmountPaid);
public record SlotView(DateTimeOffset Start, DateTimeOffset End);
