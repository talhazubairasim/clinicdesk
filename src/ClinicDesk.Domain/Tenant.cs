using System.Text.RegularExpressions;

namespace ClinicDesk.Domain;

/// <summary>A clinic: the tenant, and its subscription.</summary>
public class Tenant
{
    private static readonly Regex SlugPattern = new("^[a-z0-9](?:[a-z0-9-]{1,38}[a-z0-9])$", RegexOptions.Compiled);

    public Guid Id { get; private set; } = Guid.NewGuid();
    public string Name { get; private set; } = null!;
    public string Slug { get; private set; } = null!;
    public string TimeZone { get; private set; } = "UTC";
    public Plan Plan { get; private set; }
    public SubscriptionStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset CurrentPeriodEnd { get; private set; }
    public List<SubscriptionCharge> Charges { get; private set; } = new();

    private Tenant() { }

    public static Tenant Create(string name, string slug, string timeZone, DateTimeOffset now)
    {
        if (!SlugPattern.IsMatch(slug))
            throw new DomainException("Slug must be 3–40 lowercase letters, digits or hyphens.");
        _ = ResolveTimeZone(timeZone);
        return new Tenant
        {
            Name = name.Trim(), Slug = slug, TimeZone = timeZone, Plan = Plan.Free,
            Status = SubscriptionStatus.Active, CreatedAt = now, CurrentPeriodEnd = now.AddMonths(1),
        };
    }

    public PlanLimits Limits => PlanLimits.For(Plan);

    public TimeZoneInfo Zone => ResolveTimeZone(TimeZone);

    public static TimeZoneInfo ResolveTimeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new DomainException($"Unknown time zone '{id}'."); }
    }

    /// <summary>
    /// Switches plan immediately. Upgrades are charged pro rata for the rest of the period;
    /// downgrades take effect now and are not refunded. Callers check usage against the new limits.
    /// </summary>
    public SubscriptionCharge? ChangePlan(Plan plan, DateTimeOffset now)
    {
        if (Status == SubscriptionStatus.Cancelled) throw new DomainException("The subscription is cancelled.");
        if (plan == Plan) throw new DomainException($"Already on the {plan} plan.");
        var oldPrice = Limits.MonthlyPrice;
        Plan = plan;
        var newPrice = Limits.MonthlyPrice;
        if (newPrice <= oldPrice) return null;

        var periodStart = CurrentPeriodEnd.AddMonths(-1);
        var remaining = (decimal)Math.Clamp((CurrentPeriodEnd - now) / (CurrentPeriodEnd - periodStart), 0, 1);
        var charge = new SubscriptionCharge(Guid.NewGuid(), plan, Math.Round((newPrice - oldPrice) * remaining, 2), now, CurrentPeriodEnd, "Upgrade (pro rata)");
        Charges.Add(charge);
        return charge;
    }

    /// <summary>Renews every elapsed period. A failed payment leaves the clinic past due.</summary>
    public IReadOnlyList<SubscriptionCharge> Renew(DateTimeOffset now, bool paymentSucceeds = true)
    {
        var charges = new List<SubscriptionCharge>();
        if (Status == SubscriptionStatus.Cancelled) return charges;
        while (CurrentPeriodEnd <= now)
        {
            if (!paymentSucceeds && Limits.MonthlyPrice > 0) { Status = SubscriptionStatus.PastDue; return charges; }
            var start = CurrentPeriodEnd;
            CurrentPeriodEnd = start.AddMonths(1);
            var charge = new SubscriptionCharge(Guid.NewGuid(), Plan, Limits.MonthlyPrice, start, CurrentPeriodEnd, "Renewal");
            Charges.Add(charge);
            charges.Add(charge);
        }
        Status = SubscriptionStatus.Active;
        return charges;
    }

    public void Cancel() => Status = SubscriptionStatus.Cancelled;
}

public record SubscriptionCharge(Guid Id, Plan Plan, decimal Amount, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd, string Description);
