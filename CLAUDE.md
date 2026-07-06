# rental-api — Backend Rules

ASP.NET Core 8, Clean Architecture, EF Core 8 + SQL Server, JWT auth (BCrypt passwords, optional Google/Apple).

## Layering (hard rules)

- **Domain** → references nothing. POCO entities + enums only.
- **Application** → references Domain only. DTOs, abstractions (`IAuthService`, stores, `IJwtTokenService`, `ICurrentUserContext`, `IEmailService`…), application services with business rules, `ServiceResult`/`ServiceError`.
- **Infrastructure** → references Application + Domain. `AppDbContext`, `IEntityTypeConfiguration<T>` configurations, store implementations, query services, JWT, file storage, dev seed.
- **Api** → wiring only. Controllers map HTTP ⇄ application services; no business logic in controllers (DTO validation attributes at the boundary are OK).
- **EF Core is used ONLY in Infrastructure.** Never in Domain/Application/Api.

## Patterns to follow

- Business rules return `ServiceResult` with `ServiceError` codes (e.g. `booking.not_pending`, `admin.invalid_listing_status`) — controllers translate them to HTTP codes.
- Ownership/authorization checks live in application services (defense in depth: admin endpoints also use `[Authorize(Roles = "Admin")]`).
- New Listing/Booking fields: **nullable and additive** — the public contract stays backward compatible.
- `IEmailService` never throws; notification failures never roll back the main operation.
- Booking overlap: only **Approved** bookings block a range; overlap is re-checked at approve time. Composite index `(ListingId, Status, StartDate, EndDate)`.
- Pending booking expiry (`ExpirePendingAsync`) runs opportunistically inside booking operations — there is no background job.
- Dev seed (`Infrastructure/DependencyInjection/DevelopmentSeed/`) is idempotent and additive; runs only in Development.

## Migrations

```bash
dotnet ef migrations add <Name> --project src/RentalPlatform.Infrastructure/RentalPlatform.Infrastructure.csproj --startup-project src/RentalPlatform.Api/RentalPlatform.Api.csproj
dotnet ef database update  --project src/RentalPlatform.Infrastructure/RentalPlatform.Infrastructure.csproj --startup-project src/RentalPlatform.Api/RentalPlatform.Api.csproj
```

Every generated migration gets human review before merge.

## After any change

`dotnet build RentalPlatform.sln && dotnet test RentalPlatform.sln`. If a DTO or route changed, state it explicitly in your report — `contract-guardian` must sync the frontend contract.
