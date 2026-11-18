using SnackCounter.Core;

namespace SnackCounter.Tests;

public sealed class MockClock : TimeProvider
{
    private DateTimeOffset _now;
    public MockClock(DateTimeOffset start) { _now = start; }
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
    public void Set(DateTimeOffset to) => _now = to;
}

public sealed class Fixture
{
    public const string Oslo = "OSL";
    public const string Blr = "BLR";

    public MockClock Clock { get; } = new(new DateTimeOffset(2026, 11, 18, 8, 0, 0, TimeSpan.Zero));
    public MockChatClient Chat { get; } = new();
    public MockHrClient Hr { get; } = new();
    public RefillService Service { get; }

    public Fixture(Policy? policy = null)
    {
        var offices = new[]
        {
            new OfficeConfig(Oslo, "Oslo",      "NOK",  6_000m,  1_200m, "Europe/Oslo"),
            new OfficeConfig(Blr,  "Bengaluru", "INR", 15_000m,  3_000m, "Asia/Kolkata"),
        };
        var employees = new[]
        {
            new Employee("r1", "Ravi",  Blr,  "INR", true),
            new Employee("r2", "Priya", Blr,  "INR", true),
            new Employee("r3", "Arun",  Blr,  "INR", true),
            new Employee("n1", "Nina (not a volunteer)", Blr, "INR", false),
            new Employee("o1", "John",  Oslo, "NOK", true),
            new Employee("o2", "Sarah", Oslo, "NOK", true),
        };
        Service = new RefillService(policy ?? new Policy(), offices, employees, Chat, Hr, Clock);
    }

    public void Present(string employeeId, string officeId) =>
        Assert.True(Service.CheckIn(employeeId, officeId).IsOk);

    public Purchase Complete(string officeId, string item, decimal amount, string receipt)
    {
        var request = Service.ReportEmpty(officeId, item).Value!;
        Assert.Equal(RefillStatus.Offered, request.Status);
        var who = request.AssigneeId!;
        Assert.True(Service.Accept(request.Id, who).IsOk);
        var result = Service.RecordPurchase(request.Id, who, amount, receipt);
        Assert.True(result.IsOk, result.Outcome.ToString());
        return result.Value!;
    }
}
