namespace SnackCounter.Core;

public sealed class MockChatClient : IChatClient
{
    private readonly object _gate = new();
    private readonly List<(string To, string Message)> _sent = new();

    public IReadOnlyList<(string To, string Message)> Sent { get { lock (_gate) return _sent.ToList(); } }

    public void SendToEmployee(string employeeId, string message) { lock (_gate) _sent.Add((employeeId, message)); }
    public void SendToCoordinators(string officeId, string message) { lock (_gate) _sent.Add(($"coordinators:{officeId}", message)); }
}

public sealed class MockHrClient : IHrClient
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string ClaimId, HrClaim Claim)> _byKey = new();
    private int _next = 1;

    public int FailNextCalls { get; set; }
    public int CallCount { get; private set; }

    public IReadOnlyList<HrClaim> Claims { get { lock (_gate) return _byKey.Values.Select(v => v.Claim).ToList(); } }

    public string SubmitClaim(HrClaim claim)
    {
        lock (_gate)
        {
            CallCount++;
            if (FailNextCalls > 0)
            {
                FailNextCalls--;
                throw new HrUnavailableException("HR system is down (simulated).");
            }
            if (_byKey.TryGetValue(claim.IdempotencyKey, out var existing))
                return existing.ClaimId;

            var id = $"HR-{_next++:00000}";
            _byKey[claim.IdempotencyKey] = (id, claim);
            return id;
        }
    }
}
