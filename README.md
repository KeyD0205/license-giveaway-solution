# Free License Give-Away — Interview Solution

This solution is intentionally small and interview-focused. It covers:

- Part A: system design in `docs/system-design.md`
- Part B: C# ASP.NET Core minimal API in `src/LicenseGiveaway.Api/Program.cs`
- Unit tests for the core allocation logic in `tests/LicenseGiveaway.Tests/LicenseAllocatorTests.cs`

## Design assumptions

The production design uses:
- stateless API instances behind a load balancer/WAF
- a durable queue to absorb the opening burst and preserve server-side acceptance order
- a transactional database as the source of truth
- unique constraints on normalized email and phone
- atomic license allocation so exactly 1,000 can be issued
- asynchronous PDF generation
- private object storage and authenticated/short-lived access to PDFs

The coding exercise deliberately uses in-memory state and a lock because the brief explicitly permits that and says the implementation need not be production-grade.

## Run

Requires .NET 8 SDK.

```bash
dotnet test
dotnet run --project src/LicenseGiveaway.Api
```

Then POST to `/applications`:

```json
{
  "email": "alice@example.com",
  "phone": "+358401234567"
}
```

The PDF generation and queue are represented by simple stubs, as requested by the exercise.
