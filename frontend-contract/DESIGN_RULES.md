# DESIGN_RULES.md

> **Permanent UI/UX source of truth** for the ToyRent rental marketplace (Angular + NgRx + ASP.NET Core).

| | |
|---|---|
| **Official visual direction** | Direction A — **Refined Warm** (only). Do not introduce alternate themes, palettes, or stylistic experiments. |
| **Audience** | Cursor AI, Claude Code, frontend developers, Figma updates, future mobile app |
| **Current niche** | Child toys rental in Armenia (Yerevan MVP) |
| **Future scale** | Sports equipment, skates, balls, hobby items, general rentable consumer goods |

---

## Table of contents

1. [Product Design Philosophy](#1-product-design-philosophy)
2. [Core UX Principles](#2-core-ux-principles)
3. [Mobile-First Rules](#3-mobile-first-rules)
4. [Color System](#4-color-system)
5. [Typography System](#5-typography-system)
6. [Spacing System](#6-spacing-system)
7. [Radius System](#7-radius-system)
8. [Shadow System](#8-shadow-system)
9. [Button System](#9-button-system)
10. [Card System](#10-card-system)
11. [Form System](#11-form-system)
12. [Booking UX Rules](#12-booking-ux-rules)
13. [Trust System UX](#13-trust-system-ux)
14. [Empty States](#14-empty-states)
15. [Admin UX Rules](#15-admin-ux-rules)
16. [Navigation System](#16-navigation-system)
17. [Animation & Interaction Rules](#17-animation--interaction-rules)
18. [Accessibility Rules](#18-accessibility-rules)
19. [AI-Assisted Development Rules](#19-ai-assisted-development-rules)
20. [Prohibited Patterns](#20-prohibited-patterns)
21. [Future Scalability Rules](#21-future-scalability-rules)
- [Appendix A — File Reference](#appendix-a--file-reference)
- [Appendix B — Quick Decision Guide](#appendix-b--quick-decision-guide)

---

## 1. PRODUCT DESIGN PHILOSOPHY

### Emotional goals

- Feel **safe, warm, and family-friendly** — parents should feel comfortable renting toys for their children.
- Reduce anxiety around hygiene, condition, and unknown owners through visible trust signals.
- Keep interactions **light and optimistic** — renting should feel easier than buying clutter.

### Marketplace goals

- **Discovery → trust → booking** is the primary funnel.
- Owners can list toys quickly; admins can moderate confidently.
- Every screen should answer: *What is this? Can I trust it? What do I do next?*

### Trust principles

1. **Trust before click** — show condition, age range, hygiene notes, moderation status, and owner identity before requiring commitment.
2. **Moderation is a feature** — communicate that listings are reviewed (PendingApproval → Approved).
3. **No hidden pricing** — daily rate, deposit (when set), and rental total must be visible before submit.
4. **Honest empty states** — never imply data exists when it does not.

### UX priorities (ordered)

1. Mobile-first layout and touch behavior
2. Booking / listing conversion clarity
3. Trust signal visibility
4. Loading and error resilience
5. i18n-ready copy (EN / RU / HY)
6. Desktop enhancement (never desktop-only assumptions)

### Mobile-first philosophy

- Design for **320–430px width first**, then scale up.
- Primary actions live in **thumb reach** (bottom area on mobile, sticky panels where appropriate).
- One primary CTA per viewport section.
- Avoid hover-only affordances; every action must work on touch.

### Armenian audience considerations

- **Simple language** — short sentences, no jargon; all user-facing text via `ngx-translate`.
- **Low cognitive load** — fewer fields, progressive steps, obvious next actions.
- **Local context** — MVP hardcodes Armenia/Yerevan where location is not yet differentiated; do not over-emphasize location browsing.
- **Multilingual by default** — EN, RU, HY must remain layout-safe (longer strings must not break cards or buttons).

---

## 2. CORE UX PRINCIPLES

| Rule | Requirement |
| ---- | ----------- |
| **Booking CTA visibility** | On listing details, price + "Request rental" (or equivalent) must be visible without scrolling on mobile after hero, or in a clearly associated panel. |
| **Trust before click** | Cards and detail pages surface category, condition, age, hygiene, owner, and moderation context where data exists. |
| **No dead-end flows** | Empty, error, and success states always offer a next step (CTA link or button). |
| **Progressive disclosure** | Show essentials first; advanced filters, safety notes, and metadata in expandable sections. |
| **Minimal cognitive load** | One task per screen section; forms grouped into labeled cards. |
| **Conversion-oriented layouts** | Listing details: gallery → title/price → trust details → booking. No competing side quests. |
| **Family-friendly tone** | Warm, reassuring copy. Avoid corporate, crypto, or aggressive sales language. |
| **Footer for trust pages** | Global footer on all pages **except** `/listings/:id` (conversion focus). |
| **Guest rental intent** | Guests see the booking panel; auth is requested on submit via dialog, then return to the same listing. |

---

## 3. MOBILE-FIRST RULES

### Touch targets

- **Minimum interactive size:** 44×44px (use padding to achieve this even for icon buttons).
- **Preferred primary button height:** 48px (`min-height: 3rem` for booking CTA).
- **Icon-only buttons:** 32px visual with 44px hit area via padding or `min-width/min-height`.

### Sticky behavior

| Element | Mobile | Desktop (≥1100px) |
| ------- | ------ | ----------------- |
| **Header** | Sticky top, compact height (~3.5rem) | Sticky top (~4rem) |
| **Booking panel** | Flows in document order after main content | Sticky sidebar (`top: calc(4.25rem + 20px)`) |
| **Form submit** | Full-width at section bottom | Same; avoid floating unless sticky bottom bar is implemented consistently |
| **Global footer** | Full-width stacked columns | Centered multi-column |

### Bottom navigation (current + future)

- **Current MVP:** Primary nav in sticky header; mobile uses burger drawer.
- **Future mobile app / PWA direction:** Bottom nav with max 4 items (Home, Browse, My Toys, Bookings) + centered "List" action. When implemented, bottom nav replaces duplicate header links on mobile only.
- Do not add bottom nav until spec is unified — but **reserve 64px + safe-area** in mobile layouts where planned.

### Spacing rhythm (mobile)

- Page horizontal padding: `16px` (`--ui-space-16`) below 900px.
- Section vertical gap: `24px–40px`.
- Card internal padding: `12px–16px` on mobile, `16px–20px` on desktop.

### Card behavior (mobile)

- Full-width in single column below 560px.
- 2-column grid only above 560px for listing browse.
- Cards are **equal height in a row** (`height: 100%` on card shell).
- Tap feedback: subtle scale/shadow; no hover dependency.

### Form behavior (mobile)

- Single-column fields below 640px.
- Inputs use full width; labels above fields.
- Validation messages in **reserved slots** (fixed `padding-bottom` on field) — no layout shift.
- Textareas: `min-height: 8rem`, vertically resizable.

### Responsive breakpoints (canonical)

| Token | Range | Usage |
| ----- | ----- | ----- |
| `xs` | `< 560px` | Single-column cards, compact hero |
| `sm` | `560–639px` | 2-col listing grid |
| `md` | `640–899px` | Form grid collapse, auth stack |
| `lg` | `900–1099px` | Page container padding increase, footer stack |
| `xl` | `≥ 1100px` | Listing details 2-col + sticky booking sidebar |
| `shell-md` | `960px` | Header nav visibility toggles |

Use these consistently. Avoid introducing new arbitrary breakpoints.

### Desktop adaptation philosophy

- Desktop **adds horizontal space**, not new flows.
- Multi-column grids and sticky sidebars are enhancements.
- Never hide mobile-critical actions on desktop-only hover menus without a touch-accessible alternative.

---

## 4. COLOR SYSTEM

**Direction A — Refined Warm.** All colors must come from CSS custom properties in `src/styles.css`. Extend tokens there; do not hardcode one-off hex values in components.

### Core palette

| Token | Value | Usage |
| ----- | ----- | ----- |
| `--ui-color-primary` | `#ff6008` | Primary CTAs, brand accent, active states |
| `--ui-color-primary-strong` | `#e85500` | Hover/pressed primary, price emphasis |
| `--ui-color-primary-soft` | `#fd8b47` | Gradients, category chips, warm highlights |
| `--ui-color-secondary` | `#2a2c41` | Headings on marketing tiles, text buttons |
| `--ui-color-background` | `#111217` | App chrome backdrop (outside content areas) |
| `--ui-color-surface` | `#ffffff` | Cards, panels, inputs |
| `--ui-color-surface-muted` | `#f4f4f8` | Page backgrounds, summary blocks |
| `--ui-color-footer` | `#7b7a7a` | Footer bar, de-emphasized meta text |
| `--ui-color-text-primary` | `#181a24` | Body headings, primary text |
| `--ui-color-text-secondary` | `#6a6f86` | Secondary labels, captions |
| `--ui-color-text-muted` | `#b5b6b7` | Placeholders, disabled meta |
| `--ui-color-border` | `#e6e8f1` | Card/input borders |
| `--ui-color-border-strong` | `#b5b6b7` | Emphasized dividers |
| `--ui-color-error` | `#d43f5e` | Errors, destructive accents |

### Semantic / trust colors

| Role | Colors | Usage |
| ---- | ------ | ----- |
| **Success** | `#1f8a4c` on `#edf9f1` / border `#c6ebd2` | Booking success, approved badges |
| **Warning / pending** | `#8a5c00` on `#fff4d6` | Pending moderation badge |
| **Approved** | `#0e6245` on `#dff8ee` | Approved status badge |
| **Rejected** | `#8a2133` on `#ffe5ea` | Rejected status badge |
| **Neutral badge** | `--ui-color-text-secondary` on `#eef1fa` | Category/meta chips |
| **Error surface** | `#fff1f3` / border `#f6ccd3` | Inline form/booking errors |

### Allowed gradients

- **Brand mark / hero accents:** `135deg, primary-soft → primary → primary-strong`
- **Hero overlay:** `rgba(42, 44, 65, 0.55)` warm navy scrim over photography
- **Empty/image placeholders:** `color-mix` with `surface-muted` + soft lavender or warm tint
- **Auth marketing panel:** solid primary orange (`#ff6a00` family) — no rainbow gradients

### Prohibited color usage

- Neon, crypto-style purple/teal gradients
- Pure black `#000` for text (use `--ui-color-text-primary`)
- Red/green alone without background/border (fails accessibility and feels alarming)
- Random per-component hex overrides
- Dark-mode palette (not in MVP — do not introduce without full system update)

---

## 5. TYPOGRAPHY SYSTEM

**Font stack:** `'Inter', 'Segoe UI', Roboto, Arial, sans-serif` (global in `styles.css`).

| Role | Token / size | Weight | Line height | Usage |
| ---- | ------------ | ------ | ----------- | ----- |
| **H1** | `--ui-font-size-h1` → `clamp(1.75rem, 3vw, 2.25rem)` | 700 | 1.1–1.15 | Page heroes, auth welcome |
| **H2** | `--ui-font-size-h2` → `clamp(1.375rem, 2.5vw, 1.75rem)` | 700 | 1.2 | Section titles |
| **H3 / card title** | `--ui-font-size-h3` → `1.125rem` | 600–700 | 1.25–1.3 | Listing card title, empty state title |
| **Body** | `--ui-font-size-body` → `1rem` | 400–500 | 1.5 | Paragraphs, descriptions |
| **Caption / label** | `--ui-font-size-caption` → `0.875rem` | 500–600 | 1.4 | Labels, meta, breadcrumbs |
| **Badge** | `0.75rem` | 600 | 1 | Status badges |
| **Button** | `0.875rem`–`15px` | 600 | 1.2 | All button labels |
| **Price (hero)** | `30px` desktop / `24px` mobile | 700 | 1.1 | Booking panel primary price |
| **Price (card)** | `0.9rem`–`1rem` | 700 | 1.2 | Listing card footer price |

### Hierarchy rules

- One H1 per page.
- Section titles use H2; card titles use H3 styling (may be `<h3>` in markup).
- **Price is always visually secondary to title on cards**, primary in booking panel.
- Truncate long titles: 2-line clamp on cards (`-webkit-line-clamp: 2`).
- Letter-spacing: `-0.01em` to `-0.02em` on large headings only.

### Mobile typography scaling

- Use `clamp()` for hero headings; never fixed oversized text below 400px width.
- Minimum readable body: **16px** on mobile (avoid `14px` for long paragraphs; OK for meta/captions).

---

## 6. SPACING SYSTEM

**Base unit:** 4px. Use tokens only.

| Token | Value |
| ----- | ----- |
| `--ui-space-4` | 4px |
| `--ui-space-8` | 8px |
| `--ui-space-12` | 12px |
| `--ui-space-16` | 16px |
| `--ui-space-24` | 24px |
| `--ui-space-32` | 32px |
| `--ui-space-48` | 48px |
| `--ui-space-64` | 64px |

> **Note:** Some components reference `--ui-space-20` and `--ui-space-6`. When touching those files, migrate to the scale above or add missing tokens to `styles.css` — do not introduce ad-hoc `18px`, `22px`, etc.

### Application

| Context | Mobile | Desktop |
| ------- | ------ | ------- |
| **Page padding** | 16px inline | 24px inline (`.page-container`) |
| **Section stack gap** | 24px | 24–48px (`.section-stack`) |
| **Card grid gap** | 16–24px | 24px (`minmax(280px, 1fr)`) |
| **Card body padding** | 12–16px | 16–20px |
| **Form section padding** | 20px | 20px |
| **Form field gap** | 6–8px label-to-input | same |
| **Footer padding** | 32px 20px | 48px 40px |

### Layout utilities (reuse)

- `.page-container` — max width 1200px, centered
- `.section-stack` — vertical flex column with 24px gap
- `.cards-grid` — responsive auto-fit grid
- `.ui-card-surface` — standard bordered card shell

---

## 7. RADIUS SYSTEM

| Element | Radius | Token / value |
| ------- | ------ | ------------- |
| **Cards** | 14px | `--ui-radius-md` |
| **Large panels** (booking) | 16–20px | `--ui-radius-lg` (20px) |
| **Buttons (primary/secondary)** | Pill | `999px` / `40px` for inputs |
| **Inputs / selects** | Pill | `40px` (global PrimeNG override) |
| **Textareas** | Soft rect | `--ui-radius-md` (14px) in forms |
| **Modals / dialogs** | 16px | Match auth card radius |
| **Chips / badges** | Pill | `999px` |
| **Avatars** | Circle | `50%` |
| **Brand mark** | 10px | Slightly squarer than cards |
| **Thumbnail gallery** | 4px | Detail skeleton hero only |
| **Small focus rings** | 4–6px | `--ui-radius-sm` |

**Rule:** Cards and inputs share the same soft family (14px+). Buttons and filters are pill-shaped. Do not mix sharp 0px corners with this system.

---

## 8. SHADOW SYSTEM

| Token / usage | Value | When |
| ------------- | ----- | ---- |
| `--ui-shadow-card` | `0 10px 24px rgba(16, 18, 30, 0.07)` | Default cards, panels |
| `--ui-shadow-card-hover` | `0 16px 34px rgba(16, 18, 30, 0.13)` | Card hover (desktop) |
| **Booking panel** | `0 4px 6px rgba(0,0,0,0.08), 0 24px 48px rgba(16,18,30,0.08)` | Conversion panel emphasis |
| **Primary button** | `0 4px 14px rgba(255, 96, 8, 0.28)` | Header CTA |
| **Booking CTA** | `0 10px 24px rgba(255, 96, 8, 0.28)` | Stronger conversion shadow |
| **Sticky header** | `0 8px 24px rgba(16, 18, 30, 0.05)` | App shell |
| **Auth shell** | `0 20px 48px rgba(4, 8, 18, 0.35)` | Login/register container |
| **Modal** | Use PrimeNG default + border | No heavy drop shadow stacks |

### Prohibited shadow behavior

- Multiple stacked colored glows
- `box-shadow` on every nested element
- Shadow as the only depth cue on mobile (border + background must suffice)
- Animated shadow pulsing

---

## 9. BUTTON SYSTEM

### Variants

| Variant | Class / pattern | Appearance | Use |
| ------- | --------------- | ---------- | --- |
| **Primary** | `.app-shell__btn--primary` / `.p-button` default | Orange fill, white text, soft glow | Main CTAs: Book, Submit listing, Register |
| **Secondary / outlined** | `.p-button-outlined` | White fill, gray border, dark text | Secondary actions |
| **Ghost** | `.app-shell__btn--ghost` | Transparent, border only | Login, cancel |
| **Soft** | Surface muted background | `--ui-color-surface-muted` fill | Tertiary filters (future) |
| **Destructive** | Red text/background via error tokens | — | Reject, delete, logout in dropdown |
| **Icon** | `.listing-card__fav-btn`, circular 32–44px | — | Heart, menu, close — Favorites, toolbar |

### Sizing

- Default button: `min-height: 2.35rem`, `padding-inline: 1rem`
- Booking CTA: `min-height: 3rem`, full width
- Header "+ List a Toy": compact with icon; text hidden on very narrow desktop (`960px` rules)

### States

- **Hover:** darken to `primary-strong`; translateY(-1px) max on primary CTAs only
- **Active:** `translateY(1px)` on shell buttons
- **Disabled:** muted orange `#f4cfb7` for booking; reduced opacity elsewhere; `cursor: not-allowed`
- **Loading:** replace label with spinner; disable interaction; preserve button dimensions
- **Focus:** `outline: 2px solid primary-strong; outline-offset: 3px` OR ring `0 0 0 3px rgba(255, 96, 8, 0.18)`

### Sticky CTA buttons

- Full viewport width on mobile with 16px horizontal inset from container
- Must not overlap safe-area or footer
- Only one sticky CTA per viewport

---

## 10. CARD SYSTEM

**Most critical pattern.** All listing/marketplace tiles must feel like one family.

### Listing card structure (`app-listing-card`)

```
┌─────────────────────────┐
│  Image (16:10)          │  ← edge-to-edge, object-fit: cover
├─────────────────────────┤
│  Title (2-line clamp)   │
│  Location meta (icon)   │  ← optional city
│  Price / day (bold)     │  ← anchored to bottom via flex
├─────────────────────────┤
│              [♥ fav]    │  ← footer row, optional
└─────────────────────────┘
```

### Required fields (display when available)

| Field | Priority | Placement |
| ----- | -------- | --------- |
| Image | P0 | Top, 16:10 aspect ratio |
| Title | P0 | Body, H3 styling |
| Price/day | P0 | Body bottom, `primary-strong` |
| City | P1 | Caption row with map icon |
| Category | P1 | Chip on featured tiles |
| Age range | P2 | Detail page + future card badge |
| Condition | P2 | Detail page + admin moderation |
| Hygiene notes | P2 | Detail page + admin moderation |
| Owner | P1 | Detail page host section |
| Trust/moderation badge | P1 | My Listings, admin cards |

### Image rules

- **Browse/card aspect ratio:** `16 / 10` via `app-ui-image-container`
- **Featured homepage tile:** flexible height within grid cell; maintain cover
- **No-image fallback:** gift icon + muted warm background; never broken-image icon alone
- **Lazy load** all card images

### Pricing hierarchy

1. **Booking panel:** large price → "/ day" suffix → total in summary block
2. **Card:** bold price in `primary-strong`; deposit not on browse cards (detail + booking only)
3. **Admin card:** price + deposit inline in meta grid

### Hover behavior (desktop only)

- `translateY(-2px)` + elevated shadow
- Image scale `1.03` max
- Must not be required to discover actions

### Mobile card behavior

- Full width in column
- No hover transform
- Favorite button remains tappable in footer
- Entire card link area is tap target except favorite (separate button)

### Featured tile variant (`product-card`)

- Used on homepage sections
- Category pill: bordered `primary-soft`
- Slightly different shadow — when refactoring, align toward `--ui-shadow-card` family

---

## 11. FORM SYSTEM

### Structure

- **Section cards:** titled blocks with icon + hint + field grid (see `create-listing-form`)
- **Wizard philosophy (MVP+):** group as Step 1 Photos → Step 2 Details → Step 3 Pricing → Step 4 Trust (condition/hygiene). MVP uses single-page sections; future splits must preserve draft continuity.

### Validation UX

- Validate on submit; show inline errors immediately after first submit attempt
- **Reserved error slot:** `padding-bottom: 1.25rem` on each field — errors appear in-slot without shifting siblings
- Error color: `--ui-color-error`; required asterisk on labels
- Use backend-aligned validators (`minLength`, `maxLength`) matching DTO constraints

### Input hierarchy

1. Section title
2. Section hint (caption color)
3. Label (caption, semibold)
4. Input
5. Error message (caption)

### Sticky bottom actions

- Primary submit full width at form end
- On long mobile forms, consider sticky bottom bar (future) — must include safe-area padding

### Draft-saving philosophy

- MVP: no autosave; user warned on navigate-away (future)
- Post-submit: toast + redirect (e.g., create listing → My Toys)

### Mobile keyboard

- Use appropriate `inputmode`, `autocomplete`, and `type`
- Scroll focused field into view
- Textareas are rectangular (not pill) for multi-line clarity

---

## 12. BOOKING UX RULES

### Layout

- **Desktop (≥1100px):** booking panel in sticky right sidebar
- **Mobile:** panel follows main content (gallery, details, host); consider future bottom sticky summary bar
- **No global footer** on listing details — intentional conversion focus

### Trust communication

- Show assurance row below CTA (lock icon + short trust copy)
- Display condition, age range, hygiene notes, safety notes in main column before booking on mobile
- Host/owner section visible to authenticated users

### Pricing visibility

- Nightly rate always visible at top of panel
- Date picker inline in bordered calendar container
- Summary block: line items + **total** separated by border
- Deposit shown when `depositAmount` is set (detail meta + admin)

### Guest auth flow

1. Guest fills dates in booking panel
2. Submit opens auth dialog (not redirect immediately)
3. Dialog: Sign In / Sign Up
4. `AuthRedirectService` stores return URL
5. After auth, user returns to same listing with panel intact

### Confirmation & loading

- Skeleton mirrors final layout (hero + 2-col grid)
- Submitting: disable CTA, show loading on button
- Success: inline success status in panel (green surface)
- Error: inline error status (red surface) with retry possible

---

## 13. TRUST SYSTEM UX

| Signal | UI treatment |
| ------ | -------------- |
| **Moderation pending** | `ui-badge--pending` / toast after create |
| **Approved listing** | `ui-badge--approved` on owner My Toys cards |
| **Rejected** | `ui-badge--rejected` + reason visible to owner |
| **Condition** | Text on detail page; badge on admin card (New / Like New / Good / Fair) |
| **Hygiene notes** | Dedicated section on detail; required for approval in admin review |
| **Age range** | Detail toy-details grid; admin meta row |
| **Owner identity** | Avatar + name; email visible to admin only |
| **Verified owner (future)** | Badge near host name — do not fake until backend supports |
| **Ratings (future)** | Star + count near title; never block booking flow |

### Emotional trust copy

- **Prefer:** *"Reviewed by our team"*, *"Hygiene notes provided"*, *"Secure rental request"*
- **Avoid:** *"Verified safe"*, *"100% guaranteed"* unless legally backed

---

## 14. EMPTY STATES

Use `app-ui-empty-state` wrapped in `.ui-card-surface` pattern.

### Tone

- Warm, encouraging, never blaming the user
- Icon: PrimeIcons (`pi-inbox`, `pi-search`, `pi-heart`)

### Requirements

| Screen | Title | CTA |
| ------ | ----- | --- |
| Browse (no results) | Adjust filters suggestion | Clear filters / Browse all |
| My Toys (empty) | Encourage first listing | "List a Toy" |
| My Bookings (empty) | Encourage browsing | "Browse Toys" |
| Favorites (empty) | Explain favorites | "Browse Toys" |
| Admin pending (empty) | Moderation complete | — |
| Error states | Explain + retry | Retry button |

**Never** leave a blank white area without message and action.

---

## 15. ADMIN UX RULES

### Principles

- **Moderation-first:** admin nav shows only moderation routes (no marketplace clutter)
- **Image-first:** pending card leads with photo gallery
- **Decision hierarchy:** Approve (primary) vs Reject (destructive) — reject requires reason modal
- **Mobile moderation:** cards stack single column; actions remain full-width buttons

### Pending listing card content order

1. Image / placeholder
2. Title + category badge
3. Description excerpt
4. Meta grid: owner, location, price/deposit, created date
5. Toy details: age, condition, hygiene, safety
6. Action row: Approve | Reject

### Rejection UX

- Modal with required reason textarea
- Toast on success/failure
- Card removed from list on success

---

## 16. NAVIGATION SYSTEM

### Desktop header

- Sticky white bar, brand left, primary nav center-left, actions right
- **Guest:** Login + Register
- **Authenticated parent:** My Toys, Bookings, Requests + "+ List a Toy"
- **Admin:** Pending Moderation only
- Language switcher in header (desktop) / drawer (mobile)

### Mobile

- Burger drawer with nav links, language, auth/account footer
- Do not duplicate 10+ links — max 5 primary destinations

### Footer (global)

- Links: About, FAQ, Terms, Privacy, Partnership, All Categories, social
- Hidden on `/listings/:id` via `showFooter` signal in app shell

### CTA placement

| CTA | Location |
| --- | -------- |
| List a Toy | Header (auth non-admin) |
| Book / Request rental | Listing detail booking panel |
| Browse Toys | Hero, empty states, post-auth fallback |
| Sign In / Up | Header guest + booking guest dialog |

---

## 17. ANIMATION & INTERACTION RULES

### Allowed

- `140ms–220ms ease` transitions on color, shadow, transform
- Card hover lift: max `-2px`
- Page enter: subtle fade + 6px translate (listing details `240ms`)
- Skeleton shimmer via PrimeNG `p-skeleton`
- Header shrink on scroll (shadow intensifies)

### Prohibited

- Bounce, spring overshoot, parallax
- Auto-playing carousels with no user control
- Loading spinners longer than 300ms without skeleton
- Motion that cannot be disabled (respect `prefers-reduced-motion` when adding global motion)

### Loading skeleton behavior

- Match final layout geometry (image ratio, text lines, sidebar block)
- Use `aria-busy="true"` and localized `aria-label`
- Swap skeleton → content without layout jump

---

## 18. ACCESSIBILITY RULES

| Rule | Standard |
| ---- | -------- |
| **Contrast** | WCAG AA minimum for body text; primary orange on white for large text/buttons only |
| **Touch targets** | 44×44px minimum |
| **Focus** | Visible focus ring on all interactive elements; never `outline: none` without replacement |
| **Text** | 16px minimum for body on mobile; captions 14px max for short labels |
| **Forms** | `<label for>` on all inputs; `aria-invalid` + error text association |
| **Dialogs** | Focus trap, Esc to close, `aria-modal="true"` |
| **Images** | Meaningful `alt` on listing images; decorative icons `aria-hidden` |
| **i18n** | No hardcoded user-facing strings in templates |

---

## 19. AI-ASSISTED DEVELOPMENT RULES

For **Cursor**, **Claude**, and any AI code generation:

### Architecture (mandatory)

- **Angular standalone components** only; no NgModules
- **NgRx** for server state; components dispatch actions, never call `HttpClient` directly
- **API calls** live in `features/*/services/*-api.service.ts`
- **Routes** lazy-loaded per feature (`routes.ts`)
- **i18n:** all user-visible strings via `'key' | translate`

### Component reuse (mandatory)

Before creating new UI, check `src/app/shared/ui/`:

- `app-ui-avatar`
- `app-ui-badge`
- `app-ui-empty-state`
- `app-ui-image-container`
- `app-ui-loading-skeleton`

Extend these rather than duplicating markup.

### Styling rules

1. **Use CSS variables** from `src/styles.css` — never invent new hex colors in components
2. **Use spacing tokens** — no random `13px`, `17px`, `22px` unless migrating to token
3. **Reuse utilities:** `.page-container`, `.section-stack`, `.cards-grid`, `.ui-card-surface`
4. **PrimeNG:** override via global styles or documented `::ng-deep` in component SCSS; keep pill radius consistent
5. **BEM-like naming:** `block__element--modifier` matching feature prefix (e.g., `listing-card__`, `booking-panel__`)

### Consistency requirements

- Match existing file patterns: `.component.ts`, `.component.html`, `.component.scss`
- `ChangeDetectionStrategy.OnPush` on presentational components
- Signals for local UI state; observables/selectors for store state
- No `any` types

### Predictability checklist (AI must verify)

- [ ] Tokens used instead of hardcoded colors/spacing
- [ ] Shared UI components reused
- [ ] Mobile layout tested at 375px
- [ ] Empty/loading/error states included
- [ ] Translation keys added to `en.json`, `ru.json`, `hy.json`
- [ ] No footer on listing details if adding page-level chrome
- [ ] Booking/guest auth patterns preserved

### Do not generate

- New global CSS files without updating this document
- Alternate design directions or theme switchers
- Inline styles in templates (except dynamic calculated values)
- Duplicate footer/header inside feature pages

---

## 20. PROHIBITED PATTERNS

| Pattern | Why |
| ------- | --- |
| Dashboard-heavy admin analytics | MVP is moderation workflow, not BI |
| Crypto / Web3 aesthetics | Breaks family trust positioning |
| Over-animation | Distracts from booking conversion |
| Enterprise tables with 12 columns | Use cards + progressive detail |
| Inconsistent spacing/shadows | Erodes marketplace polish |
| Hover-only critical actions | Fails mobile |
| Desktop-first assumptions | Armenian market is mobile-heavy |
| Location-first browsing UX | MVP search is title/name-based |
| Hidden booking panel for guests | Show panel; gate on submit |
| Social login prominence | Disabled in MVP UI |
| Multiple visual directions | Direction A only |
| Hardcoded English strings | Breaks i18n |

---

## 21. FUTURE SCALABILITY RULES

### Categories

- Category chips and filters are **data-driven** — UI must accept new categories without layout changes
- Use neutral badge styling for unknown categories
- Homepage sections (`GET /api/home/sections`) pattern scales to any vertical

### Marketplace growth

- Listing card structure supports any rentable item (image, title, price, meta)
- Toy-specific fields (age, hygiene) become **optional attribute section** for other verticals
- Keep "trust details" as a pluggable block, not hardcoded into card shell

### Mobile app direction

- Tokens in `styles.css` map 1:1 to future design tokens export (JSON/Figma variables)
- Bottom navigation spec reserved; header drawer is interim
- Auth redirect service pattern applies to deep links

### Advanced filters

- Filter panel slides or expands; never occupy full screen on mobile unless modal
- Preserve `query` filter for text search across verticals

### Trust systems

- Badge component already supports pending/approved/rejected/neutral — extend, don't replace
- Future: verified owner, ratings, insurance badges slot into card footer and detail header

---

## APPENDIX A — FILE REFERENCE

| Concern | Location |
| ------- | -------- |
| Design tokens | `src/styles.css` |
| App shell + footer visibility | `src/app/app.ts`, `app.html`, `app.css` |
| Listing card | `src/app/features/listings/components/listing-card/` |
| Booking panel | `src/app/features/listings/components/booking-panel/` |
| Create listing form | `src/app/features/listings/components/create-listing-form/` |
| Shared UI | `src/app/shared/ui/` |
| i18n | `public/i18n/en.json`, `ru.json`, `hy.json` |
| Admin moderation | `src/app/features/admin/` |

---

## APPENDIX B — QUICK DECISION GUIDE

### Adding a new page?

Wrap content in `.page-container`, use `.section-stack`, include empty/loading states, show global footer unless it's a full-screen conversion page.

### Adding a new listing attribute?

Show on detail page trust section + admin moderation card; add to form section card; optional on browse card badge.

### Adding a new button?

Primary orange pill for main action; ghost for secondary; full width on mobile.

### AI generating components?

Read this file + `src/styles.css` first; reuse shared UI; match NgRx + standalone patterns.

---

*Last updated: May 2026 — ToyRent MVP (child toys rental, Armenia)*
