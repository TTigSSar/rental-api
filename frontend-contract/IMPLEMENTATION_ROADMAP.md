# IMPLEMENTATION_ROADMAP.md

> **Permanent implementation strategy** for ToyRent — child toys rental marketplace MVP (Angular + NgRx + ASP.NET Core).

| | |
|---|---|
| **Companion document** | [`DESIGN_RULES.md`](./DESIGN_RULES.md) — visual/UX source of truth (Direction A: **Refined Warm** only) |
| **Audience** | Product lead, frontend developers, Cursor AI, Claude Code |
| **Goal** | Ship a polished, trust-first, mobile-ready MVP in Armenia without endless refactoring or scope chaos |

---

## Table of contents

1. [Product Implementation Strategy](#1-product-implementation-strategy)
2. [Current Product Status Review](#2-current-product-status-review)
3. [Implementation Priority Principles](#3-implementation-priority-principles)
4. [Phase 1 — Design Foundation Stabilization](#4-phase-1--design-foundation-stabilization)
5. [Phase 2 — Mobile Navigation & Core UX](#5-phase-2--mobile-navigation--core-ux)
6. [Phase 3 — Listing Details Page Optimization](#6-phase-3--listing-details-page-optimization)
7. [Phase 4 — Create Listing Wizard](#7-phase-4--create-listing-wizard)
8. [Phase 5 — My Toys Dashboard Improvements](#8-phase-5--my-toys-dashboard-improvements)
9. [Phase 6 — Admin Moderation UX](#9-phase-6--admin-moderation-ux)
10. [Phase 7 — Empty States & Trust Polish](#10-phase-7--empty-states--trust-polish)
11. [Phase 8 — Production Stabilization](#11-phase-8--production-stabilization)
12. [Phase 9 — Real User Testing Preparation](#12-phase-9--real-user-testing-preparation)
13. [Backend-Dependent Future Features](#13-backend-dependent-future-features)
14. [Features to Explicitly Avoid for Now](#14-features-to-explicitly-avoid-for-now)
15. [Implementation Safety Rules](#15-implementation-safety-rules)
16. [Cursor + Claude Execution Workflow](#16-cursor--claude-execution-workflow)
17. [Recommended Implementation Order](#17-recommended-implementation-order)
18. [MVP Launch Checklist](#18-mvp-launch-checklist)
19. [Post-Launch Roadmap (High Level)](#19-post-launch-roadmap-high-level)
20. [Final Product Strategy Summary](#20-final-product-strategy-summary)
- [Appendix — Phase Summary Table](#appendix--phase-summary-table)

---

## 1. PRODUCT IMPLEMENTATION STRATEGY

### Overall philosophy

- **Ship working flows first, polish second, expand last.**
- Every change must improve conversion, trust, or stability — not aesthetics alone.
- The codebase already works end-to-end (browse → detail → book, list → moderate → approve). Protect that.
- UI growth must follow **one design direction** and **one token system**.

### Incremental implementation approach

1. **Stabilize tokens and shared components** before redesigning pages.
2. **Improve mobile UX** before adding desktop-only features.
3. **Optimize conversion pages** (listing details, create listing) before secondary pages.
4. **Polish empty/trust states** after core flows are solid.
5. **Production hardening** immediately before launch — not at the start.

Each phase = 1 focused sprint. One phase in flight at a time. No parallel redesigns.

### MVP-first mindset

- MVP = parents can **find**, **trust**, **request rental**; owners can **list**; admins can **moderate**.
- Payments, chat prominence, ratings, recommendations, and social auth are **not** launch blockers.
- Hardcoded Armenia/Yerevan for listing location is acceptable until post-launch.

### Stabilization-first approach

- Fix inconsistencies (tokens, card variants, spacing) **before** new features.
- Extend existing components (`listing-card`, `booking-panel`, `ui-badge`) — do not fork new card types.
- NgRx + API service patterns are **frozen** unless a backend contract changes.

### Mobile-first rollout strategy

1. Design and QA at **375px width** for every phase.
2. Ship mobile interaction improvements (sticky CTAs, bottom nav) before desktop refinements.
3. Desktop enhancements are additive (sticky sidebar, multi-column grids) — never mobile-afterthoughts.

---

## 2. CURRENT PRODUCT STATUS REVIEW

### What is already good (do not rewrite)

| Area | Status |
| ---- | ------ |
| **Architecture** | Angular standalone, lazy routes, NgRx per feature, typed API services |
| **Auth** | Login/register, guards, token persistence, role-based nav, `AuthRedirectService` return URLs |
| **Browse toys** | `/listings` with filters, load-more, title search via `query` filter |
| **Listing details** | Gallery, toy trust fields, booking panel, guest auth dialog, sticky sidebar ≥1100px |
| **Create listing** | Form + image upload, validation aligned with backend, success toast → My Toys |
| **Bookings** | Create request, My Bookings, Rental Requests, approve/reject |
| **Admin moderation** | Pending queue, image-first cards, approve/reject + reason modal, toasts |
| **My Toys** | Status badges (Pending/Approved/Rejected), edit/archive actions |
| **Homepage** | Hero, search, category tiles, API-driven sections (Popular/Recent) |
| **Info pages** | About, FAQ, Terms, Privacy — lazy-loaded |
| **Global shell** | Sticky header, role-aware nav, global footer (hidden on listing details) |
| **i18n** | EN / RU / HY via ngx-translate |

### What is unstable or inconsistent

| Issue | Impact |
| ----- | ------ |
| **Token gaps** | `--ui-space-20`, `--ui-space-6` used in components but missing from `styles.css` |
| **Hardcoded colors** | Auth pages, some page titles use raw hex instead of CSS variables |
| **Dual card styles** | `listing-card` vs homepage `product-card` / `featured-listing-tile` diverge |
| **Breakpoint sprawl** | 520, 560, 600, 640, 700, 900, 960, 1100px — needs consolidation per DESIGN_RULES |
| **Mobile booking UX** | Panel flows below content; no sticky bottom CTA bar on mobile |
| **Mobile navigation** | Burger drawer only; no bottom nav (planned in DESIGN_RULES) |
| **Create listing** | Single long form; no wizard, no draft save |
| **My Toys empty state** | Message only — missing "List a Toy" CTA |
| **Test coverage** | Minimal (`app.spec.ts` only) |
| **Bundle budgets** | `app.css` and initial bundle exceed configured thresholds |
| **README drift** | Some docs outdated (e.g. auth redirect now implemented) |

### What should NOT be rewritten

- NgRx store/effects structure per feature
- `ApiContract` + `toApiUrl` pattern
- Auth guard chain and admin role normalization
- Listing API normalization in `listings-api.service.ts`
- Admin moderation flow (extend, don't replace)
- PrimeNG as UI foundation (override tokens, don't swap library)

### Current product strengths

- End-to-end marketplace loop works
- Trust fields (condition, hygiene, age) integrated on detail + admin
- Family-oriented copy and toy-focused language
- Moderation gate (PendingApproval) builds supply quality

### Current UX weaknesses

- Mobile conversion path on listing details is long (scroll to book)
- Visual inconsistency between homepage tiles and browse cards
- Owner dashboard feels functional but not motivational
- Trust signals on browse cards are minimal (no age/condition preview)
- No onboarding guidance for first-time listers or renters

---

## 3. IMPLEMENTATION PRIORITY PRINCIPLES

Prioritize tasks using this scorecard (highest first):

| Principle | Question |
| --------- | ---------- |
| **Conversion impact** | Does this help users complete a rental request or list a toy? |
| **Trust impact** | Does this reduce anxiety about hygiene, safety, or owner reliability? |
| **Engineering safety** | Can this ship without touching backend or with isolated frontend changes? |
| **Mobile UX importance** | Does this fix a mobile-only pain point? |
| **Implementation speed** | Can this ship in ≤3 days without cross-feature rewrites? |
| **User psychology** | Does this clarify the next step and reduce abandonment? |

**Deprioritize:** new categories, chat UI, favorites page, social login, analytics dashboards, payment UI.

---

## 4. PHASE 1 — DESIGN FOUNDATION STABILIZATION

| | |
|---|---|
| **Sprint** | Week 1 |
| **Backend** | No — frontend-only |
| **Complexity** | Low |
| **Risk** | Low |

### Objective

Establish one visual language across the app with minimal code churn — token cleanup, not a redesign.

### Business value

Higher perceived quality and trust; faster AI/human implementation with fewer one-off styles.

### UX impact

**Medium-high** — users feel a cohesive "product" instead of stitched pages.

### Engineering complexity

**Low** — mostly CSS and small template class updates.

### Implementation risk

**Low** — no flow changes; visual-only.

### Dependencies

- `DESIGN_RULES.md` published ✅
- None blocking

### Tasks (ordered)

1. Add missing tokens to `src/styles.css`: `--ui-space-6`, `--ui-space-20`, semantic success/warning if needed
2. Replace hardcoded hex in auth pages, listings page title, featured tile with CSS variables
3. Align `product-card` / `featured-listing-tile` shadows and radius to `--ui-shadow-card` / `--ui-radius-md`
4. Document canonical breakpoints in comments at top of `styles.css`
5. Ensure all PrimeNG button/input overrides reference tokens only

### Success criteria

- [ ] Zero new hardcoded brand colors in touched files
- [ ] All spacing uses `--ui-space-*` tokens
- [ ] Homepage tiles and browse cards feel like one family at 375px and 1440px
- [ ] `npm run build` passes

### Pages / components affected

| File / area | Change |
| ----------- | ------ |
| `src/styles.css` | Token additions |
| `login-page`, `register-page` SCSS | Token migration |
| `featured-listing-tile` | Shadow/radius alignment |
| `listings-page` | Title color token |
| `create-listing-form` | Spacing token cleanup |

### Backend required?

**No** — frontend-only.

### Estimated order

**Sprint 1 — Week 1** (first task in roadmap)

---

## 5. PHASE 2 — MOBILE NAVIGATION & CORE UX

| | |
|---|---|
| **Sprint** | Week 2 |
| **Backend** | No |
| **Complexity** | Medium |
| **Risk** | Medium |

### Objective

Improve daily mobile usage: navigation, touch targets, spacing, and primary action reach.

### Business value

Armenian users are mobile-heavy; faster navigation = more browsing and listings.

### UX impact

**High** — reduces friction for returning users.

### Engineering complexity

**Medium** — app shell changes affect all pages.

### Implementation risk

**Medium** — bottom nav must not conflict with sticky CTAs (Phase 3). Implement nav first; reserve bottom safe-area.

### Dependencies

- Phase 1 complete (tokens stable)
- DESIGN_RULES §16 navigation spec

### Tasks (ordered)

1. **Mobile bottom navigation** (≤960px): Home, Browse, My Toys, Bookings — hide duplicate links from burger where redundant
2. Add `padding-bottom: calc(64px + env(safe-area-inset-bottom))` to main content on mobile when bottom nav active
3. Audit touch targets: header buttons, fav icon, filter chips — minimum 44×44px
4. Consolidate breakpoints in `app.css` to canonical set (900, 960, 1100)
5. Fix mobile page padding inconsistencies (16px inline below 900px everywhere)
6. Language switcher: ensure reachable in bottom nav era or keep in header drawer

### Success criteria

- [ ] Primary destinations reachable in ≤2 taps on mobile
- [ ] No content hidden behind bottom nav
- [ ] Burger drawer still works for account, admin, language
- [ ] QA pass at 375px, 390px, 768px

### Pages / components affected

| Area | Change |
| ---- | ------ |
| `app.ts`, `app.html`, `app.css` | Bottom nav component/slot |
| All `page-container` pages | Bottom padding for nav |
| Header drawer | Dedupe links |

### Backend required?

**No**.

### Estimated order

**Sprint 2 — Week 2**

---

## 6. PHASE 3 — LISTING DETAILS PAGE OPTIMIZATION

| | |
|---|---|
| **Sprint** | Week 3 |
| **Backend** | No |
| **Complexity** | Medium |
| **Risk** | Medium |
| **Conversion impact** | Very high |

### Objective

Maximize rental conversion on the highest-intent page — trust-first layout + mobile sticky booking.

### Business value

Direct revenue impact — every improved detail view → booking conversion.

### UX impact

**Very high** — core marketplace funnel.

### Engineering complexity

**Medium** — layout restructure on one page + booking panel enhancements.

### Implementation risk

**Medium** — sticky mobile bar must not fight bottom nav; coordinate z-index and safe-area.

### Dependencies

- Phase 2 bottom nav (for safe-area / z-index)
- Existing `booking-panel`, `listing-gallery`, guest auth dialog

### Tasks (ordered)

1. **Mobile sticky booking bar** (<1100px): price + "Request rental" fixed above bottom nav; expands to full panel on tap OR scroll-to-panel
2. Reorder mobile content: gallery → title/price → **trust summary block** → description → owner → full booking panel
3. Add **trust summary row** below title: condition chip, age range, "Reviewed listing" if approved (when data exists)
4. Surface **deposit** prominently in booking panel when set
5. Gallery: swipe-friendly thumbnails on mobile; maintain aspect ratio consistency
6. Keep **footer hidden** on this route (already implemented)
7. Verify guest auth dialog + `AuthRedirectService` after sticky CTA submit

### Success criteria

- [ ] Booking CTA visible without scrolling on mobile after initial hero load
- [ ] Trust fields visible before booking panel on mobile
- [ ] Guest → auth → return to listing flow works from sticky CTA
- [ ] Desktop sticky sidebar unchanged and functional
- [ ] No layout jump between skeleton and loaded state

### Pages / components affected

| Component | Change |
| --------- | ------ |
| `listing-details-page` | Layout, trust block, sticky bar |
| `booking-panel` | Compact mode for sticky bar |
| `listing-gallery` | Mobile swipe polish |

### Backend required?

**No** — uses existing listing fields. Optional: backend flag for "verified owner" (future).

### Estimated order

**Sprint 3 — Week 3** (highest conversion ROI)

---

## 7. PHASE 4 — CREATE LISTING WIZARD

| | |
|---|---|
| **Sprint** | Week 4 |
| **Backend** | No |
| **Complexity** | Medium-high |
| **Risk** | Medium |

### Objective

Replace the long single-page form with a guided wizard that reduces abandonment for owners.

### Business value

More supply — easier listing = more toys available to rent.

### UX impact

**High** for owners; low for renters.

### Engineering complexity

**Medium-high** — form state refactor across steps; must preserve existing API contract.

### Implementation risk

**Medium** — regression risk on create + image upload flow. **Do not change API payload shape.**

### Dependencies

- Phase 1 form tokens stable
- Existing `create-listing-form` logic and validators

### Tasks (ordered)

1. Split form into steps: **Photos → Details → Pricing → Trust & Safety → Review**
2. Add progress stepper (PrimeNG Steps or custom — match DESIGN_RULES)
3. Sticky bottom bar: Back | Continue | Submit on mobile
4. **Client-side draft** in `sessionStorage` (optional MVP): restore on return; clear on success
5. Preserve reserved validation slots — no layout shift per step
6. Keep hardcoded Armenia/Yerevan in submit payload (MVP)
7. Retain post-submit toast + redirect to My Toys + image upload failure handling

### Success criteria

- [ ] User can complete listing in ≤5 minutes on mobile
- [ ] Validation fires per-step with clear errors
- [ ] Successful submit behavior unchanged (PendingApproval)
- [ ] Image upload failure still creates listing with warning toast
- [ ] No new backend endpoints required

### Pages / components affected

| Component | Change |
| --------- | ------ |
| `create-listing-page` | Wizard shell, stepper |
| `create-listing-form` | Split into step sub-components or internal steps |

### Backend required?

**No** — same `POST /api/listings` + image upload.

### Estimated order

**Sprint 4 — Week 4**

---

## 8. PHASE 5 — MY TOYS DASHBOARD IMPROVEMENTS

| | |
|---|---|
| **Sprint** | Week 5 |
| **Backend** | Mostly no |
| **Complexity** | Low-medium |
| **Risk** | Low |

### Objective

Give owners clarity on listing status, next actions, and motivation to keep supply active.

### Business value

Supply retention — owners understand pending/rejected states and relist.

### UX impact

**Medium-high** for owners.

### Engineering complexity

**Low-medium** — mostly UI + existing `GET /api/listings/mine` data.

### Implementation risk

**Low** — isolated feature.

### Dependencies

- Phase 1 card/badge consistency
- `my-listing-card`, `ui-badge` tones

### Tasks (ordered)

1. Add **"List a Toy" CTA** to empty state (currently missing)
2. Status filter tabs: All | Pending | Approved | Rejected (client-side filter on existing data)
3. Rejected cards: show rejection reason if backend returns it (display only when present)
4. Pending cards: explanatory banner "Under review — usually within 24–48 hours"
5. Align `my-listing-card` visual with browse `listing-card` family
6. **Earnings visibility** — UI placeholder or "Coming soon" unless backend exposes earnings (do not fake numbers)

### Success criteria

- [ ] Empty state has primary CTA
- [ ] Owner understands status of each toy without leaving page
- [ ] Mobile grid readable at 375px
- [ ] Edit/archive actions still work

### Pages / components affected

| Component | Change |
| --------- | ------ |
| `my-listings-page` | Filters, empty CTA, banners |
| `my-listing-card` | Visual alignment, rejection reason |

### Backend required?

**Mostly no.** Rejection reason display = **yes, only if** backend already returns it on mine endpoint; otherwise skip until API adds field.

### Estimated order

**Sprint 5 — Week 5**

---

## 9. PHASE 6 — ADMIN MODERATION UX

| | |
|---|---|
| **Sprint** | Week 6 |
| **Backend** | Yes (Admin role on `/api/auth/me`) |
| **Complexity** | Low |
| **Risk** | Low |

### Objective

Polish the existing moderation queue for speed and mobile usability — not a rebuild.

### Business value

Faster approvals = faster time-to-market for new listings = more inventory.

### UX impact

**High** for admin operators; indirect for users.

### Engineering complexity

**Low** — enhancements to working admin feature.

### Implementation risk

**Low** — approve/reject API already wired.

### Dependencies

- Admin role assignment on backend (known issue: empty roles until backend assigns Admin)
- Phase 1 badge/token consistency

### Tasks (ordered)

1. Mobile: full-width approve/reject buttons stacked; image gallery tap-to-zoom
2. Queue header: pending count + last refreshed timestamp
3. Highlight missing hygiene/age/condition with warning callout (soft, not blocking)
4. Keyboard shortcuts on desktop: `A` approve, `R` reject (optional, low priority)
5. Empty queue state: positive messaging "All caught up"
6. Confirm reject modal validation UX (already exists — polish copy + mobile layout)

### Success criteria

- [ ] Admin can moderate a listing on mobile in <60 seconds
- [ ] Approve/reject toasts and list refresh work
- [ ] Non-admin users never see admin routes
- [ ] Image-first layout preserved

### Pages / components affected

| Component | Change |
| --------- | ------ |
| `pending-listings-page` | Queue header, empty state |
| `pending-listing-card` | Mobile actions, trust callouts |

### Backend required?

**No** for UI polish. **Yes** for reliable Admin role on `/api/auth/me`.

### Estimated order

**Sprint 6 — Week 6** (can overlap lightly with Phase 5 if different developer)

---

## 10. PHASE 7 — EMPTY STATES & TRUST POLISH

| | |
|---|---|
| **Sprint** | Week 7 |
| **Backend** | No |
| **Complexity** | Low |
| **Risk** | Very low |

### Objective

Eliminate dead-end screens; reinforce trust messaging across browse, bookings, and errors.

### Business value

Reduced bounce rate; improved first-session confidence.

### UX impact

**Medium** — cumulative across all pages.

### Engineering complexity

**Low** — component reuse.

### Implementation risk

**Very low**.

### Dependencies

- `app-ui-empty-state` shared component
- Phases 3–5 complete (so CTAs link to real improved flows)

### Tasks (ordered)

1. Audit all pages for empty/error/loading: listings, bookings, requests, favorites, admin, profile
2. Standardize on `app-ui-empty-state` + primary CTA per DESIGN_RULES §14
3. Browse zero-results: "Clear filters" + link to all toys
4. Bookings empty: "Browse toys" CTA
5. Add subtle trust strip on homepage: "Every listing reviewed" (copy only, honest)
6. Loading skeletons: ensure geometry matches content (listings grid, detail page already good — extend to bookings)
7. i18n: all new strings in en/ru/hy

### Success criteria

- [ ] No screen ends without explanation + next action
- [ ] Empty states use consistent icon/title/body/CTA pattern
- [ ] Trust copy is accurate (moderation exists; no fake verification)

### Pages / components affected

All feature pages with list views; `empty-state` component extensions if needed.

### Backend required?

**No**.

### Estimated order

**Sprint 7 — Week 7**

---

## 11. PHASE 8 — PRODUCTION STABILIZATION

| | |
|---|---|
| **Sprint** | Week 8 |
| **Backend** | Yes |
| **Complexity** | Medium |
| **Risk** | Medium |

### Objective

Make the app launch-safe: performance, accessibility, edge cases, deployment config.

### Business value

Prevents launch-day embarrassment and support load.

### UX impact

**Medium** — invisible when done right.

### Engineering complexity

**Medium** — cross-cutting QA and config.

### Implementation risk

**Medium** if scope creeps into rewrites — **stay in fix-only mode**.

### Dependencies

- Phases 1–7 complete
- Production API URL configured

### Tasks (ordered)

1. **Responsive QA matrix**: 375, 390, 768, 1024, 1440 — document pass/fail
2. **Accessibility pass**: focus rings, aria labels, contrast on primary buttons, form labels
3. Fix bundle budget warnings OR adjust `angular.json` thresholds with justification
4. `environment.prod.ts`: real `apiBaseUrl`
5. Edge cases: expired token logout, 404 listing, network error retry on all main pages
6. **Analytics hooks** (frontend-only): data-layer events for `view_listing`, `start_booking`, `submit_listing`, `approve_listing` — no vendor lock until chosen
7. Smoke test all guards: guest, auth, admin redirects
8. Update README to match current behavior (auth redirect, footer rules, home route)

### Success criteria

- [ ] Production build succeeds
- [ ] Critical paths manually tested on real mobile device
- [ ] No console errors on happy paths
- [ ] WCAG AA on primary flows (best effort for MVP)
- [ ] README accurate

### Pages / components affected

Cross-cutting; `environment.prod.ts`, `angular.json`, `README.md`.

### Backend required?

**Yes** — production API must be deployed and CORS-configured.

### Estimated order

**Sprint 8 — Week 8**

---

## 12. PHASE 9 — REAL USER TESTING PREPARATION

| | |
|---|---|
| **Sprint** | Week 9 |
| **Backend** | Yes |
| **Complexity** | Low |
| **Risk** | Low |

### Objective

Validate MVP with real Armenian users before public launch.

### Business value

De-risk UX assumptions; seed initial supply and demand.

### UX impact

**Validation** — may produce small copy/layout fixes only.

### Engineering complexity

**Low** — mostly process; code changes should be ≤1 sprint of fixes.

### Implementation risk

**Low** if scope locked to "fix list from tests" only.

### Dependencies

- Phase 8 complete
- Backend staging environment
- At least one Admin user configured

### Tasks (ordered)

1. Define **5 test scenarios**: guest browse→detail, guest book→register→return, owner list toy, owner manage booking request, admin moderate
2. Recruit 5–8 participants (Armenian parents); mix RU/HY/EN
3. Supply onboarding: help 10–20 real toy listings through moderation
4. Track drop-off points (spreadsheet if analytics not wired)
5. Fix-only sprint from findings — **no new features**
6. Launch checklist sign-off (Section 18)

### Success criteria

- [ ] ≥80% of test users complete primary task without assistance
- [ ] ≥10 approved listings live
- [ ] ≥1 successful rental request end-to-end (even if offline handoff)
- [ ] Critical findings fixed or documented as post-launch

### Pages / components affected

Targeted fixes only — determined by test results.

### Backend required?

**Yes** — staging + admin roles + real categories.

### Estimated order

**Sprint 9 — Week 9**

---

## 13. BACKEND-DEPENDENT FUTURE FEATURES

**Not for current MVP launch.** Do not start frontend work until backend contract exists.

| Feature | Why deferred | Backend needed |
| ------- | ------------ | -------------- |
| **Payments** | MVP uses offline/cash handoff between parties | Payment provider, escrow, webhooks |
| **Chat (UI prominence)** | Code exists; hidden from nav intentionally | Stable messaging, push |
| **Advanced search** | Title + filters sufficient for Armenia MVP | Full-text, geo, facets |
| **Recommendations** | Homepage sections enough for now | ML or rules engine |
| **Ratings / reviews** | Trust via moderation + hygiene first | Review model, aggregation |
| **Notifications** | Email assumed backend-side for moderation | Push, email templates, preferences |
| **Advanced availability** | Basic booked dates on calendar work | iCal, buffer days, min rental |
| **Verified owner badge** | Don't fake trust signals | Verification workflow |
| **Earnings dashboard** | No payment data yet | Transaction history API |
| **Edit listing after approve** | May exist partially — confirm API | PUT/PATCH listing contract |
| **Social auth (Google/Apple)** | Disabled in UI | OAuth credentials + stable config |
| **Favorites page in nav** | Nice-to-have | Already has API — product decision only |
| **Multi-city / geo browse** | MVP is Yerevan-focused | Location index |

---

## 14. FEATURES TO EXPLICITLY AVOID FOR NOW

### Do NOT build (MVP stage)

| Feature | Reason |
| ------- | ------ |
| **Full app redesign** | Token stabilization + targeted page work is enough |
| **Second design direction** | Direction A only |
| **New state management** | NgRx stays |
| **Component library swap** | PrimeNG stays |
| **Micro-frontend split** | Overengineering |
| **Native mobile app** | Responsive web first |
| **AI chatbot / support bot** | Distraction |
| **Owner analytics dashboard** | Premature |
| **Dynamic pricing / surge** | Premature |
| **Insurance integration** | Legal + backend complexity |
| **Multi-vendor admin roles** | Single admin role sufficient |
| **Infinite scroll rewrite** | Load-more works |
| **GraphQL migration** | REST works |
| **Dark mode** | Not in DESIGN_RULES |
| **Re-enabling social login UI** | Until credentials stable |

### Premature optimization

- Extracting every card into a monorepo package
- 100% unit test coverage before launch
- Perfect Lighthouse 100 — aim for "good enough on 4G mobile"
- Category-specific card layouts per toy type

### What causes chaos at MVP stage

- Building chat + payments + ratings simultaneously
- AI-generated one-off components without token usage
- Per-page custom button styles
- Rewriting admin while polishing owner flows
- Adding bottom nav and sticky booking in the same PR without QA

---

## 15. IMPLEMENTATION SAFETY RULES

1. **No broad rewrites** — max ~5–8 files per PR unless Phase 1 token sweep
2. **Incremental commits** — one phase task per PR where possible
3. **Preserve working flows** — run manual smoke test after every merge: login, browse, detail, book, create, moderate
4. **Mobile QA after every phase** — 375px screenshot or device check mandatory
5. **Design consistency validation** — PR must reference DESIGN_RULES section affected
6. **No component duplication** — extend `shared/ui` before creating `listing-card-v2`
7. **No backend changes from frontend team** without API contract document
8. **No fake data** — show fields only when API returns them
9. **i18n mandatory** — no English-only strings in templates
10. **Feature flags over half-shipped UI** — hide incomplete flows, don't expose broken nav links

---

## 16. CURSOR + CLAUDE EXECUTION WORKFLOW

### Before any AI-assisted task

1. Read **`DESIGN_RULES.md`** (relevant sections)
2. Read **`IMPLEMENTATION_ROADMAP.md`** (current phase only)
3. Identify affected files — **list them in the prompt**
4. State constraints: "frontend-only", "no API changes", "Direction A only"

### Prompt structure (template)

```text
Phase: [N — name]
Task: [specific task from roadmap]
Files allowed: [explicit list]
Must preserve: [flows, API shapes]
Design reference: DESIGN_RULES.md §[X]
Do NOT: [scope exclusions]
Verify: npm run build + mobile 375px
```

### Incremental implementation

- One task per AI session when possible
- After AI generates code: run `npm run build`
- Manually test the changed flow
- Fix before moving to next task

### Avoiding AI chaos

- Never prompt "redesign the entire app"
- Never prompt "make it look modern" without citing DESIGN_RULES tokens
- Reject outputs that introduce new hex colors, `any` types, or HttpClient in components
- Reject duplicate components when extension works
- If AI proposes new dependencies — reject unless explicitly approved

### Using DESIGN_RULES.md

| Topic | Section |
| ----- | ------- |
| Colors/spacing/type | §4–§7 |
| Cards | §10 (mandatory for listing surfaces) |
| Booking | §12 |
| AI rules | §19 checklist before merge |

### QA after AI changes

| Check | How |
| ----- | --- |
| Build | `npm run build` |
| Mobile layout | Browser devtools 375px |
| Auth flow | Guest book → login → return |
| i18n | Spot-check RU/HY keys exist |
| Regression | Browse → detail → back |

---

## 17. RECOMMENDED IMPLEMENTATION ORDER

### Exact sequence

| Order | Phase | Unlocks |
| ----- | ----- | ------- |
| 1 | Design foundation stabilization | Consistent AI output, all UI phases |
| 2 | Mobile navigation & core UX | Safe-area for sticky CTAs |
| 3 | Listing details optimization | Conversion baseline for launch |
| 4 | Create listing wizard | Supply growth |
| 5 | My Toys dashboard | Owner retention |
| 6 | Admin moderation UX polish | Faster inventory approval |
| 7 | Empty states & trust polish | Launch-quality feel |
| 8 | Production stabilization | Deploy |
| 9 | Real user testing prep | Public launch |

### What must happen first

1. **Token stabilization (Phase 1)** — everything else gets cheaper
2. **Bottom nav (Phase 2)** — before mobile sticky booking bar
3. **Listing details (Phase 3)** — highest ROI before owner-side wizard

### What can wait

- Admin polish (Phase 6) if admin is single operator on desktop
- Favorites nav exposure — post-launch
- Featured tile animation polish — post-launch

### Parallelization (only if 2+ developers)

- Phase 5 (My Toys) + Phase 6 (Admin) can run in parallel **after Phase 1**
- Never parallelize Phase 2 and Phase 3 on same shell files

---

## 18. MVP LAUNCH CHECKLIST

### UX readiness

- [ ] Guest can browse and view toy details
- [ ] Guest can initiate booking and complete auth return flow
- [ ] Authenticated user can submit rental request
- [ ] Owner can list toy with photos
- [ ] Owner sees pending/approved/rejected status
- [ ] Admin can approve/reject with reason
- [ ] All empty states have CTAs
- [ ] Info pages accessible from footer

### Mobile readiness

- [ ] Bottom nav works (post Phase 2)
- [ ] Sticky booking CTA on detail (post Phase 3)
- [ ] Forms usable with mobile keyboard
- [ ] Touch targets ≥44px on primary actions
- [ ] Tested on real iOS + Android device

### Moderation readiness

- [ ] Admin role assigned on backend
- [ ] Pending queue loads
- [ ] Reject reason saved and visible to owner (if API supports)
- [ ] ≥10 listings approved for launch content

### Listing readiness

- [ ] Categories load from API
- [ ] Images upload after create (graceful failure handled)
- [ ] Toy trust fields display on detail when provided
- [ ] Search by title works from homepage

### Deployment readiness

- [ ] `environment.prod.ts` API URL set
- [ ] CORS configured on backend
- [ ] Production build passes
- [ ] HTTPS enforced

### Trust readiness

- [ ] FAQ covers hygiene, moderation, deposits
- [ ] Terms + Privacy linked in footer
- [ ] No fake verification badges
- [ ] Moderation messaging on create success

---

## 19. POST-LAUNCH ROADMAP (HIGH LEVEL)

### Q1 after launch — Retention & trust

- Ratings/reviews (backend + detail/card UI)
- Email/in-app notifications for booking status
- Re-enable favorites in nav
- Rejection reason on owner dashboard (if API added)

### Q2 — Category expansion

- Sports equipment, skates, balls — **same card/booking patterns**, category-driven attributes
- Optional attribute schema (age → size, condition → sport-specific)
- Homepage sections per vertical

### Q3 — Transactions & communication

- Payment integration (local providers relevant to Armenia)
- Chat prominence in nav (already wired)
- Verified owner program

### Q4 — Platform scale

- Advanced filters and search
- Mobile app (React Native / Capacitor) reusing design tokens export from `styles.css`
- Multi-city support beyond Yerevan
- Recommendation engine for homepage

**Rule:** Each expansion reuses Phase 1–3 patterns — new verticals change **data and copy**, not layout architecture.

---

## 20. FINAL PRODUCT STRATEGY SUMMARY

### What this product should become

ToyRent is a **trust-first, mobile-first rental marketplace** where Armenian families safely share child toys — expanding later to sports gear and general rentable consumer items — without becoming a complex enterprise platform.

### Why mobile-first matters

The primary user is a parent on a phone, browsing during short moments. Navigation, booking, and listing must work one-handed, with CTAs in thumb reach. Desktop is a enhancement layer, not the design target.

### Why trust-first matters

Renting used toys for children triggers hygiene and safety anxiety. Moderation, visible condition/hygiene notes, clear pricing, and honest empty states convert better than discounts or flashy UI.

### Why incremental implementation matters

The codebase already implements the full MVP loop. The fastest path to launch is **stabilize → mobile polish → convert → supply → harden → test** — not rebuild. Each phase in this roadmap delivers shippable value with bounded risk.

### Success definition for MVP launch

A parent in Yerevan can find a toy, trust what they see, request a rental on mobile, and an owner can list and get approved — with admin moderation keeping quality high — all within a cohesive Direction A experience.

---

## APPENDIX — PHASE SUMMARY TABLE

| Phase | Sprint | Focus | FE/BE | Complexity | Risk | Conversion | Trust |
| ----- | ------ | ----- | ----- | ---------- | ---- | ---------- | ----- |
| 1 | W1 | Design tokens | FE | Low | Low | Low | Medium |
| 2 | W2 | Mobile nav | FE | Medium | Medium | Medium | Low |
| 3 | W3 | Detail page | FE | Medium | Medium | **Very high** | **High** |
| 4 | W4 | Create wizard | FE | Med-High | Medium | Medium | Medium |
| 5 | W5 | My Toys | FE (+BE?) | Low-Med | Low | Medium | Medium |
| 6 | W6 | Admin polish | FE | Low | Low | Low | **High** |
| 7 | W7 | Empty/trust | FE | Low | Very low | Medium | **High** |
| 8 | W8 | Production | FE+BE | Medium | Medium | — | — |
| 9 | W9 | User testing | Process | Low | Low | Validation | Validation |

---

*Last updated: May 2026 — ToyRent MVP (child toys rental, Armenia)*  
*Visual direction: Direction A — Refined Warm (`DESIGN_RULES.md`)*
