using SnackCounter.Core;

namespace SnackCounter.Tests;

public class RefillRuleTests
{

    [Fact]
    public void Reporting_the_same_empty_box_twice_creates_one_request()
    {
        var f = new Fixture();
        f.Present("r1", Fixture.Blr);

        var first = f.Service.ReportEmpty(Fixture.Blr, "Biscuits").Value!;
        var second = f.Service.ReportEmpty(Fixture.Blr, "  biscuits ").Value!;

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, f.Service.Requests.Count);
    }

    [Fact]
    public void Fifty_simultaneous_reports_of_the_same_item_create_exactly_one_request_and_one_offer()
    {
        var f = new Fixture();
        f.Present("r1", Fixture.Blr);
        f.Present("r2", Fixture.Blr);

        Parallel.For(0, 50, _ => f.Service.ReportEmpty(Fixture.Blr, "Biscuits"));

        Assert.Equal(1, f.Service.Requests.Count);
        Assert.Equal(1, f.Chat.Sent.Count(m => m.Message.Contains("Can you refill it")));
    }


    [Fact]
    public void Only_the_person_who_was_offered_the_refill_can_commit()
    {
        var f = new Fixture();
        f.Present("r1", Fixture.Blr);
        f.Present("r2", Fixture.Blr);
        var request = f.Service.ReportEmpty(Fixture.Blr, "Biscuits").Value!;
        var offeredTo = request.AssigneeId!;
        var other = offeredTo == "r1" ? "r2" : "r1";

        Assert.Equal(Outcome.NotAllowed, f.Service.Accept(request.Id, other).Outcome);
        Assert.True(f.Service.Accept(request.Id, offeredTo).IsOk);
        Assert.Equal(offeredTo, f.Service.GetRequest(request.Id)!.AssigneeId);
    }

    [Fact]
    public void Two_people_racing_to_accept_exactly_one_wins_and_money_is_reserved_once()
    {
        var f = new Fixture();
        f.Present("r1", Fixture.Blr);
        f.Present("r2", Fixture.Blr);
        var request = f.Service.ReportEmpty(Fixture.Blr, "Biscuits").Value!;
        var offeredTo = request.AssigneeId!;
        var other = offeredTo == "r1" ? "r2" : "r1";

        var winners = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.For(0, 40, i =>
        {
            var who = i % 2 == 0 ? offeredTo : other;
            if (f.Service.Accept(request.Id, who).IsOk) winners.Add(who);
        });

        Assert.True(winners.All(w => w == offeredTo));    
        var after = f.Service.GetRequest(request.Id)!;
        Assert.Equal(RefillStatus.Committed, after.Status);
        Assert.Equal(offeredTo, after.AssigneeId);
        Assert.Equal(3_000m, after.ReservedAmount);        
    }


    [Fact]
    public void An_offer_that_is_not_accepted_within_30_minutes_goes_to_someone_else()
    {
        var f = new Fixture();
        f.Present("r1", Fixture.Blr);
        f.Present("r2", Fixture.Blr);
        var request = f.Service.ReportEmpty(Fixture.Blr, "Biscuits").Value!;
        var first = request.AssigneeId!;

        f.Clock.Advance(TimeSpan.FromMinutes(31));
        f.Service.Tick();

        var after = f.Service.GetRequest(request.Id)!;
        Assert.Equal(RefillStatus.Offered, after.Status);
        Assert.True(after.AssigneeId != first);
        Assert.Equal(Outcome.NotAllowed, f.Service.Accept(request.Id, first).Outcome);  
    }

    [Fact]
    public void A_commitment_not_completed_within_24_hours_is_handed_on_and_the_reserved_money_is_released()
    {
        var f = new Fixture();
        f.Present("r1", Fixture.Blr);
        f.Present("r2", Fixture.Blr);
        var request = f.Service.ReportEmpty(Fixture.Blr, "Biscuits").Value!;
        var first = request.AssigneeId!;
        Assert.True(f.Service.Accept(request.Id, first).IsOk);
        Assert.Equal(3_000m, f.Service.GetRequest(request.Id)!.ReservedAmount);

        f.Clock.Advance(TimeSpan.FromHours(25));
        f.Present("r1", Fixture.Blr);
        f.Present("r2", Fixture.Blr);

        var after = f.Service.GetRequest(request.Id)!;
        Assert.Equal(RefillStatus.Offered, after.Status);
        Assert.True(after.AssigneeId != first);
        Assert.Equal(0m, after.ReservedAmount);
        Assert.Equal(Outcome.NotAllowed, f.Service.RecordPurchase(request.Id, first, 500m, "late").Outcome);
    }

    [Fact]
    public void Declining_passes_the_refill_straight_to_the_next_person_and_never_back()
    {
        var f = new Fixture();
        f.Present("r1", Fixture.Blr);
        f.Present("r2", Fixture.Blr);
        var request = f.Service.ReportEmpty(Fixture.Blr, "Biscuits").Value!;
        var first = request.AssigneeId!;

        Assert.True(f.Service.Decline(request.Id, first).IsOk);
        var second = f.Service.GetRequest(request.Id)!.AssigneeId!;
        Assert.True(second != first);

        Assert.True(f.Service.Decline(request.Id, second).IsOk);
        var after = f.Service.GetRequest(request.Id)!;
        Assert.Equal(RefillStatus.Escalated, after.Status);      
        Assert.Equal(1, f.Chat.Sent.Count(m => m.To == "coordinators:BLR"));
    }

    [Fact]
    public void An_escalated_refill_is_retried_automatically_when_someone_arrives_and_coordinators_are_told_only_once()
    {
        var f = new Fixture();
        var request = f.Service.ReportEmpty(Fixture.Blr, "Biscuits").Value!;
        Assert.Equal(RefillStatus.Escalated, request.Status);

        f.Service.Tick(); f.Service.Tick(); f.Service.Tick();
        Assert.Equal(1, f.Chat.Sent.Count(m => m.To == "coordinators:BLR"));

        f.Present("r3", Fixture.Blr);

        var after = f.Service.GetRequest(request.Id)!;
        Assert.Equal(RefillStatus.Offered, after.Status);
        Assert.Equal("r3", after.AssigneeId);
    }


    [Fact]
    public void Only_people_physically_at_that_office_today_and_available_are_chosen()
    {
        var f = new Fixture();
        f.Present("r1", Fixture.Oslo);                             
        Assert.True(f.Service.CheckIn("r2", null).IsOk);           
        Assert.True(f.Service.CheckIn("r3", Fixture.Blr, available: false).IsOk);   

        var request = f.Service.ReportEmpty(Fixture.Blr, "Biscuits").Value!;

        Assert.Equal(RefillStatus.Escalated, request.Status);
    }

    [Fact]
    public void A_visitor_from_the_other_office_can_refill_and_the_claim_is_in_the_currency_actually_spent()
    {
        var f = new Fixture();
        f.Present("r1", Fixture.Oslo);  

        var purchase = f.Complete(Fixture.Oslo, "Fruit", 800m, "osl-001");

        Assert.Equal("r1", purchase.EmployeeId);
        Assert.Equal("NOK", purchase.Currency);
        var claim = f.Hr.Claims.Single();
        Assert.Equal("NOK", claim.Currency);  
        Assert.Equal("r1", claim.EmployeeId);
    }

    [Fact]
    public void Presence_is_per_local_day_so_yesterdays_check_in_does_not_count()
    {
        var f = new Fixture();
        f.Present("r1", Fixture.Blr);
        f.Clock.Advance(TimeSpan.FromHours(20));   

        var request = f.Service.ReportEmpty(Fixture.Blr, "Biscuits").Value!;

        Assert.Equal(RefillStatus.Escalated, request.Status);
    }

    [Fact]
    public void Non_volunteers_are_only_drafted_when_the_policy_allows_it()
    {
        var strict = new Fixture();
        strict.Present("n1", Fixture.Blr);
        Assert.Equal(RefillStatus.Escalated, strict.Service.ReportEmpty(Fixture.Blr, "Biscuits").Value!.Status);

        var relaxed = new Fixture(new Policy { AllowNonVolunteerFallback = true });
        relaxed.Present("n1", Fixture.Blr);
        Assert.Equal("n1", relaxed.Service.ReportEmpty(Fixture.Blr, "Biscuits").Value!.AssigneeId);
    }


    [Fact]
    public void The_job_rotates_to_whoever_was_asked_least_recently()
    {
        var f = new Fixture();
        f.Present("r1", Fixture.Blr);
        f.Present("r2", Fixture.Blr);
        f.Present("r3", Fixture.Blr);

        var order = new List<string>();
        for (var i = 1; i <= 6; i++)
        {
            var p = f.Complete(Fixture.Blr, $"Snack {i}", 500m, $"rot-{i}");
            order.Add(p.EmployeeId);
            f.Clock.Advance(TimeSpan.FromMinutes(5));
            f.Service.OnHrStatusChanged(p.ClaimId(), HrClaimStatus.Approved);
            f.Service.OnHrStatusChanged(p.ClaimId(), HrClaimStatus.Paid);
        }

        Assert.Equal(new[] { "r1", "r2", "r3", "r1", "r2", "r3" }, order);
    }

    [Fact]
    public void Someone_still_waiting_for_two_reimbursements_is_not_asked_again_until_HR_pays()
    {
        var f = new Fixture();
        f.Present("r1", Fixture.Blr);

        var a = f.Complete(Fixture.Blr, "Snack A", 500m, "exp-1");
        f.Complete(Fixture.Blr, "Snack B", 500m, "exp-2");

        var third = f.Service.ReportEmpty(Fixture.Blr, "Snack C").Value!;
        Assert.Equal(RefillStatus.Escalated, third.Status);     

        f.Service.OnHrStatusChanged(a.ClaimId(), HrClaimStatus.Approved);
        f.Service.OnHrStatusChanged(a.ClaimId(), HrClaimStatus.Paid);
        f.Service.Tick();

        Assert.Equal("r1", f.Service.GetRequest(third.Id)!.AssigneeId);
    }
}

internal static class PurchaseExtensions
{
    public static string ClaimId(this Purchase p) => p.HrClaimId ?? throw new InvalidOperationException("Claim not submitted");
}
