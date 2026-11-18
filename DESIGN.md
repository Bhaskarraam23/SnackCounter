# Snack Counter Automation: Design

## 1. What "automate" means here
The pain is coordination, not shopping. So the system automates **who buys, when, and whether money is available**, and hands the paperwork (receipt, claim) to HR. It does not buy snacks, detect empty boxes by itself, or approve anything. A person reports "empty" (in this prototype a call to `ReportEmpty`; a real front end would be a chat command or button), and everything after that is automatic.

## 2. A refill from "empty" to "reimbursed"
1. **Report.** Someone reports "Biscuits empty at Oslo". If an open request for that office and item already exists, it is returned instead of creating a second one (stops two people buying on the same day).
2. **Offer.** The system picks **one** person: checked in at that office today (office-local date), marked available, a volunteer, not already tried for this request, and not waiting on 2 or more unpaid reimbursements. Order: fewest offers in the last 30 days, then longest since last offer. The chat message goes to that person only.
3. **Accept (30 min).** Silence or a decline moves the offer to the next person. Nobody is asked twice for the same request. If nobody is left, the request is **Escalated**: coordinators are told once, and it is retried automatically when someone checks in or on `Tick()`.
4. **Commit (24 h).** Accepting **reserves** up to the per-refill cap from the office's monthly budget. If the headroom is not there, nobody is asked at all and the request escalates.
5. **Purchase.** The volunteer submits amount and receipt reference. Rules: only the committed person; amount above 0 and at most the cap; receipt not used before. Missing the 24 h deadline releases the reservation and hands the request to someone else.
6. **Claim.** The system creates the HR claim (amount, currency spent, description, receipt, employee id) with an idempotency key. If HR is down, the purchase is kept as `PendingSubmission` and retried.
7. **Reimbursed.** HR statuses `Submitted -> Approved -> Paid` (or `Rejected`) come back as callbacks, and illegal jumps are refused. A rejected claim stops counting against the budget.

## 3. Parts and the data each keeps
| Part | Owns |
|---|---|
| **RefillService** (all rules) | Requests (office, item, status, assignee, deadlines, reserved amount, who was already tried), offer history (fairness), today's check-ins, purchases (amount, currency, receipt, HR claim id and status) |
| **IChatClient** (stand-in: `MockChatClient`) | Nothing; sends messages |
| **IHrClient** (stand-in: `MockHrClient`, idempotent, can simulate downtime) | HR's own claims |
| **MonthlyReport** (read-only) | Nothing; computes from purchases |
| **Policy / OfficeConfig** | Windows (30 min / 24 h), fairness window, unpaid-claim limit, budget, currency, per-refill cap, time zone |

Time comes from an injected `TimeProvider`. Nothing calls `DateTime.Now`, so every deadline is testable.

## 4. How each stated problem is prevented
- **Box empty for days.** The system picks a person and chases: 30 min to accept, then the next person, then coordinator escalation, with automatic retry.
- **Two people buy the same day.** One open request per office and item, and one offer at a time. Only the offered person can accept, and a double tap changes nothing.
- **A few people do everything / wait weeks for money.** Fair rotation, and nobody with 2 unpaid claims is asked again, so out-of-pocket exposure is bounded.
- **Nobody knows spend or what is eaten.** Every purchase is stored per office in its own currency. The monthly report shows per-office budget, spent and remaining, plus spend by item.
- **Hybrid work, holidays, visitors.** Presence is a daily check-in per office-local date. A visitor checks in at the other office and is eligible there. Their claim is in the currency actually spent, and HR converts it to payroll currency.
- **Budget.** Checked when asking, reserved when committing, and re-checked at purchase. Oslo (6,000 NOK, cap 1,200) and Bengaluru (15,000 INR, cap 3,000) cannot go over, and nobody spends money that would then be refused.

## 5. Management report
One report, with per-office lines in their own currency, plus a total in a reporting currency **only if Finance supplied a rate** for each currency. Otherwise `Total` is null and the missing currencies are listed. Months are the office's local months, and HR-rejected claims are excluded. A guessed exchange rate in a finance report is worse than no total.

## 6. Testing
28 xUnit tests cover: duplicate reports (including 50 simultaneous), who may commit (including a race between two people), offer and commit expiry with a fake clock, decline and escalation, presence and visitors, fairness and the unpaid-claim limit, budgets in both currencies, per-refill cap, duplicate receipts, HR downtime and retry, legal HR transitions, and the monthly report.

## 7. Trade-offs, and what I deliberately did not build
- **Single-process lock, in-memory state.** The rules are simple to read and test. Production moves them to a database: a unique index on open (office, item), and a conditional update for each status change.
- **Reserve a fixed cap, not an estimate.** Slightly conservative (a 300 NOK trip reserves 1,200), but simple, and it can never overspend.
- **Offer one person at a time.** Slower than broadcasting, but it makes "two people commit" impossible by construction.
- **Non-volunteers are not asked** unless the policy flag `AllowNonVolunteerFallback` is turned on, because spending their own money is a policy decision.
- **Not built:** web API and authentication (identity would come from the company login, never a request parameter), real chat and HR adapters, receipt image upload and OCR, automatic empty-box detection, holiday and leave calendar integration, an exchange-rate feed, a coordinator UI, and a scheduled background service.
- `Tick()` must be called by a timer (for example a `BackgroundService` with a `PeriodicTimer`, about once a minute). It is not scheduled in this prototype. Correctness does not depend on it, because deadlines are also applied at the start of every operation.

## 8. Assumptions to confirm (I would have asked)
The 30 min and 24 h windows; the per-refill cap as the top of the "typical purchase" range; the limit of 2 unpaid claims; that non-volunteers are never drafted; who the coordinators are; who reports "empty"; whether over-cap purchases should be approved manually.