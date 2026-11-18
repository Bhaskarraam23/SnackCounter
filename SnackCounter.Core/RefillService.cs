namespace SnackCounter.Core;
public sealed class RefillService
{
    private readonly object _gate = new();
    private readonly Policy _policy;
    private readonly Dictionary<string, OfficeConfig> _offices;
    private readonly Dictionary<string, Employee> _employees;
    private readonly IChatClient _chat;
    private readonly IHrClient _hr;
    private readonly TimeProvider _clock;

    private readonly List<RefillRequest> _requests = new();
    private readonly List<Purchase> _purchases = new();
    private readonly List<(string EmployeeId, DateTimeOffset At)> _offerHistory = new();
    private readonly Dictionary<string, (string OfficeId, DateOnly LocalDate, bool Available)> _presence = new();

    public RefillService(Policy policy, IEnumerable<OfficeConfig> offices, IEnumerable<Employee> employees, IChatClient chat, IHrClient hr,TimeProvider clock)
    {
        _policy = policy;
        _offices = offices.ToDictionary(o => o.Id);
        _employees = employees.ToDictionary(e => e.Id);
        _chat = chat;
        _hr = hr;
        _clock = clock;
    }

    public Result<bool> CheckIn(string employeeId, string? officeId, bool available = true)
    {
        lock (_gate)
        {
            if (!_employees.ContainsKey(employeeId)) return new(Outcome.UnknownEmployee);
            var now = _clock.GetUtcNow();

            if (officeId == null)
            {
                _presence.Remove(employeeId);
                return new(Outcome.Ok, true);
            }
            if (!_offices.TryGetValue(officeId, out var office)) return new(Outcome.UnknownOffice);

            _presence[employeeId] = (officeId, LocalDate(office, now), available);
            Sweep(now);
            RetryOpenRequests(now);  
            return new(Outcome.Ok, true);
        }
    }
    public Result<RefillRequest> ReportEmpty(string officeId, string item)
    {
        lock (_gate)
        {
            if (!_offices.ContainsKey(officeId)) return new(Outcome.UnknownOffice);
            var key = Normalise(item);
            var now = _clock.GetUtcNow();
            Sweep(now);

            var open = _requests.FirstOrDefault(r =>
                r.OfficeId == officeId && Normalise(r.Item) == key && r.Status != RefillStatus.Purchased);
            if (open != null)
                return new(Outcome.Ok, open, "Already open; no duplicate created.");

            var request = new RefillRequest
            {
                Id = _requests.Count + 1,
                OfficeId = officeId,
                Item = item.Trim(),
                CreatedAt = now
            };
            _requests.Add(request);
            Assign(request, now);
            return new(Outcome.Ok, request);
        }
    }

    public Result<RefillRequest> Accept(int requestId, string employeeId)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            Sweep(now);

            var request = Find(requestId);
            if (request == null) return new(Outcome.NotFound);
            if (request.Status == RefillStatus.Committed && request.AssigneeId == employeeId) return new(Outcome.Ok, request);
            if (request.Status != RefillStatus.Offered || request.AssigneeId != employeeId)
                return new(Outcome.NotAllowed, request, "This refill is not currently offered to you.");

            if (request.OfferExpiresAt <= now) return new(Outcome.OfferExpired, request);
            var office = _offices[request.OfficeId];
            if (Headroom(office, now) < office.MaxPurchase)
            {
                Escalate(request, "Monthly budget would be exceeded");
                return new(Outcome.BudgetExhausted, request, "Budget is exhausted; nobody has been asked to spend.");
            }

            request.Status = RefillStatus.Committed;
            request.ReservedAmount = office.MaxPurchase;          
            request.OfferExpiresAt = null;
            request.CommitDeadline = now + _policy.PurchaseWindow;

