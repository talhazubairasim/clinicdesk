namespace ClinicDesk.Domain;

public enum AppointmentStatus { Scheduled, CheckedIn, Completed, Cancelled, NoShow }

public class Appointment : ITenantOwned
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid PatientId { get; private set; }
    public Guid PractitionerId { get; private set; }
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }
    public AppointmentStatus Status { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public string? CancellationReason { get; private set; }
    public DateTimeOffset? ReminderSentAt { get; set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    /// <summary>Maps to Postgres xmin for optimistic concurrency.</summary>
    public uint Version { get; private set; }

    private Appointment() { }

    public Appointment(Guid patientId, Guid practitionerId, DateTimeOffset startsAt, DateTimeOffset endsAt, string reason, DateTimeOffset now)
    {
        if (endsAt <= startsAt) throw new DomainException("An appointment must end after it starts.");
        if (startsAt < now) throw new DomainException("Appointments can't be booked in the past.");
        PatientId = patientId;
        PractitionerId = practitionerId;
        StartsAt = startsAt.ToUniversalTime();
        EndsAt = endsAt.ToUniversalTime();
        Reason = reason.Trim();
        Status = AppointmentStatus.Scheduled;
        UpdatedAt = now;
    }

    public bool Overlaps(DateTimeOffset start, DateTimeOffset end) => StartsAt < end && start < EndsAt;

    public void CheckIn(DateTimeOffset now)
    {
        Require(AppointmentStatus.Scheduled, "check in");
        if (now < StartsAt.AddHours(-2)) throw new DomainException("Patients can check in at most 2 hours early.");
        Move(AppointmentStatus.CheckedIn, now);
    }

    public void Complete(DateTimeOffset now)
    {
        Require(AppointmentStatus.CheckedIn, "complete");
        Move(AppointmentStatus.Completed, now);
    }

    public void Cancel(string reason, DateTimeOffset now)
    {
        if (Status is not (AppointmentStatus.Scheduled or AppointmentStatus.CheckedIn))
            throw new DomainException($"A {Status} appointment can't be cancelled.");
        if (string.IsNullOrWhiteSpace(reason)) throw new DomainException("A cancellation reason is required.");
        CancellationReason = reason.Trim();
        Move(AppointmentStatus.Cancelled, now);
    }

    public void MarkNoShow(DateTimeOffset now)
    {
        Require(AppointmentStatus.Scheduled, "mark as no-show");
        if (now < StartsAt) throw new DomainException("An appointment can only be a no-show after it starts.");
        Move(AppointmentStatus.NoShow, now);
    }

    private void Require(AppointmentStatus expected, string action)
    {
        if (Status != expected) throw new DomainException($"Can't {action} a {Status} appointment.");
    }

    private void Move(AppointmentStatus to, DateTimeOffset now)
    {
        Status = to;
        UpdatedAt = now;
    }
}

/// <summary>Turns a practitioner's weekly hours (clinic local time) into concrete UTC slots.</summary>
public static class Schedule
{
    public record Slot(DateTimeOffset Start, DateTimeOffset End);

    public static IReadOnlyList<Slot> SlotsOn(Practitioner p, DateOnly localDate, TimeZoneInfo zone)
    {
        var slots = new List<Slot>();
        foreach (var h in p.Hours.Where(h => h.Day == localDate.DayOfWeek))
        {
            for (var t = h.Start; t.AddMinutes(p.SlotMinutes) <= h.End && t.AddMinutes(p.SlotMinutes) > t; t = t.AddMinutes(p.SlotMinutes))
            {
                var start = ToUtc(localDate, t, zone);
                slots.Add(new Slot(start, start.AddMinutes(p.SlotMinutes)));
            }
        }
        return slots;
    }

    /// <summary>Free slots: inside working hours, in the future, not clashing with live appointments.</summary>
    public static IReadOnlyList<Slot> FreeSlots(Practitioner p, DateOnly localDate, TimeZoneInfo zone,
        IEnumerable<Appointment> booked, DateTimeOffset now)
    {
        var live = booked.Where(a => a.Status != AppointmentStatus.Cancelled).ToList();
        return SlotsOn(p, localDate, zone).Where(s => s.Start >= now && !live.Any(a => a.Overlaps(s.Start, s.End))).ToList();
    }

    /// <summary>True when [start, end) lies inside one of the practitioner's working windows.</summary>
    public static bool WithinWorkingHours(Practitioner p, DateTimeOffset start, DateTimeOffset end, TimeZoneInfo zone)
    {
        var localStart = TimeZoneInfo.ConvertTime(start, zone);
        var localEnd = TimeZoneInfo.ConvertTime(end, zone);
        if (localStart.Date != localEnd.Date && localEnd.TimeOfDay != TimeSpan.Zero) return false;
        var day = localStart.DayOfWeek;
        var s = TimeOnly.FromTimeSpan(localStart.TimeOfDay);
        var e = localEnd.TimeOfDay == TimeSpan.Zero && localEnd.Date > localStart.Date ? TimeOnly.MaxValue : TimeOnly.FromTimeSpan(localEnd.TimeOfDay);
        return p.Hours.Any(h => h.Day == day && s >= h.Start && e <= h.End);
    }

    public static DateTimeOffset ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }
}
