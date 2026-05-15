# Child Toys Rental Backend

This document describes the **current** backend as implemented in this repository. The product is a **child toys rental MVP**: parents in Armenia/Yerevan list children's toys, admins moderate them, other parents browse approved toys and request short-term rentals. This document is derived from the codebase only; anything not present in code is called out explicitly under **Known gaps**.

> **MVP niche**: child toys rental. Internally the backend still uses a generic `Listing` entity and keeps `/api/listings` routes for API contract stability; the UI can present listings as "toys". The toy focus is encoded in the **categories**, the **seed data**, and a small set of **optional toy-specific fields** on each listing (age range, condition, hygiene/safety notes, deposit).

---

## A. Project Overview

The backend is an **ASP.NET Core Web API** for the child toys rental MVP. It exposes JSON REST endpoints for:

- User registration and login (email/password)
- Optional sign-in with **Google** or **Apple** identity tokens (validated server-side, non-MVP / postponed)
- JWT-based API authentication
- Public browsing of **approved** toy listings and toy categories
- Authenticated owners creating toy listings (submitted as **pending approval**), viewing their own listings, and uploading listing images to **local disk** under `wwwroot`
- Renters (parents) creating **booking requests** for approved listings; listing owners approving or rejecting **pending** requests (with overlap and 24-hour expiry rules)
- **Admin** users moderating **pending** listings (approve/reject) — required for trust because child toys involve parents and safety
- Users managing **favorites** (non-MVP, kept stable, not expanded)

### Core MVP business loop

1. A user registers / logs in.
2. The user creates a toy listing (status starts as `PendingApproval`).
3. An admin approves the listing (or rejects it).
4. Public users browse approved toys (filterable by city, category, price).
5. A renter requests a booking for an approved toy (status starts as `Pending`, expires in 24 hours).
6. The owner approves or rejects the booking; approval is rejected if any overlap with another approved booking exists.

There is **no** separate BFF, GraphQL, or SignalR layer in this solution.

---

## B. Tech Stack

| Area | Technology |
|------|-------------|
| Runtime | **.NET 8** (`net8.0`) |
| Web framework | **ASP.NET Core** (minimal hosting + controllers) |
| ORM | **Entity Framework Core 8** with **SQL Server** |
| Auth | **JWT Bearer** (`Microsoft.AspNetCore.Authentication.JwtBearer`), symmetric signing |
| Password hashing | **BCrypt** via `BCrypt.Net-Next` |
| External identity | **Google.Apis.Auth** (`GoogleJsonWebSignature`) for Google; **Microsoft.IdentityModel.Tokens** + JWKS fetch for Apple |
| API docs | **Swashbuckle** (Swagger / OpenAPI), enabled in **Development** only |
| OpenAPI metadata | **Microsoft.AspNetCore.OpenApi** package reference exists on the API project |

---

## C. Architecture

### Clean-style layering

| Project | Responsibility |
|---------|----------------|
| **RentalPlatform.Domain** | Entities (`User`, `Category`, `Listing`, `ListingImage`, `Booking`, `Favorite`) and enums (`UserRole`, `ListingStatus`, `BookingStatus`). No infrastructure references. |
| **RentalPlatform.Application** | DTOs, `ServiceResult` / `ServiceError`, abstractions (`IAuthService`, stores, `IJwtTokenService`, etc.), and **application services** that implement use cases (e.g. `AuthService`, `BookingsService`). |
| **RentalPlatform.Infrastructure** | EF Core `AppDbContext`, Fluent API **entity configurations**, **store** implementations, `JwtTokenService`, `CurrentUserContext`, `LocalFileStorageService`, `ExternalIdentityTokenValidator`, query services registered here (e.g. `ListingsQueryService`, `CategoriesQueryService`), **development seed** extension. |
| **RentalPlatform.Api** | `Program.cs`, `Controllers`, API-specific `Extensions` (service registration, JWT options validation at startup, CORS, Swagger). |

### Dependency direction

- **Api** references **Application** and **Infrastructure** (wiring).
- **Application** references **Domain** only.
- **Infrastructure** references **Application** and **Domain**.

### Interaction pattern

- Controllers depend on **Application** abstractions (`IAuthService`, `IBookingsService`, …).
- Application services depend on **abstractions** implemented in Infrastructure (stores, `IJwtTokenService`, `ICurrentUserContext`, …).
- **EF Core** is used only in Infrastructure (stores/query services), not in Domain.

---

## D. Implemented modules

### Auth (email/password)

