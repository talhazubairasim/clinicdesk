namespace ClinicDesk.Domain;

/// <summary>Every row owned by a clinic carries its tenant id; the DbContext filters and stamps it.</summary>
public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

/// <summary>A broken business rule. The API turns it into a 409 / 422 response.</summary>
public class DomainException(string message) : Exception(message);

public enum Plan { Free, Pro, Enterprise }

public enum SubscriptionStatus { Active, PastDue, Cancelled }

public enum StaffRole { Owner, Admin, Practitioner, Receptionist }

public sealed record PlanLimits(int MaxPractitioners, int MaxAppointmentsPerMonth, decimal MonthlyPrice)
{
    public static PlanLimits For(Plan plan) => plan switch
    {
        Plan.Free => new(1, 50, 0m),
        Plan.Pro => new(5, 1_000, 49m),
        Plan.Enterprise => new(int.MaxValue, int.MaxValue, 199m),
        _ => throw new ArgumentOutOfRangeException(nameof(plan)),
    };
}
