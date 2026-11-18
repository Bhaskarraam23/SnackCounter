namespace SnackCounter.Core;

public interface IChatClient
{
    void SendToEmployee(string employeeId, string message);
    void SendToCoordinators(string officeId, string message);
}
public interface IHrClient
{
    string SubmitClaim(HrClaim claim);
}
public sealed record HrClaim(string IdempotencyKey,string EmployeeId,decimal Amount,string Currency,string Description,string ReceiptId);

public class HrUnavailableException : Exception
{
    public HrUnavailableException(string message) : base(message) { }
}