**Implemented**

- `POST /api/auth/register` — creates user with normalized (trim + lower) email, BCrypt password hash, role **User**, `IsBlocked = false`.
- `POST /api/auth/login` — verifies password; rejects blocked users.
- `GET /api/auth/me` — returns current user from JWT subject; rejects blocked users.

**Partial / notes**

- Registration always assigns `UserRole.User` (no self-service admin signup).
- No refresh tokens, email verification, or password reset flows in code.

### External auth (Google / Apple)

**Implemented**

- `POST /api/auth/external` — body: `provider` (`google` / `apple`) + `idToken`.
- **Google**: validates ID token via `GoogleJsonWebSignature.ValidateAsync` with configured audiences (`ExternalAuth:Google:ValidAudiences`).
- **Apple**: validates JWT using keys from `ExternalAuth:Apple:JwksUrl`, issuer `ExternalAuth:Apple:Issuer`, audiences `ExternalAuth:Apple:ValidAudiences`.
- User resolution: find by `(ExternalAuthProvider, ExternalProviderId)`; else if email present, find/create/link by email with explicit link rules; blocked users rejected; returns same `AuthResponse` as login/register.

**Partial / notes**

- `ExternalAuthOptions` is bound from configuration but **not** validated with `ValidateOnStart` in Infrastructure (invalid config surfaces at token validation time).
- New external-only users get `PasswordHash = string.Empty` in code; password login for such users is not separately guarded in `LoginAsync` (BCrypt verify on empty hash is effectively invalid login, not a dedicated error).

### Listings (toy listings)

**Implemented**

- Public **approved** listing list + detail (`GET /api/listings`, `GET /api/listings/{id}`) with filters and pagination; detail includes **approved** booking date ranges for availability-style display.
- Owner: `POST /api/listings` creates listing in **`PendingApproval`**; `GET /api/listings/mine` returns all statuses for the owner.
- Owner images: `POST /api/listings/{listingId}/images` multipart upload; content-type/extension allowlist; stored under `wwwroot` + configured relative path.
- Toy-specific optional metadata on every listing (all nullable, all additive):
  - `ageFromMonths`, `ageToMonths` — recommended age range in months (cross-validated: `to >= from`)
  - `condition` — short condition tag (e.g. "Excellent", "Like new", "Good", "Used")
  - `hygieneNotes` — how the toy is cleaned between rentals
  - `safetyNotes` — safety considerations parents should know
  - `depositAmount` — optional refundable deposit

  These fields are accepted on `POST /api/listings`, returned by `GET /api/listings/{id}` and populated for seed data. They are intentionally **optional** so the generic `Listing` entity stays compatible and the create-listing contract stays backward compatible.

**Partial / notes**

- No owner endpoints for **edit**, **delete**, or **withdraw** listings in controllers.
- Public endpoints never expose non-approved listings.
- The route stays `/api/listings`, not `/api/toys`, to avoid breaking existing frontend integrations. The toy domain is expressed through categories, seed data and the optional fields above.

### Categories (toy categories)

**Implemented**

- `GET /api/categories` — all categories ordered by name.

**Partial / notes**

- Read-only API; **no** admin CRUD for categories.
- The seeded category set is toy-focused: Educational Toys, Building Blocks, Outdoor Toys, Baby Toys, Board Games, Pretend Play, Ride-On Toys, Puzzles, Montessori Toys, Party Toys.

### Bookings

**Implemented**

- Renter: `POST /api/bookings` — pending booking, 24h expiry, overlap check against **approved** bookings, listing must be **Approved**, cannot book own listing, date rules.
- Renter: `GET /api/bookings/mine`.
- Owner: `GET /api/bookings/requests` — **Pending** only for listings they own.
- Owner: `POST /api/bookings/{id}/approve` and `…/reject` — only if booking still **Pending** and **not expired**; approve re-checks overlap.

**Partial / notes**

- No renter **cancel** endpoint after creation.
- Expiry is applied via `ExpirePendingAsync` (bulk update) when booking operations run, not as a standalone background job.

### Favorites

**Implemented**

- `GET /api/favorites` — current user’s favorites as listing previews.
- `POST /api/favorites/{listingId}` — idempotent add (`TryAddAsync` handles unique race); returns `bool` (inserted vs already existed).
- `DELETE /api/favorites/{listingId}` — removes if present.

**Partial / notes**

