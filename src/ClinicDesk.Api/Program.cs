using System.Text.Json.Serialization;
using ClinicDesk.Api.Features;
using ClinicDesk.Api.Infrastructure;
using ClinicDesk.Api.Jobs;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<TenantContext>();
var connectionString = config.GetConnectionString("Clinic")
    ?? "Host=localhost;Database=clinicdesk;Username=clinicdesk;Password=clinicdesk";
builder.Services.AddDbContext<ClinicDbContext>(o => o.UseNpgsql(connectionString));

var jwt = config.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddSingleton(jwt);
// Tokens follow the wall clock (JWT validation does), even when tests fake the business clock.
builder.Services.AddSingleton(sp => new TokenService(jwt, TimeProvider.System));
builder.Services.AddSingleton<Passwords>();
builder.Services.AddSingleton<EmailSender>();
builder.Services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<EmailSender>());
builder.Services.AddScoped<ReminderJob>();
builder.Services.AddScoped<BillingJob>();
if (config.GetValue("Jobs:Enabled", true)) builder.Services.AddHostedService<JobRunner>();

builder.Services.AddValidatorsFromAssemblyContaining<Program>(includeInternalTypes: true);
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ErrorHandler>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = jwt.Validation();
});
Policies.Register(builder.Services.AddAuthorizationBuilder());

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(config.GetSection("Cors:Origins").Get<string[]>() ?? ["http://localhost:5173"])
    .AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddHealthChecks().AddNpgSql(connectionString, name: "database");
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "ClinicDesk API", Version = "v1" });
    var scheme = new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT",
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
    };
    o.AddSecurityDefinition("Bearer", scheme);
    o.AddSecurityRequirement(new OpenApiSecurityRequirement { [scheme] = Array.Empty<string>() });
});

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseCors();
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthentication();
app.UseMiddleware<TenantMiddleware>();
app.UseAuthorization();

app.MapHealthChecks("/health");
app.MapGet("/api/me", (HttpContext http) => Results.Ok(new
{
    id = http.User.FindFirst(Claims.Subject)?.Value,
    name = http.User.FindFirst(Claims.Name)?.Value,
    role = http.User.FindFirst(Claims.Role)?.Value,
    tenant = http.User.FindFirst(Claims.Tenant)?.Value,
})).RequireAuthorization(Policies.Staff);

Clinics.Map(app);
People.Map(app);
Appointments.Map(app);
Invoices.Map(app);
Dashboard.Map(app);

await Database.InitialiseAsync(app.Services, app.Logger);
if (config.GetValue("Seed:Demo", false)) await DemoSeed.RunAsync(app.Services);
app.Run();

public partial class Program;
