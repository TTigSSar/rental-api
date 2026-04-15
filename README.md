# Rental Marketplace Backend

This document describes the **current** rental marketplace backend as implemented in this repository. It is derived from the codebase only; anything not present in code is called out explicitly under **Known gaps**.

---

## A. Project Overview

The backend is an **ASP.NET Core Web API** for a rental marketplace. It exposes JSON REST endpoints for:

- User registration and login (email/password)
- Optional sign-in with **Google** or **Apple** identity tokens (validated server-side)
- JWT-based API authentication
- Public browsing of **approved** listings and categories
- Authenticated owners creating listings (submitted as **pending approval**), viewing their own listings, and uploading listing images to **local disk** under `wwwroot`
- Renters creating **booking requests** for approved listings; listing owners approving or rejecting **pending** requests (with overlap and expiry rules)
- Users managing **favorites**
- **Admin** users moderating **pending** listings (approve/reject)

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

### Listings

**Implemented**

- Public **approved** listing list + detail (`GET /api/listings`, `GET /api/listings/{id}`) with filters and pagination; detail includes **approved** booking date ranges for availability-style display.
- Owner: `POST /api/listings` creates listing in **`PendingApproval`**; `GET /api/listings/mine` returns all statuses for the owner.
- Owner images: `POST /api/listings/{listingId}/images` multipart upload; content-type/extension allowlist; stored under `wwwroot` + configured relative path.

**Partial / notes**

- No owner endpoints for **edit**, **delete**, or **withdraw** listings in controllers.
- Public endpoints never expose non-approved listings.

### Categories

**Implemented**

- `GET /api/categories` — all categories ordered by name.

**Partial / notes**

- Read-only API; **no** admin CRUD for categories.

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

- `GET /api/admin/listings/pending` — `[Authorize(Roles = "Admin")]`.
- `POST /api/admin/listings/{id}/approve` and `…/reject` — only if listing status is **`PendingApproval`**; service also verifies `UserRole.Admin` and not blocked.

**Partial / notes**

- Admin authorization is both **policy attribute** and **service-layer** role check (defense in depth, not a gap).

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
- Approve/reject only when listing is **`PendingApproval`**.

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

| Method | Route | Description |
|--------|-------|-------------|
| POST | `/api/bookings` | Create booking request. |
| GET | `/api/bookings/mine` | Renter’s bookings. |
| GET | `/api/bookings/requests` | Owner’s **pending** requests. |
| POST | `/api/bookings/{id}/approve` | Owner approves. |
| POST | `/api/bookings/{id}/reject` | Owner rejects. |

### Favorites — `FavoritesController` → `/api/favorites`

Class **`[Authorize]`**.

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/favorites` | My favorites (listing previews). |
| POST | `/api/favorites/{listingId}` | Add favorite; returns `bool`. |
| DELETE | `/api/favorites/{listingId}` | Remove favorite (204 on success). |

### Admin — `AdminListingsController` → `/api/admin/listings`

**`[Authorize(Roles = "Admin")]`** on controller.

| Method | Route | Description |
|--------|-------|-------------|
| GET | `/api/admin/listings/pending` | Pending listings queue. |
| POST | `/api/admin/listings/{id}/approve` | Approve listing. |
| POST | `/api/admin/listings/{id}/reject` | Reject listing. |

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
- **Where**: `Infrastructure/DependencyInjection/DevelopmentSeedExtensions.cs`.
- **What** (idempotent, keyed by slugs / seed emails / fixed listing ids):
  - Categories: Apartments, Houses, Cars, Electronics, Toys, Tools (with fixed GUIDs for categories used by listings).
  - Users (if missing): `demo.user@local.test` (**User**) and `demo.admin@local.test` (**Admin**), BCrypt-hashed password.
  - Listings (if missing fixed ids): one **Approved**, one **PendingApproval**, one **Rejected**.
  - One **ListingImage** per seeded listing with placeholder URL `/uploads/listings/dev-{listingGuid}.jpg` (file may not exist on disk).
  - Optional: one **Favorite** (demo user → approved listing) and one **Pending** **Booking** on the approved listing, if not already present.

### Demo password (development)

- Constant in code: **`LocalDemo123!`** (also logged at **Information** level when seed runs — acceptable for local dev only; do not rely on this for production).

### Production / non-Development

- **No** automatic seeding in `Program.cs` outside Development.

---

## L. Known gaps / TODO

Verified from code structure and behavior described above:

- **No** listing **update** or **delete** APIs for owners.
- **No** booking **cancel** or renter-side withdrawal after create.
- **No** category management (create/update) for admins.
- **No** user profile update, email change, or password reset.
- **No** refresh tokens or token revocation.
- **Favorites**: no restriction to **approved** listings only; can favorite non-public listings by id.
- **Favorites** `DELETE`: HTTP **204** even when the favorite did not exist (service returns success with `false`).
- **External users** use empty `PasswordHash`; no explicit “external only” branch in password login (fails as invalid credentials).
- **Admin users** cannot be created through the public register endpoint (always `User`); must be seeded or updated in DB.
- **Apple** token may omit email on later logins; linking path requires email when no existing external mapping — operational limitation documented in `AuthService` (`auth.external_email_missing`).
- **Seed images** are DB URLs only; static files may not exist unless `wwwroot` is populated or images uploaded.
- **Background job** for booking expiry not present; expiry runs opportunistically when booking code paths execute.
- **Production** `appsettings.json` ships with empty JWT secret and placeholder external auth audiences — hosting **must** override via environment/secrets or the app will fail JWT validation at startup (by design).

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
