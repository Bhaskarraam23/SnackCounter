namespace SnackCounter.Core;

public sealed record OfficeLine(string OfficeId, string OfficeName, string Currency, decimal Budget, decimal Spent, decimal Remaining, int PurchaseCount);
public sealed record ItemLine(string OfficeId, string Currency, string Item, int Purchases, decimal Spent);

public sealed record ManagementReport(
    int Year,
    int Month,
    IReadOnlyList<OfficeLine> Offices,
    IReadOnlyList<ItemLine> Items,                 
    string ReportingCurrency,
    decimal? Total,                               
    IReadOnlyList<string> MissingRates);

public static class MonthlyReport
{
    public static ManagementReport Build(RefillService service, int year, int month,string reportingCurrency, IReadOnlyDictionary<string, decimal> ratesToReporting)
    {
        if (month is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(month));
        var offices = service.Offices;
        var purchases = service.Purchases;
        var lines = new List<OfficeLine>();
        var items = new List<ItemLine>();
        foreach (var o in offices)
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(o.TimeZoneId);
            var inMonth = purchases
                .Where(p => p.OfficeId == o.Id && p.ClaimStatus != HrClaimStatus.Rejected)
                .Where(p =>
                {
                    var local = TimeZoneInfo.ConvertTime(p.PurchasedAt, tz);
                    return local.Year == year && local.Month == month;
                })
                .ToList();

            var spent = inMonth.Sum(p => p.Amount);
            lines.Add(new OfficeLine(o.Id, o.Name, o.Currency, o.MonthlyBudget, spent, o.MonthlyBudget - spent, inMonth.Count));

            items.AddRange(inMonth
                .GroupBy(p => p.Item.Trim().ToLowerInvariant())
                .Select(g => new ItemLine(o.Id, o.Currency, g.First().Item, g.Count(), g.Sum(p => p.Amount)))
                .OrderByDescending(i => i.Spent));
        }

        var missing = lines.Select(l => l.Currency).Distinct().Where(c => c != reportingCurrency && !ratesToReporting.ContainsKey(c)).ToList();
        decimal? total = null;
        if (missing.Count == 0)
        {
            total = Math.Round(lines.Sum(l =>
                l.Currency == reportingCurrency ? l.Spent : l.Spent * ratesToReporting[l.Currency]),
                2, MidpointRounding.AwayFromZero);
        }
        return new ManagementReport(year, month, lines, items, reportingCurrency, total, missing);
    }
}