- `AddAsync` accepts any existing `ListingId` (including **non-approved** listings); there is **no** “approved only” rule in `FavoritesService`.
- `DELETE` returns **204 No Content** whenever the service returns success, including when nothing was removed (`Success(false)`), so the HTTP response does not distinguish “removed” vs “was not a favorite”.

### Admin moderation

**Implemented**

- `GET /api/admin/listings/pending` — returns `PendingListingForReviewResponse[]`; includes full listing detail (description, toy-specific fields, images, owner info) so the admin has everything needed to decide.
- `POST /api/admin/listings/{id}/approve` — sets status `Approved`, records `moderatedAt` + `moderatedByUserId`, clears any prior `rejectionReason`, saves, then fires an approval email to the owner.
- `POST /api/admin/listings/{id}/reject` — request body `{ "reason": "string" }` (required, 1–1000 chars); sets status `Rejected`, stores `rejectionReason`, records `moderatedAt` + `moderatedByUserId`, saves, then fires a rejection email to the owner.

Both moderation endpoints return `ModerateListingResponse` (`id`, `status`, `rejectionReason`, `moderatedAt`, `message`).

**Moderation entity fields** (added via migration `20260515061307_AddListingModerationFields`):

| Field | Type | Notes |
|-------|------|-------|
| `RejectionReason` | `nvarchar(1000) NULL` | Set on reject, cleared on approve. |
| `ModeratedAt` | `datetime2 NULL` | UTC timestamp of the last moderation action. |
| `ModeratedByUserId` | `uniqueidentifier NULL` | Admin user id; stored for audit, no FK constraint. |

**Email notification behavior**

- Abstraction: `IEmailService` in `Application/Abstractions`. Contract: **never throws** — implementations handle and log all failures internally.
- Development implementation: `DevelopmentEmailService` (`Infrastructure/Services`). Writes full email content (recipient, subject, body) to the application log at `Information` level using `ILogger<DevelopmentEmailService>`. No SMTP required locally.
- Production replacement: swap `DevelopmentEmailService` for an SMTP/SES/SendGrid implementation in DI — the application layer requires no changes.
- **Email failure policy (MVP)**: moderation action (approve/reject) is committed to the database first; the email is fired after. If the email service implementation throws despite the contract, the moderation result is still returned to the caller. Production implementations should catch their own exceptions.

**Partial / notes**

- Admin authorization is both `[Authorize(Roles = "Admin")]` policy attribute and explicit `UserRole` check in the service (defense in depth).
- Only `PendingApproval` listings can be moderated; re-moderating an already-approved or rejected listing returns `409 Conflict`.

### File storage

**Implemented**

- `IFileStorageService` with `LocalFileStorageService`: writes under `ContentRootPath/wwwroot/{ListingsImagesPath}` with path traversal guard; returns public URL path string used in `ListingImage.Url`.

**Partial / notes**

- If `wwwroot` does not exist, `UseStaticFiles` is skipped in `Program.cs`; uploaded files still land under `wwwroot/...` when saving, so directory creation happens on first upload.

---

## E. Authentication

### JWT (app tokens)

- Issued by `JwtTokenService` after successful register/login/external auth.
- Claims include: `sub` + `NameIdentifier` (user id), `email` (JWT + claim types), `role` (string name of `UserRole`), `jti`.
- API JWT bearer configuration (`AddJwtBearer`) uses `NameClaimType = ClaimTypes.Email`, `RoleClaimType = ClaimTypes.Role`, validates issuer/audience/signing key/lifetime, zero clock skew.

### Password hashing

- `BcryptPasswordHasher` implements `IPasswordHasher` (`HashPassword` / `VerifyPassword`).

### Current user

- `ICurrentUserContext.UserId` reads `ClaimTypes.NameIdentifier` or `JwtRegisteredClaimNames.Sub` from `HttpContext.User`.

### External auth flow (summary)

1. Client sends `provider` + `idToken`.
2. `ExternalIdentityTokenValidator` validates with Google or Apple.
3. `AuthService.ExternalAsync` finds or creates/links user, then issues the same JWT as password login.

---

## F. Business rules (from code)

### Listing lifecycle (`ListingStatus`)

- **Create (owner)**: always **`PendingApproval`** (`ListingsOwnerService`).
- **Public read**: only **`Approved`** listings (`ListingsQueryService`).
- **Admin**: may set **`PendingApproval`** → **`Approved`** or **`Rejected`** only (`AdminListingsService`); other statuses rejected with `admin.invalid_listing_status`.

### Booking rules (`BookingsService` + `BookingsStore`)

