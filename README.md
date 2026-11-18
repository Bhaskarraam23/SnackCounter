# Snack Counter Automation

    dotnet test            # runs 28 tests (xunit)

* `SnackCounter.Core`  the logic (no dependencies): `RefillService`, `MonthlyReport`, `IChatClient`/`IHrClient` + simulated versions
* `SnackCounter.Tests` the tests
* `DESIGN.md`          the design document

Targets net8.0; change `TargetFramework` to net10.0 in both csproj files if you prefer, nothing else changes.

Wiring a timer (real host): `new PeriodicTimer(TimeSpan.FromMinutes(1))` in a `BackgroundService` that calls `service.Tick()`.
