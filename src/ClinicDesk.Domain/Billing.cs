namespace ClinicDesk.Domain;

public enum InvoiceStatus { Draft, Issued, PartiallyPaid, Paid, Void }

public enum PaymentMethod { Cash, Card, BankTransfer, Insurance }

public record InvoiceLine(string Description, int Quantity, decimal UnitPrice)
{
    public decimal Amount => Quantity * UnitPrice;
}

public record Payment(Guid Id, decimal Amount, PaymentMethod Method, DateTimeOffset PaidAt, string? Reference);

public class Invoice : ITenantOwned
{
    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public string Number { get; private set; } = null!;
    public Guid PatientId { get; private set; }
    public Guid? AppointmentId { get; private set; }
    public decimal TaxRate { get; private set; }
    public InvoiceStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? IssuedAt { get; private set; }
    public DateTimeOffset? DueAt { get; private set; }
    public List<InvoiceLine> Lines { get; private set; } = new();
    public List<Payment> Payments { get; private set; } = new();

    private Invoice() { }

    public Invoice(string number, Guid patientId, Guid? appointmentId, decimal taxRate, DateTimeOffset now)
    {
        if (taxRate is < 0 or > 0.5m) throw new DomainException("Tax rate must be between 0% and 50%.");
        Number = number;
        PatientId = patientId;
        AppointmentId = appointmentId;
        TaxRate = taxRate;
        Status = InvoiceStatus.Draft;
        CreatedAt = now;
    }

    public decimal Subtotal => Lines.Sum(l => l.Amount);
    public decimal Tax => Math.Round(Subtotal * TaxRate, 2, MidpointRounding.AwayFromZero);
    public decimal Total => Subtotal + Tax;
    public decimal AmountPaid => Payments.Sum(p => p.Amount);
    public decimal Balance => Total - AmountPaid;

    public bool IsOverdue(DateTimeOffset now) =>
        Status is InvoiceStatus.Issued or InvoiceStatus.PartiallyPaid && DueAt < now;

    public void AddLine(string description, int quantity, decimal unitPrice)
    {
        if (Status != InvoiceStatus.Draft) throw new DomainException("Only draft invoices can be edited.");
        if (string.IsNullOrWhiteSpace(description)) throw new DomainException("Each line needs a description.");
        if (quantity < 1) throw new DomainException("Quantity must be at least 1.");
        if (unitPrice < 0) throw new DomainException("Prices can't be negative.");
        Lines.Add(new InvoiceLine(description.Trim(), quantity, unitPrice));
    }

    public void Issue(DateTimeOffset now, int dueInDays = 14)
    {
        if (Status != InvoiceStatus.Draft) throw new DomainException("Only draft invoices can be issued.");
        if (Lines.Count == 0 || Total <= 0) throw new DomainException("An invoice needs at least one charged line.");
        Status = InvoiceStatus.Issued;
        IssuedAt = now;
        DueAt = now.AddDays(dueInDays);
    }

    public Payment RecordPayment(decimal amount, PaymentMethod method, DateTimeOffset now, string? reference = null)
    {
        if (Status is not (InvoiceStatus.Issued or InvoiceStatus.PartiallyPaid))
            throw new DomainException($"Payments can't be recorded on a {Status} invoice.");
        if (amount <= 0) throw new DomainException("Payment amount must be positive.");
        if (amount > Balance) throw new DomainException($"Payment of {amount:0.00} exceeds the balance of {Balance:0.00}.");
        var payment = new Payment(Guid.NewGuid(), amount, method, now, reference);
        Payments.Add(payment);
        Status = Balance == 0 ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid;
        return payment;
    }

    public void Void()
    {
        if (Status is InvoiceStatus.Void or InvoiceStatus.Paid) throw new DomainException($"A {Status} invoice can't be voided.");
        if (Payments.Count > 0) throw new DomainException("Invoices with payments can't be voided.");
        Status = InvoiceStatus.Void;
    }
}

public class AuditEntry : ITenantOwned
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid? UserId { get; set; }
    public string Action { get; set; } = null!;
    public string Entity { get; set; } = null!;
    public string EntityId { get; set; } = null!;
    public DateTimeOffset At { get; set; }
}
