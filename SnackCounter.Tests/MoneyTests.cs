using SnackCounter.Core;

namespace SnackCounter.Tests;

public class MoneyTests
{
    private static Fixture NoExposureLimit() => new(new Policy { MaxUnpaidClaimsPerEmployee = 100 });


    [Fact]
    public void Oslo_budget_of_6000_NOK_is_never_exceeded_and_nobody_is_asked_to_spend_money_that_is_not_there()
    {
        var f = NoExposureLimit();
        f.Present("o1", Fixture.Oslo);

        for (var i = 1; i <= 5; i++) f.Complete(Fixture.Oslo, $"Snack {i}", 1_200m, $"nok-{i}");   
        Assert.Equal(6_000m, f.Service.SpentThisMonth(Fixture.Oslo));

        var sixth = f.Service.ReportEmpty(Fixture.Oslo, "Snack 6").Value!;
        Assert.Equal(RefillStatus.Escalated, sixth.Status);     
    }

    [Fact]
    public void Bengaluru_budget_of_15000_INR_is_never_exceeded()
    {
        var f = NoExposureLimit();
        f.Present("r1", Fixture.Blr);

        for (var i = 1; i <= 5; i++) f.Complete(Fixture.Blr, $"Snack {i}", 3_000m, $"inr-{i}");   
        Assert.Equal(15_000m, f.Service.SpentThisMonth(Fixture.Blr));

        Assert.Equal(RefillStatus.Escalated, f.Service.ReportEmpty(Fixture.Blr, "Snack 6").Value!.Status);
    }

    [Fact]
    public void Money_is_reserved_when_someone_commits_so_two_committed_people_cannot_overspend_together()
    {
        var f = NoExposureLimit();
        f.Present("o1", Fixture.Oslo);
        f.Present("o2", Fixture.Oslo);

        for (var i = 1; i <= 3; i++) f.Complete(Fixture.Oslo, $"Snack {i}", 1_200m, $"r-{i}");   

        var a = f.Service.ReportEmpty(Fixture.Oslo, "A").Value!;
        var b = f.Service.ReportEmpty(Fixture.Oslo, "B").Value!;
        Assert.True(f.Service.Accept(a.Id, a.AssigneeId!).IsOk);     
        Assert.True(f.Service.Accept(b.Id, b.AssigneeId!).IsOk);     
        var c = f.Service.ReportEmpty(Fixture.Oslo, "C").Value!;

        Assert.Equal(RefillStatus.Escalated, c.Status);              
    }

    [Fact]
    public void A_purchase_over_the_per_refill_cap_is_refused_but_the_commitment_stays_open_for_a_valid_receipt()
    {
        var f = new Fixture();
        f.Present("o1", Fixture.Oslo);
        var request = f.Service.ReportEmpty(Fixture.Oslo, "Fruit").Value!;
        f.Service.Accept(request.Id, "o1");

        Assert.Equal(Outcome.OverPurchaseCap, f.Service.RecordPurchase(request.Id, "o1", 1_201m, "big").Outcome);
        Assert.Equal(Outcome.InvalidAmount, f.Service.RecordPurchase(request.Id, "o1", 0m, "zero").Outcome);
        Assert.Equal(Outcome.MissingReceipt, f.Service.RecordPurchase(request.Id, "o1", 500m, "  ").Outcome);
        Assert.Equal(RefillStatus.Committed, f.Service.GetRequest(request.Id)!.Status);
        Assert.True(f.Service.RecordPurchase(request.Id, "o1", 1_200m, "ok").IsOk);
    }

    [Fact]
    public void A_claim_HR_rejects_no_longer_counts_against_the_budget_and_stuck_requests_resume()
    {
        var f = NoExposureLimit();
        f.Present("o1", Fixture.Oslo);

        var purchases = new List<Purchase>();
        for (var i = 1; i <= 5; i++) purchases.Add(f.Complete(Fixture.Oslo, $"Snack {i}", 1_200m, $"x-{i}"));
        var stuck = f.Service.ReportEmpty(Fixture.Oslo, "Snack 6").Value!;
        Assert.Equal(RefillStatus.Escalated, stuck.Status);

        Assert.True(f.Service.OnHrStatusChanged(purchases[0].ClaimId(), HrClaimStatus.Rejected).IsOk);
        f.Service.Tick();

        Assert.Equal(4_800m, f.Service.SpentThisMonth(Fixture.Oslo));
        Assert.Equal(RefillStatus.Offered, f.Service.GetRequest(stuck.Id)!.Status);
    }

