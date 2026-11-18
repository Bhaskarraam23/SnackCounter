# Snack Counter Automation

## Overview

Snack Counter Automation is a lightweight .NET prototype that simplifies snack refill coordination across the Oslo and Bengaluru offices.

It automates **who buys, when, and whether budget is available**: volunteer selection, refill assignment with deadlines, budget reservation, purchase tracking and simulated HR reimbursement. It does not buy snacks or detect empty boxes. A person reports "empty", and the workflow runs from there.

## Technology Stack

- C#, .NET 10
- xUnit
- In-memory application state
- Interface-based integrations (chat and HR)

## Project Structure

```text
SnackCounter.slnx
├── SnackCounter.Core/            Business logic, no external dependencies
│   ├── Domain.cs                 Statuses, offices, employees, requests, purchases, policy
│   ├── Abstractions.cs           IChatClient, IHrClient
│   ├── Simulated.cs              MockChatClient, MockHrClient (simulated integrations)
│   ├── RefillService.cs          Refill lifecycle, volunteer selection, budget rules
│   └── MonthlyReport.cs          Monthly spending report
├── SnackCounter.Tests/           Automated xUnit tests
├── DESIGN.md                     Design document
└── README.md
```

## Getting Started

**Prerequisite:** .NET 10 SDK

```bash
dotnet restore
dotnet build
dotnet test
```

This is a class library with tests and **has no executable**, so there is nothing to start with a Run button. Behaviour is verified through the automated tests. The suite contains 28 xUnit tests covering the lifecycle, concurrency, deadlines, fairness, budgets in both currencies, HR handling and the monthly report.

## Implementation Notes

- Business logic is separated into a core library.
- Chat and HR integrations are simulated with `MockChatClient` and `MockHrClient` behind `IChatClient` and `IHrClient`. The mock HR client is idempotent and can simulate downtime.
- State is in memory, protected by a single lock, which is valid for one process only.
- An injected `TimeProvider` makes deadlines testable without waiting.
- `Tick()` processes expired offers and commitments, retries stuck requests and resends HR claims that failed. **Scheduling is not implemented in this prototype**. In production, a .NET `BackgroundService` with a `PeriodicTimer` would call it about once a minute. Deadlines are also applied at the start of every operation, so correctness does not depend on the timer.
- Report totals across currencies are produced only when an exchange rate is supplied. Otherwise the total is empty and the missing currencies are listed.

## Assumptions to Confirm

The 30-minute acceptance window, the 24-hour purchase window, the per-refill cap (1,200 NOK / 3,000 INR), the limit of two unpaid reimbursements per person, and that non-volunteers are not asked, are assumptions to confirm with management.

## Not Implemented

Web API and authentication, database persistence, real chat and HR integrations, receipt image upload, automatic empty-box detection, leave calendar integration, exchange-rate feed, a scheduled background service and a coordinator interface.

## AI Tools Used

- **OpenAI ChatGPT:** requirement analysis, architecture discussions, implementation guidance, code review and documentation.
- **Claude:** coding assistant used to help implement and refine the business logic and automated tests.

AI assistance was used for drafting and refinement. I reviewed the code, ran the test suite and made the final decisions on design and scope.

## Design Documentation

See [DESIGN.md](DESIGN.md) for the workflow, parts and data, how each problem is prevented, trade-offs, assumptions and future improvements.

---

**Framework:** .NET 10  
**Architecture:** Modular core library  
**Testing:** xUnit