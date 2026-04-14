# RentalPlatform Backend - Implementation Log and Reference

This document records what has been implemented and hardened in this backend so far, why it was done, and how the current API is expected to behave.  
It is intended as a long-term project memory/reference.

---

## 1) Current Architecture

The repository was transformed from a minimal single-project ASP.NET template into a Clean Architecture-style multi-project solution:

- `src/RentalPlatform.Api`
- `src/RentalPlatform.Application`
- `src/RentalPlatform.Domain`
- `src/RentalPlatform.Infrastructure`

### Solution and Project References

- `Api -> Application`
- `Api -> Infrastructure`
- `Application -> Domain`
- `Infrastructure -> Application`
- `Infrastructure -> Domain`

### Layer Responsibilities

- **Domain**
  - Entities and enums only
  - No EF, no HTTP, no infrastructure concerns
- **Application**
  - DTOs, interfaces/abstractions, use-case/business services, service result patterns
- **Infrastructure**
  - EF Core `AppDbContext`, entity configurations, store implementations, JWT generation, current user extraction, local file storage implementation
- **Api**
  - Controllers, startup wiring, auth middleware configuration, Swagger setup

---

## 2) Core Startup and DI

### API Startup

- `Program.cs` remains clean and minimal
- Registration is delegated through extension methods
- Middleware order:
  - `UseHttpsRedirection()`
  - `UseStaticFiles()`
  - `UseAuthentication()`
  - `UseAuthorization()`
  - `MapControllers()`

### DI

