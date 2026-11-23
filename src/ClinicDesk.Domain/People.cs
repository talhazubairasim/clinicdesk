namespace ClinicDesk.Domain;

public class StaffMember : ITenantOwned
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Email { get; private set; } = null!;
    public string FullName { get; private set; } = null!;
    public StaffRole Role { get; private set; }
    public string PasswordHash { get; set; } = string.Empty;
    public bool Active { get; private set; } = true;

    private StaffMember() { }

    public StaffMember(string email, string fullName, StaffRole role)
    {
        Email = email.Trim().ToLowerInvariant();
        FullName = fullName.Trim();
        Role = role;
    }

    public void Deactivate()
    {
        if (Role == StaffRole.Owner) throw new DomainException("The clinic owner can't be deactivated.");
        Active = false;
    }
}

public class Patient : ITenantOwned
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string FirstName { get; set; } = null!;
    public string LastName { get; set; } = null!;
    public DateOnly DateOfBirth { get; set; }
    public string Phone { get; set; } = null!;
    public string? Email { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public string FullName => $"{FirstName} {LastName}";
}

/// <summary>A weekly working window in the clinic's local time, e.g. Monday 09:00–13:00.</summary>
public record WorkingHours(DayOfWeek Day, TimeOnly Start, TimeOnly End);

public class Practitioner : ITenantOwned
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Name { get; set; } = null!;
    public string Specialty { get; set; } = null!;
    public int SlotMinutes { get; private set; } = 30;
    public List<WorkingHours> Hours { get; private set; } = new();

    private Practitioner() { }

    public Practitioner(string name, string specialty, int slotMinutes, IEnumerable<WorkingHours> hours)
    {
        Name = name.Trim();
        Specialty = specialty.Trim();
        SetSchedule(slotMinutes, hours);
    }

    public void SetSchedule(int slotMinutes, IEnumerable<WorkingHours> hours)
    {
        if (slotMinutes is < 5 or > 240) throw new DomainException("Slots must be 5–240 minutes long.");
        var list = hours.OrderBy(h => h.Day).ThenBy(h => h.Start).ToList();
        foreach (var h in list)
            if (h.End <= h.Start) throw new DomainException($"{h.Day}: working hours must end after they start.");
        for (int i = 1; i < list.Count; i++)
            if (list[i].Day == list[i - 1].Day && list[i].Start < list[i - 1].End)
                throw new DomainException($"{list[i].Day}: working hours overlap.");
        SlotMinutes = slotMinutes;
        Hours = list;
    }
}