    [Fact]
    public void The_budget_resets_on_the_first_of_the_month_in_the_offices_own_time_zone()
    {
        var f = NoExposureLimit();
        f.Present("o1", Fixture.Oslo);
        for (var i = 1; i <= 5; i++) f.Complete(Fixture.Oslo, $"Snack {i}", 1_200m, $"m-{i}");

        f.Clock.Set(new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero));
        f.Present("o1", Fixture.Oslo);

        Assert.Equal(0m, f.Service.SpentThisMonth(Fixture.Oslo));
        Assert.Equal(RefillStatus.Offered, f.Service.ReportEmpty(Fixture.Oslo, "December snack").Value!.Status);
    }


    [Fact]
    public void The_same_receipt_cannot_be_claimed_twice()
    {
        var f = NoExposureLimit();
        f.Present("r1", Fixture.Blr);
        f.Complete(Fixture.Blr, "Biscuits", 500m, "BILL-42");

        var second = f.Service.ReportEmpty(Fixture.Blr, "Chips").Value!;
        f.Service.Accept(second.Id, second.AssigneeId!);

        Assert.Equal(Outcome.DuplicateReceipt, f.Service.RecordPurchase(second.Id, second.AssigneeId!, 500m, "bill-42").Outcome);
        Assert.Single(f.Hr.Claims);
    }

    [Fact]
    public void If_HR_is_down_the_purchase_is_kept_and_submitted_later_exactly_once()
    {
        var f = new Fixture();
        f.Present("r1", Fixture.Blr);
        f.Hr.FailNextCalls = 2;

        var purchase = f.Complete(Fixture.Blr, "Biscuits", 500m, "hr-down");
        Assert.Equal(HrClaimStatus.PendingSubmission, purchase.ClaimStatus);
        Assert.Empty(f.Hr.Claims);

        f.Service.Tick();   
        Assert.Equal(HrClaimStatus.PendingSubmission, purchase.ClaimStatus);

        f.Service.Tick();   
        Assert.Equal(HrClaimStatus.Submitted, purchase.ClaimStatus);
        Assert.Single(f.Hr.Claims);

        var calls = f.Hr.CallCount;
        f.Service.Tick();   
        Assert.Equal(calls, f.Hr.CallCount);
        Assert.Single(f.Hr.Claims);
    }

    [Fact]
    public void HR_claim_carries_amount_currency_description_receipt_and_employee()
    {
        var f = new Fixture();
        f.Present("o2", Fixture.Oslo);
        f.Complete(Fixture.Oslo, "Fruit", 640m, "rcpt-9");

        var claim = f.Hr.Claims.Single();
        Assert.Equal(640m, claim.Amount);
        Assert.Equal("NOK", claim.Currency);
        Assert.Equal("rcpt-9", claim.ReceiptId);
        Assert.Equal("o2", claim.EmployeeId);
        Assert.True(claim.Description.Contains("Fruit"));
    }

    [Fact]
    public void HR_status_can_only_move_forward_along_legal_transitions()
    {
        var f = new Fixture();
        f.Present("r1", Fixture.Blr);
        var p = f.Complete(Fixture.Blr, "Biscuits", 500m, "t-1");

        Assert.Equal(Outcome.InvalidTransition, f.Service.OnHrStatusChanged(p.ClaimId(), HrClaimStatus.Paid).Outcome);      // skipped approval
        Assert.True(f.Service.OnHrStatusChanged(p.ClaimId(), HrClaimStatus.Approved).IsOk);
        Assert.True(f.Service.OnHrStatusChanged(p.ClaimId(), HrClaimStatus.Paid).IsOk);
        Assert.Equal(Outcome.InvalidTransition, f.Service.OnHrStatusChanged(p.ClaimId(), HrClaimStatus.Submitted).Outcome); // paid is final
        Assert.Equal(Outcome.NotFound, f.Service.OnHrStatusChanged("HR-NOPE", HrClaimStatus.Approved).Outcome);
    }


    [Fact]
    public void Report_keeps_each_currency_separate_and_converts_the_total_with_the_rate_Finance_supplies()
    {
        var f = NoExposureLimit();
        f.Present("o1", Fixture.Oslo);
        f.Present("r1", Fixture.Blr);
        f.Complete(Fixture.Oslo, "Fruit", 1_000m, "a-1");
        f.Complete(Fixture.Blr, "Biscuits", 2_500m, "b-1");

        var rates = new Dictionary<string, decimal> { ["INR"] = 0.12m };    
        var report = MonthlyReport.Build(f.Service, 2026, 11, "NOK", rates);

        var oslo = report.Offices.Single(o => o.OfficeId == Fixture.Oslo);
        var blr = report.Offices.Single(o => o.OfficeId == Fixture.Blr);
        Assert.Equal(1_000m, oslo.Spent);   Assert.Equal(5_000m, oslo.Remaining);
        Assert.Equal(2_500m, blr.Spent);    Assert.Equal(12_500m, blr.Remaining);
        Assert.Equal(1_300.00m, report.Total);    
        Assert.Equal(0, report.MissingRates.Count);
    }

    [Fact]
    public void Report_refuses_to_guess_a_total_when_a_rate_is_missing()
    {
        var f = new Fixture();
        f.Present("r1", Fixture.Blr);
        f.Complete(Fixture.Blr, "Biscuits", 2_500m, "b-1");

        var report = MonthlyReport.Build(f.Service, 2026, 11, "NOK", new Dictionary<string, decimal>());

        Assert.Null(report.Total);
        Assert.Equal(1, report.MissingRates.Count);
        Assert.Equal("INR", report.MissingRates[0]);
        Assert.Equal(2_500m, report.Offices.Single(o => o.OfficeId == Fixture.Blr).Spent);   
    }

    [Fact]
    public void Report_excludes_rejected_claims_and_counts_a_purchase_in_the_month_of_its_own_office()
    {
        var f = NoExposureLimit();
        f.Present("r1", Fixture.Blr);
        var kept = f.Complete(Fixture.Blr, "Biscuits", 1_000m, "k-1");
        var rejected = f.Complete(Fixture.Blr, "Chips", 700m, "k-2");
        f.Service.OnHrStatusChanged(rejected.ClaimId(), HrClaimStatus.Rejected);

        f.Clock.Set(new DateTimeOffset(2026, 11, 30, 19, 0, 0, TimeSpan.Zero));
        f.Present("r1", Fixture.Blr);
        f.Complete(Fixture.Blr, "Late snack", 400m, "k-3");

        var rates = new Dictionary<string, decimal> { ["INR"] = 0.12m };
        var nov = MonthlyReport.Build(f.Service, 2026, 11, "NOK", rates).Offices.Single(o => o.OfficeId == Fixture.Blr);
        var dec = MonthlyReport.Build(f.Service, 2026, 12, "NOK", rates).Offices.Single(o => o.OfficeId == Fixture.Blr);

        Assert.Equal(kept.Amount, nov.Spent);   
        Assert.Equal(400m, dec.Spent);          
    }

    [Fact]
    public void Report_shows_what_is_bought_most_per_office()
    {
        var f = NoExposureLimit();
        f.Present("r1", Fixture.Blr);
        f.Complete(Fixture.Blr, "Biscuits", 500m, "i-1");
        f.Complete(Fixture.Blr, "biscuits", 600m, "i-2");
        f.Complete(Fixture.Blr, "Fruit", 300m, "i-3");

        var report = MonthlyReport.Build(f.Service, 2026, 11, "INR", new Dictionary<string, decimal>());
        var top = report.Items.First();

        Assert.Equal("Biscuits", top.Item);
        Assert.Equal(2, top.Purchases);
        Assert.Equal(1_100m, top.Spent);
    }
}