- Listing must exist and be **`Approved`**.
- Renter cannot book **own** listing.
- `EndDate >= StartDate`; `StartDate` cannot be before **today** (UTC calendar date).
- **Overlap**: cannot create or **approve** if another **Approved** booking overlaps the date range (inclusive range overlap in store query).
- New booking: **`Pending`**, `ExpiresAt = UtcNow + 24 hours`, `TotalPrice = inclusiveDayCount * listing.PricePerDay`.
- Owner actions: only **`Pending`** and `ExpiresAt > now`; otherwise `booking.not_pending`.
- Pending expiry: `ExpirePendingAsync` sets status **`Expired`** when `ExpiresAt <= utcNow` (executed as part of booking service operations).

### Favorite rules (`FavoritesService` + `FavoritesStore`)

- Blocked users cannot use favorites.
- Unique `(UserId, ListingId)` at DB level; `TryAddAsync` swallows unique constraint violations and returns `false` for idempotent add.

### Admin rules (`AdminListingsService`)

- Must be authenticated, `UserRole.Admin`, not blocked.
- Approve/reject only when listing is **`PendingApproval`**; otherwise `409 Conflict`.
- **Reject** requires a non-empty `reason` (1–1000 chars, trimmed); validated by `RejectListingRequest` DTO and enforced in service.
- Approve sets `Status = Approved`, clears `RejectionReason`, records `ModeratedAt` + `ModeratedByUserId`.
- Reject sets `Status = Rejected`, stores `RejectionReason`, records `ModeratedAt` + `ModeratedByUserId`.
- Owner email notification is fired after saving; if the notification fails (in a production SMTP scenario), the moderation result is still returned — email failures never roll back moderation.

---

## G. API endpoints

Base route pattern: **`/api/{controller}`** except admin listings (**`/api/admin/listings`**).

### Auth — `AuthController` → `/api/auth`

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| POST | `/api/auth/register` | Anonymous | Register; returns `AuthResponse`. |
| POST | `/api/auth/login` | Anonymous | Login; returns `AuthResponse`. |
| POST | `/api/auth/external` | Anonymous | External provider token sign-in; returns `AuthResponse`. |
| GET | `/api/auth/me` | **Authorize** | Current user profile. |

### Categories — `CategoriesController` → `/api/categories`

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| GET | `/api/categories` | Anonymous | All categories. |

### Listings — `ListingsController` → `/api/listings`

| Method | Route | Auth | Description |
|--------|-------|------|-------------|
| GET | `/api/listings` | Anonymous | Paged **approved** listings; query: `City`, `CategoryId`, `MinPrice`, `MaxPrice`, `Page`, `PageSize` (`ListingsQueryFilter`). |
| GET | `/api/listings/{id}` | Anonymous | **Approved** listing detail or 404. |
| POST | `/api/listings` | **Authorize** | Create listing (**pending approval**). |
| GET | `/api/listings/mine` | **Authorize** | Owner’s listings (all statuses). |
| POST | `/api/listings/{listingId}/images` | **Authorize** | Multipart image upload for owned listing. |

### Bookings — `BookingsController` → `/api/bookings`

Controller has **`[Authorize]`** on the class — all actions require JWT.

| Method | Route | Request body | Description |
|--------|-------|--------------|-------------|
| GET | `/api/admin/listings/pending` | — | Full pending queue (`PendingListingForReviewResponse[]`). |
| POST | `/api/admin/listings/{id}/approve` | — | Approve; fires owner email. Returns `ModerateListingResponse`. |
| POST | `/api/admin/listings/{id}/reject` | `{ "reason": "..." }` | Reject with required reason; fires owner email. Returns `ModerateListingResponse`. |

---

## H. Database structure

### Main entities (`Domain/Entities`)

- **User**: email (unique), password hash, profile fields, optional external auth fields (`ExternalAuthProvider`, `ExternalProviderId`), optional `AvatarUrl`, `CreatedAt`, `IsBlocked`, `Role`.
- **Category**: `Name`, `Slug` (unique).
- **Listing**: owner, category, address/geo, pricing, `Status`, timestamps; navigation to images, bookings, favorites.
- **ListingImage**: `Url`, `IsPrimary`, `SortOrder`.
- **Booking**: listing, renter, date range, `TotalPrice`, `Status`, `ExpiresAt`, timestamps.
- **Favorite**: user, listing, `CreatedAt`.

### Relationships (high level)