            _chat.SendToEmployee(employeeId,
                $"Thanks! Please buy '{request.Item}' and submit the receipt within {_policy.PurchaseWindow.TotalHours:0} hours. " +
                $"Spend at most {office.MaxPurchase} {office.Currency}.");
            return new(Outcome.Ok, request);
        }
    }

    public Result<RefillRequest> Decline(int requestId, string employeeId)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            Sweep(now);

            var request = Find(requestId);
            if (request == null) return new(Outcome.NotFound);
            if (request.Status != RefillStatus.Offered || request.AssigneeId != employeeId)
                return new(Outcome.NotAllowed, request);

            Release(request);
            Assign(request, now);   
            return new(Outcome.Ok, request);
        }
    }

    public Result<Purchase> RecordPurchase(int requestId, string employeeId, decimal amount, string receiptId)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            Sweep(now);

            var request = Find(requestId);
            if (request == null) return new(Outcome.NotFound);
            if (request.Status != RefillStatus.Committed || request.AssigneeId != employeeId)
                return new(Outcome.NotAllowed, null, "Only the person who committed can submit the purchase.");

            if (amount <= 0) return new(Outcome.InvalidAmount);
            if (string.IsNullOrWhiteSpace(receiptId)) return new(Outcome.MissingReceipt);

            var office = _offices[request.OfficeId];
            if (amount > office.MaxPurchase)
                return new(Outcome.OverPurchaseCap, null,
                    $"Maximum per refill is {office.MaxPurchase} {office.Currency}. Contact the coordinator.");

            var receipt = receiptId.Trim();
            if (_purchases.Any(p => string.Equals(p.ReceiptId, receipt, StringComparison.OrdinalIgnoreCase)))
                return new(Outcome.DuplicateReceipt, null, "This receipt has already been claimed.");

            if (SpentInMonth(office, now) + amount > office.MonthlyBudget)
                return new(Outcome.BudgetExhausted, null, "Monthly budget exceeded.");

            var purchase = new Purchase
            {
                Id = _purchases.Count + 1,
                RequestId = request.Id,
                OfficeId = office.Id,
                EmployeeId = employeeId,
                Item = request.Item,
                Amount = amount,
                Currency = office.Currency,
                ReceiptId = receipt,
                PurchasedAt = now,
                ClaimStatus = HrClaimStatus.PendingSubmission
            };
            _purchases.Add(purchase);

            request.Status = RefillStatus.Purchased;
            request.PurchaseId = purchase.Id;
            request.ReservedAmount = 0;
            request.CommitDeadline = null;

            TrySubmitToHr(purchase);   
            return new(Outcome.Ok, purchase);
        }
    }

    public Result<Purchase> OnHrStatusChanged(string hrClaimId, HrClaimStatus newStatus)
    {
        lock (_gate)
        {
            var purchase = _purchases.FirstOrDefault(p => p.HrClaimId == hrClaimId);
            if (purchase == null) return new(Outcome.NotFound);

            var allowed = purchase.ClaimStatus switch
            {
                HrClaimStatus.Submitted => newStatus is HrClaimStatus.Approved or HrClaimStatus.Rejected,
                HrClaimStatus.Approved => newStatus is HrClaimStatus.Paid or HrClaimStatus.Rejected,
                _ => false
            };
            if (!allowed)
                return new(Outcome.InvalidTransition, purchase, $"{purchase.ClaimStatus} -> {newStatus} is not allowed.");

            purchase.ClaimStatus = newStatus;
            _chat.SendToEmployee(purchase.EmployeeId, $"HR reimbursement for '{purchase.Item}' is now {newStatus}.");
            if (newStatus == HrClaimStatus.Rejected)
                _chat.SendToCoordinators(purchase.OfficeId,
                    $"HR rejected the claim for purchase {purchase.Id}. It no longer counts against the budget.");
            return new(Outcome.Ok, purchase);
        }
    }

    public void Tick()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            Sweep(now);
            RetryOpenRequests(now);
            foreach (var p in _purchases.Where(p => p.ClaimStatus == HrClaimStatus.PendingSubmission))
                TrySubmitToHr(p);
        }
    }


    public IReadOnlyList<RefillRequest> Requests { get { lock (_gate) return _requests.ToList(); } }
    public IReadOnlyList<Purchase> Purchases { get { lock (_gate) return _purchases.ToList(); } }
    public IReadOnlyList<OfficeConfig> Offices => _offices.Values.ToList();

    public RefillRequest? GetRequest(int id) { lock (_gate) return Find(id); }

    public decimal SpentThisMonth(string officeId)
    {
        lock (_gate) return SpentInMonth(_offices[officeId], _clock.GetUtcNow());
    }


    private RefillRequest? Find(int id) => _requests.FirstOrDefault(r => r.Id == id);

    private static string Normalise(string s) => s.Trim().ToLowerInvariant();

    private DateOnly LocalDate(OfficeConfig office, DateTimeOffset utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById(office.TimeZoneId)).DateTime);

    private (int Year, int Month) LocalMonth(OfficeConfig office, DateTimeOffset utc)
    {
        var d = LocalDate(office, utc);
        return (d.Year, d.Month);
    }

    private decimal SpentInMonth(OfficeConfig office, DateTimeOffset now)
    {
        var month = LocalMonth(office, now);
        return _purchases
            .Where(p => p.OfficeId == office.Id &&
                        p.ClaimStatus != HrClaimStatus.Rejected &&
                        LocalMonth(office, p.PurchasedAt) == month)
            .Sum(p => p.Amount);
    }

    private decimal Headroom(OfficeConfig office, DateTimeOffset now)
    {
        var reserved = _requests
            .Where(r => r.OfficeId == office.Id && r.Status == RefillStatus.Committed)
            .Sum(r => r.ReservedAmount);
        return office.MonthlyBudget - SpentInMonth(office, now) - reserved;
    }

    private void Sweep(DateTimeOffset now)
    {
        foreach (var r in _requests.Where(r => r.Status == RefillStatus.Offered && r.OfferExpiresAt <= now).ToList())
        {
            _chat.SendToEmployee(r.AssigneeId!, $"The offer to refill '{r.Item}' expired and was passed to someone else.");
            Release(r);
            Assign(r, now);
        }

        foreach (var r in _requests.Where(r => r.Status == RefillStatus.Committed && r.CommitDeadline <= now).ToList())
        {
            _chat.SendToEmployee(r.AssigneeId!, $"The deadline to buy '{r.Item}' passed. It has been handed to someone else.");
            Release(r);          
            Assign(r, now);
        }
    }

    private void Release(RefillRequest r)
    {
        r.Status = RefillStatus.Pending;
        r.AssigneeId = null;
        r.OfferExpiresAt = null;
        r.CommitDeadline = null;
        r.ReservedAmount = 0;
    }

    private void RetryOpenRequests(DateTimeOffset now)
    {
        foreach (var r in _requests.Where(r => r.Status is RefillStatus.Pending or RefillStatus.Escalated).ToList())
            Assign(r, now);
    }

    private int UnpaidClaims(string employeeId) =>
        _purchases.Count(p => p.EmployeeId == employeeId &&
                              p.ClaimStatus is HrClaimStatus.PendingSubmission or HrClaimStatus.Submitted or HrClaimStatus.Approved);

    private void Assign(RefillRequest request, DateTimeOffset now)
    {
        var office = _offices[request.OfficeId];
        if (Headroom(office, now) < office.MaxPurchase)
        {
            Escalate(request, "Monthly budget is used up");
            return;
        }

        var today = LocalDate(office, now);
        var windowStart = now - TimeSpan.FromDays(_policy.FairnessDays);

        var candidates = _employees.Values
            .Where(e => _presence.TryGetValue(e.Id, out var p) &&
                        p.OfficeId == office.Id && p.LocalDate == today && p.Available)   
            .Where(e => !request.Tried.Contains(e.Id))                                     
            .Where(e => e.IsVolunteer || _policy.AllowNonVolunteerFallback)
            .Where(e => UnpaidClaims(e.Id) < _policy.MaxUnpaidClaimsPerEmployee)           
            .OrderBy(e => _offerHistory.Count(h => h.EmployeeId == e.Id && h.At >= windowStart))  
            .ThenBy(e => _offerHistory.Where(h => h.EmployeeId == e.Id).Select(h => h.At).DefaultIfEmpty(DateTimeOffset.MinValue).Max())
            .ThenBy(e => e.Id, StringComparer.Ordinal)                                     
            .ToList();

        if (candidates.Count == 0)
        {
            Escalate(request, "Nobody eligible is available at the office right now");
            return;
        }

        var chosen = candidates[0];
        request.Tried.Add(chosen.Id);
        request.Status = RefillStatus.Offered;
        request.AssigneeId = chosen.Id;
        request.OfferExpiresAt = now + _policy.AcceptWindow;
        request.EscalationNotified = false;
        _offerHistory.Add((chosen.Id, now));

        _chat.SendToEmployee(chosen.Id,
            $"'{request.Item}' is empty at {office.Name}. Can you refill it? Please accept within {_policy.AcceptWindow.TotalMinutes:0} minutes.");
    }

    private void Escalate(RefillRequest request, string reason)
    {
        Release(request);
        request.Status = RefillStatus.Escalated;
        if (!request.EscalationNotified)   
        {
            request.EscalationNotified = true;
            _chat.SendToCoordinators(request.OfficeId, $"'{request.Item}': {reason}. Manual action needed; it will retry automatically.");
        }
    }

    private void TrySubmitToHr(Purchase p)
    {
        try
        {
            p.HrClaimId = _hr.SubmitClaim(new HrClaim(
                IdempotencyKey: $"purchase-{p.Id}",
                EmployeeId: p.EmployeeId,
                Amount: p.Amount,
                Currency: p.Currency,
                Description: $"Office snacks: {p.Item}",
                ReceiptId: p.ReceiptId));
            p.ClaimStatus = HrClaimStatus.Submitted;
        }
        catch (HrUnavailableException)
        {
            p.ClaimStatus = HrClaimStatus.PendingSubmission;   
        }
    }
}
