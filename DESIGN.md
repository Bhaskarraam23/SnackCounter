# Snack Counter Automation: Design

## 1. What "automate" means here
The pain is coordination, not shopping. So the system automates **who buys, when, and whether money is available**, and hands the paperwork (receipt, claim) to HR. It does not buy snacks, detect empty boxes by itself, or approve anything. A person reports "empty" (chat command or button); everything after that is automatic.

## 2. A refill from "empty" to "reimbursed"
1. **Report.** Someone reports "Biscuits empty at Oslo". If an open request for that office and item already exists, it is returned instead of creating a second one (stops two people buying on the same day).
2. **Offer.** The system picks **one** person: checked in at that office today (office-local date), marked available, volunteer, not already tried for this request, not waiting on 2+ unpaid reimbursements. Order: fewest offers in the last 30 days, then longest since last offer. Chat message goes to that person only.
3. **Accept (30 min).** Silence, or a decline, moves the offer to the next person. Nobody is asked twice for the same request. If nobody is left, the request is **Escalated**: coordinators are told once, and it is retried automatically when someone checks in or a budget frees up.
4. **Commit (24 h).** Accepting **reserves** up to the per-refill cap from the office's monthly budget. If the headroom is not there, nobody is asked at all; the request escalates.
5. **Purchase.** The volunteer submits amount + receipt reference. Rules: only the committed person; amount > 0 and <= cap; receipt not used before. Missing the 24 h deadline releases the reservation and hands the request to someone else.
6. **Claim.** The system creates the HR claim (amount, currency spent, description, receipt, employee id) with an idempotency key. If HR is down, the purchase is kept as `PendingSubmission` and retried.
7. **Reimbursed.** HR statuses `Submitted -> Approved -> Paid` (or `Rejected`) come back as callbacks; illegal jumps are refused. A rejected claim stops counting against the budget.

## 3. Parts and the data each keeps
| Part | Owns |
|---|---|
| **RefillService** (all rules) | Requests (office, item, status, assignee, deadlines, reserved amount, who was already tried), offer history (fairness), today's check-ins, purchases (amount, currency, receipt, HR claim id/status) |
| **IChatClient** (stand-in: `FakeChatClient`) | nothing; sends messages |
| **IHrClient** (stand-in: `FakeHrClient`, idempotent, can fail) | HR's own claims |
| **MonthlyReport** (read-only) | nothing; computes from purchases |
| **Policy / OfficeConfig** | windows (30 min / 24 h), fairness window, unpaid-claim limit, budget, currency, per-refill cap, time zone |

Time comes from an injected `TimeProvider`. Nothing calls `DateTime.Now`, so every deadline is testable.

## 4. How each stated problem is prevented
- **Box empty for days.** The system picks a person and chases: 30 min to accept, then the next person, then coordinator escalation, with automatic retry.
- **Two people buy the same day.** One open request per office+item, and one offer at a time. Only the offered person can accept, and a double tap changes nothing.
- **A few people do everything / wait weeks for money.** Fair rotation, plus nobody with 2 unpaid claims is asked again. Out-of-pocket exposure is bounded.
- **Nobody knows spend / what is eaten.** Every purchase is stored per office in its own currency. The monthly report shows per-office budget/spent/remaining and spend by item.
- **Hybrid, holidays, visitors.** Presence is a daily check-in per office-local date. A visitor checks in at the other office and is eligible there. Their claim is in the currency actually spent; HR converts to payroll currency.
- **Budget.** Checked when asking, reserved when committing, and re-checked at purchase. Oslo (6 000 NOK, cap 1 200) and Bengaluru (15 000 INR, cap 3 000) cannot go over, and nobody spends money that would then be refused.

## 5. Management report
One report, per-office lines in their own currency, plus a total in a reporting currency **only if Finance supplied a rate** for each currency; otherwise `Total = null` and the missing currencies are listed. Months are the office's local months. A guessed exchange rate in a finance report is worse than no total.

## 6. Trade-offs, and what I deliberately did not build
- **Single-process lock, in-memory state.** The invariants are simple to read and test. Production moves them to a database: a unique index on open (office, item), and a conditional update for each status change.
- **Reserve a fixed cap, not an estimate.** Slightly conservative (a 300 NOK trip reserves 1 200) but simple, and it can never overspend.
- **Offer one person at a time** (slower than broadcasting) but it makes "two people commit" impossible by construction.
- **Not built:** the web API and auth (the identity would come from the company login, never a request parameter), real chat/HR adapters, receipt image upload and OCR, automatic empty-box detection, holiday/leave calendar integration, an exchange-rate feed, a coordinator UI.
- `Tick()` must be called by a timer (e.g. every minute). Correctness does not depend on it, because deadlines are also applied at the start of every operation.

## 7. Assumptions to confirm (I would have asked)
30 min / 24 h windows; per-refill cap = top of the "typical purchase" range; 2 unpaid claims limit; non-volunteers are never drafted; who the coordinators are; who reports "empty"; whether over-cap purchases should be approved manually.