- User 1—* Listings (owner); User 1—* Bookings (renter); User 1—* Favorites.
- Category 1—* Listings.
- Listing 1—* ListingImages / Bookings / Favorites.

### Important constraints (see `Infrastructure/Configurations`)

- Unique `Users.Email`.
- Filtered unique index on `(ExternalAuthProvider, ExternalProviderId)` when both non-null.
- Unique `Favorites (UserId, ListingId)`.
- Unique `Categories.Slug`.
- Booking overlap support via composite index on `(ListingId, Status, StartDate, EndDate)` (see `BookingConfiguration`).

### Migrations

- EF migrations live under `src/RentalPlatform.Infrastructure/Persistence/Migrations/` (includes initial schema and subsequent changes such as external auth columns).

---

## I. Configuration

### Connection string

- `ConnectionStrings:DefaultConnection` — required; Infrastructure throws if missing.

### JWT (`Jwt` section; `JwtOptions.SectionName = "Jwt"`)

- `Issuer`, `Audience`, `SecretKey` (minimum **32** characters), `AccessTokenExpirationMinutes` (> 0).
- API startup validates these values in `AddApiServices` before registering JWT bearer.
- Infrastructure also registers options with `ValidateOnStart` for the same rules.

### External auth (`ExternalAuth`)

- `Google:ValidAudiences` — string array (client IDs).
- `Apple:Issuer`, `Apple:JwksUrl`, `Apple:ValidAudiences`.

### File storage (`FileStorage`)

- `ListingsImagesPath` — relative path under `wwwroot` (validated to stay under `wwwroot`).

### CORS (`Cors:AllowedOrigins`)

- Optional string array; in **Development**, loopback HTTP(S) origins may be allowed via `SetIsOriginAllowed` logic in `Api/Extensions/ServiceCollectionExtensions.cs`.

### Environment files

- `appsettings.json` — base; production-like placeholders (e.g. empty `Jwt:SecretKey` must be overridden).
- `appsettings.Development.json` — local SQL Server example, dev JWT secret, CORS origins, placeholder external auth audiences.

---

## J. File storage

- **Implementation**: `LocalFileStorageService` (`Singleton` lifetime in DI).
- **Physical path**: `{ContentRoot}/wwwroot/{ListingsImagesPath}/...`
- **Public URL**: `/{ListingsImagesPath}/{generatedFileName}` (forward slashes).
- **Upload rules** (`ListingImagesOwnerService`): JPEG/PNG/WebP/GIF by content type and extension; non-empty files; owner-only.

---

## K. Seed / data state

### Development seed

- **When**: `Program.cs` calls `await app.Services.SeedDevelopmentDataAsync()` **only if** `app.Environment.IsDevelopment()` **before** the rest of the pipeline runs.
- **Where**: `Infrastructure/DependencyInjection/DevelopmentSeed/`.
- **What** (idempotent, keyed by slugs / seed emails / fixed listing ids):
  - **Categories** (10, toy-focused): Educational Toys, Building Blocks, Outdoor Toys, Baby Toys, Board Games, Pretend Play, Ride-On Toys, Puzzles, Montessori Toys, Party Toys.
  - **Users** (5 demo accounts, all with password `LocalDemo123!`):
    | Email | Role | Notes |
    |-------|------|-------|
    | `admin@rental.local`    | Admin | Moderates pending listings |
    | `owner@rental.local`    | User  | Owns every seeded toy listing |
    | `renter@rental.local`   | User  | Books toys and uses favorites |
    | `user2@rental.local`    | User  | Secondary user for ownership/favorites checks |
    | `blocked@rental.local`  | User  | `IsBlocked = true`, for auth-rejection testing |
  - **Listings** (10 toy listings): 7 **Approved**, 2 **PendingApproval**, 1 **Rejected**. Yerevan/Gyumri addresses, USD pricing, realistic toy data (LEGO Duplo, Montessori wooden set, baby activity gym, balance bike, backyard slide, puzzle bundle, toy kitchen, board game bundle, birthday party pack, soft-play foam set). Each listing populates the toy-specific optional fields (age range, condition, hygiene/safety notes, deposit).
  - **Listing images** (14): real placeholder URLs from `picsum.photos`, so dev demos render without any local file setup.
  - **Favorites** (4): renter and second user have a few favorited toys.
  - **Bookings** (5): one per non-overlapping state — Pending, Approved, Rejected, Expired, Completed — by the demo renter against different approved listings (so no Approved overlap).

### Demo password (development)

- Constant in code: **`LocalDemo123!`** (also logged at **Information** level when seed runs — acceptable for local dev only; do not rely on this for production).

