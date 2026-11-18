namespace SnackCounter.Core;

public enum RefillStatus
{
    Pending,    
    Offered,    
    Committed,  
    Purchased,  
    Escalated   
}

public enum HrClaimStatus 
{ 
    PendingSubmission, 
    Submitted, 
    Approved, 
    Rejected, 
    Paid 
}

public sealed record OfficeConfig(string Id,string Name,string Currency,decimal MonthlyBudget, decimal MaxPurchase,string TimeZoneId);
public sealed record Employee(string Id,string Name,string HomeOfficeId,string PayrollCurrency,bool IsVolunteer);
public sealed class RefillRequest
{
    public int Id { get; init; }
    public required string OfficeId { get; init; }
    public required string Item { get; init; }
    public RefillStatus Status { get; internal set; } = RefillStatus.Pending;
    public DateTimeOffset CreatedAt { get; init; }
    public string? AssigneeId { get; internal set; }
    public DateTimeOffset? OfferExpiresAt { get; internal set; }
    public DateTimeOffset? CommitDeadline { get; internal set; }
    public decimal ReservedAmount { get; internal set; }
    public int? PurchaseId { get; internal set; }
    internal HashSet<string> Tried { get; } = new();
    internal bool EscalationNotified { get; set; }
    public IReadOnlyCollection<string> TriedEmployeeIds => Tried;
}

public sealed class Purchase
{
    public int Id { get; init; }
    public int RequestId { get; init; }
    public required string OfficeId { get; init; }
    public required string EmployeeId { get; init; }
    public required string Item { get; init; }
    public decimal Amount { get; init; }
    public required string Currency { get; init; }
    public required string ReceiptId { get; init; }
    public DateTimeOffset PurchasedAt { get; init; }
    public HrClaimStatus ClaimStatus { get; internal set; }
    public string? HrClaimId { get; internal set; }
}

public enum Outcome
{
    Ok,
    NotFound,
    NotAllowed,       
    OfferExpired,
    BudgetExhausted,
    InvalidAmount,
    OverPurchaseCap,
    MissingReceipt,
    DuplicateReceipt,
    InvalidTransition,
    UnknownOffice,
    UnknownEmployee
}

public sealed record Result<T>(Outcome Outcome, T? Value = default, string? Message = null)
{
    public bool IsOk => Outcome == Outcome.Ok;
}

public sealed class Policy
{
    public TimeSpan AcceptWindow { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan PurchaseWindow { get; init; } = TimeSpan.FromHours(24);
    public int FairnessDays { get; init; } = 30;
    public int MaxUnpaidClaimsPerEmployee { get; init; } = 2;
    public bool AllowNonVolunteerFallback { get; init; } = false;
}