- Infrastructure registrations are centralized in:
  - `src/RentalPlatform.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Services/stores for Auth, Listings, Images, Categories, Bookings, Favorites, Admin moderation are registered there.

---

## 3) Persistence Foundation (EF Core + SQL Server)

### EF Core setup

- `AppDbContext` created and typed correctly with:
  - `DbContextOptions<AppDbContext>`
- SQL Server provider wired via connection string from API settings.
- Entity configurations are split into dedicated classes and applied with:
  - `modelBuilder.ApplyConfigurationsFromAssembly(...)`

### Implemented persistence constraints and relationships

- **Unique constraints**
  - `Users.Email` unique
  - `Favorites(UserId, ListingId)` unique
- **Indexes and typing**
  - String lengths, required fields, decimal precision, and date column types set through entity configurations
- **Delete behaviors**
  - Restrict/cascade configured per relationship to avoid accidental destructive chains

---

## 4) Auth Module (Vertical Slice)

### Implemented

- Register: `POST /api/auth/register`
- Login: `POST /api/auth/login`
- Current user: `GET /api/auth/me`

### Rules enforced

- Duplicate email is rejected
- Passwords are hashed via BCrypt
- `PasswordHash` never appears in response DTOs
- Blocked users cannot login and cannot resolve `/me`
- JWT access token returned with user payload

### JWT details

- Token includes:
  - User id (`NameIdentifier`/`sub`)
  - Email
  - Role
- `CurrentUserContext` safely resolves user id from claims

---

## 5) Listings Public Read Module

### Implemented

- `GET /api/listings`
- `GET /api/listings/{id}`

### Behavior

- Public read returns only `Approved` listings
- Supports filters:
  - `city`
  - `categoryId`
  - `minPrice`
  - `maxPrice`
- Supports paging

### Response contract alignment

- Paged response includes:
  - `page`
  - `pageSize`
  - `totalCount`
  - `hasMore`
  - `items`
- Listing details include:
  - images
  - owner basic info
  - approved booking date ranges (`bookedDateRanges`) for calendar-style UX

---

## 6) Listings Owner Module

### Implemented

- `POST /api/listings`
- `GET /api/listings/mine`

### Rules enforced

- Authenticated users only
- Owner id always comes from current authenticated context
- New listing status always starts as `PendingApproval`
- Category existence validated
- Blocked users cannot create or access owner listing operations
- My listings returns only current owner listings (sorted newest first)

### Response semantics correction

- Listing create no longer uses misleading `CreatedAtAction` to public approved endpoint.
- Returns clean success payload (`200 OK` with create response DTO).

---

## 7) Categories + Image Upload Module

### Implemented

- `GET /api/categories`
- `POST /api/listings/{listingId}/images`

### File storage abstraction

- Application abstraction:
  - `IFileStorageService`
- Infrastructure implementation:
  - `LocalFileStorageService`
  - `LocalFileStorageOptions`

### Upload rules enforced

- Authenticated users only
- Only listing owner can upload
- Multipart form-data supported
- Rejects empty files
- Restricts allowed image types/extensions
- Creates `ListingImage` records
- `SortOrder` is assigned consistently
- First uploaded image becomes primary only when no primary exists yet

### Local storage hardening

- Path normalization ensures configured upload directory stays under `wwwroot`
- File names are randomized (GUID-based)
- Stream position reset support added for safer copying

---

## 8) Bookings Module

### Implemented

- `POST /api/bookings`
- `GET /api/bookings/mine`
- `GET /api/bookings/requests`
- `POST /api/bookings/{id}/approve`
- `POST /api/bookings/{id}/reject`

### Domain/status model

- `Booking` entity + `BookingStatus` enum:
  - `Pending`, `Approved`, `Rejected`, `Cancelled`, `Expired`, `Completed`

### Rules enforced

- Authenticated users only
- Renter cannot book own listing
- Listing must be `Approved`
- Day-based booking model
- Inclusive date range
- Same-day booking = 1 day
- Price calculation:
  - `inclusiveDays * PricePerDay`
- New booking starts as `Pending`
- Expires after 24 hours
- Overlap prevention:
  - check against `Approved` bookings at create time
  - re-check at approval time
- Only listing owner can approve/reject
- Invalid transitions are blocked (`not pending`, expired, etc.)

### Expiration handling

- Expiration is applied safely during reads/actions via store method (`ExpirePendingAsync`) when no background job exists.

### Response semantics correction

- Booking create no longer returns misleading `CreatedAtAction` to collection endpoint.
- Uses clean `200 OK` payload.

---

## 9) Favorites Module

### Implemented

- `GET /api/favorites`
- `POST /api/favorites/{listingId}`
- `DELETE /api/favorites/{listingId}`

### Rules enforced

- Authenticated users only
- Add validates listing existence
- Duplicate favorites prevented by:
  - unique DB constraint (`UserId + ListingId`)
  - race-safe add handling in persistence (`TryAddAsync`)
- Delete is idempotent-friendly

### Response semantics

- Add returns boolean success state via `200 OK` for predictable optimistic frontend flow.
- Delete returns `204 No Content` on success.

---

## 10) Admin Moderation Module

### Implemented

- `GET /api/admin/listings/pending`
- `POST /api/admin/listings/{id}/approve`
- `POST /api/admin/listings/{id}/reject`

### Authorization

- Role support added in domain/user and JWT claims
- Admin controller enforces role policy:
  - `[Authorize(Roles = nameof(UserRole.Admin))]`
- Service additionally validates current user role/blocked status for defense-in-depth

### Moderation rules

- Only `PendingApproval` listings can be approved/rejected
- `UpdatedAt` is refreshed on moderation actions

---

## 11) JWT and Configuration Hardening

### Strongly typed options

- `JwtOptions` used for configuration binding
- Startup validation added (API + Infrastructure level)

### Fail-fast behavior

- Required JWT fields validated:
  - issuer
  - audience
  - secret key
  - token expiration minutes > 0
- Secret length minimum enforced

### Config safety

- Production-like `appsettings.json` no longer keeps a weak placeholder secret value.
- Development file keeps a development-only secret shape so local dev remains workable.

---

## 12) Controller and Error Handling Consistency

General consistency approach applied:

- Controllers remain thin (delegate to services)
- Business logic stays in Application services/stores
- Error mapping based on service error codes
- No internal exception details exposed in API responses
- Status code usage kept consistent:
  - `400` validation/business bad request
  - `401` unauthenticated
  - `403` forbidden
  - `404` not found
  - `409` conflict (e.g., overlaps/state conflicts)

---

## 13) Runtime/Environment Notes

### Running locally

Project run command:

```bash
dotnet run --project "src/RentalPlatform.Api/RentalPlatform.Api.csproj"
```

### What was observed

- Initially `dotnet` CLI was unavailable in PATH in this shell, then resolved by installed location.
- App required .NET 8 runtime; once installed, app started successfully.
- Current startup warnings seen in dev:
  - `wwwroot` missing (static files warning)
  - dev HTTPS cert not trusted

These warnings are non-blocking unless you explicitly need static assets and trusted local HTTPS.

---

## 14) Current API Surface (Implemented)

### Auth
- `POST /api/auth/register`
- `POST /api/auth/login`
- `GET /api/auth/me`

### Listings
- `GET /api/listings`
- `GET /api/listings/{id}`
- `POST /api/listings`
- `GET /api/listings/mine`
- `POST /api/listings/{listingId}/images`

### Categories
- `GET /api/categories`

### Bookings
- `POST /api/bookings`
- `GET /api/bookings/mine`
- `GET /api/bookings/requests`
- `POST /api/bookings/{id}/approve`
- `POST /api/bookings/{id}/reject`

### Favorites
- `GET /api/favorites`
- `POST /api/favorites/{listingId}`
- `DELETE /api/favorites/{listingId}`

### Admin Listings Moderation
- `GET /api/admin/listings/pending`
- `POST /api/admin/listings/{id}/approve`
- `POST /api/admin/listings/{id}/reject`

---

## 15) Scope Guardrails Followed

During implementation/hardening:

- No architecture rewrite
- Controllers + EF Core retained
- No in-memory fake persistence
- No unnecessary generic repository introduction
- No migration to Minimal APIs
- Changes focused on correctness, consistency, and integration safety