### Resetting the dev database after the toy refocus

The seed is **idempotent and additive**: it only inserts rows that are missing, never deletes. If your local dev database was populated by an earlier (non-toy) seed run, you will see both old generic categories/listings and the new toy ones side-by-side. For a clean toy MVP demo, drop the database before starting the API:

```bash
dotnet ef database drop --force --project src/RentalPlatform.Infrastructure/RentalPlatform.Infrastructure.csproj --startup-project src/RentalPlatform.Api/RentalPlatform.Api.csproj
```

The next startup will recreate the schema (migrations) and seed the toy data.

### Production / non-Development

- **No** automatic seeding in `Program.cs` outside Development.

---

## L. Known gaps / TODO (non-MVP / postponed)

Verified from code structure and behavior described above:

- **No** listing **update** or **delete** APIs for owners.
- **No** booking **cancel** or renter-side withdrawal after create.
- **No** category management (create/update) for admins.
- **No** user profile update, email change, or password reset.
- **No** refresh tokens or token revocation.
- **Favorites** are kept stable but **not** an MVP focus: no restriction to approved listings, `DELETE` returns **204** even when nothing was removed, and the module is intentionally not expanded.
- **External auth** (Google/Apple) is implemented but **postponed** for the toy MVP; external-only users have empty `PasswordHash` and password login fails for them as invalid credentials.
- **Admin users** cannot be created through the public register endpoint (always `User`); must be seeded or updated in DB.
- **Apple** token may omit email on later logins; linking path requires email when no existing external mapping — operational limitation documented in `AuthService` (`auth.external_email_missing`).
- **Seed images** are remote URLs (`picsum.photos`); no static files are written to disk by the seed.
- **Background job** for booking expiry not present; expiry runs opportunistically when booking code paths execute.
- **Production** `appsettings.json` ships with empty JWT secret and placeholder external auth audiences — hosting **must** override via environment/secrets or the app will fail JWT validation at startup (by design).
- **Toy-specific fields** (`ageFromMonths`, `ageToMonths`, `condition`, `hygieneNotes`, `safetyNotes`, `depositAmount`) are optional and exposed only on listing **detail**, **create** and seed data. They are intentionally **not** added to list / preview / "my listings" / admin queue responses to keep those payloads small and avoid frontend ripple — they can be added later when there is concrete UI demand.

---

## M. Running the project

### Prerequisites

- **.NET 8 SDK** (projects target `net8.0`).
- **SQL Server** reachable from the machine (connection string uses `Server=.` in Development sample).
- **EF Core tools** (optional): `dotnet-ef` for migrations (`Microsoft.EntityFrameworkCore.Design` is referenced from the API project for tooling).

### Database

1. Create an empty database (name must match your connection string).
2. Apply migrations (from repo root), for example:

```bash
dotnet ef database update --project src/RentalPlatform.Infrastructure/RentalPlatform.Infrastructure.csproj --startup-project src/RentalPlatform.Api/RentalPlatform.Api.csproj
```

### Run the API

```bash
dotnet run --project src/RentalPlatform.Api/RentalPlatform.Api.csproj
```

- URLs are defined in `src/RentalPlatform.Api/Properties/launchSettings.json` (default includes `https://localhost:7241` and `http://localhost:5241`).
- **HTTPS**: trust the ASP.NET dev certificate (`dotnet dev-certs https --trust`) if browsers reject `https://localhost`.

### Swagger

- Available in **Development** at the default Swagger UI path after startup.

---

## N. Development rules (as reflected by this codebase)

- **Controllers** map HTTP to application services; they do not embed business rules (validation attributes on DTOs are an exception at the API boundary; service layer enforces rules with `ServiceResult`).
- **Application** holds use cases and abstractions; **Infrastructure** implements persistence and integrations.
- **EF Core** is confined to Infrastructure stores/services; Domain entities are POCOs configured via `IEntityTypeConfiguration<T>`.
- **JWT** configuration must stay consistent between token generation (`JwtTokenService`) and API bearer validation (`AddJwtBearer`).
- **Ownership** checks live in application services (e.g. booking owner approve, listing image owner).
- **Admin** operations use both `[Authorize(Roles = "Admin")]` and explicit `UserRole` checks in `AdminListingsService`.

---

*Last aligned with repository layout: `RentalPlatform.Api`, `RentalPlatform.Application`, `RentalPlatform.Domain`, `RentalPlatform.Infrastructure`.*
