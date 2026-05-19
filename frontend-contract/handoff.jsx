// handoff.jsx — Implementation handoff for Cursor + Claude Code

function CodeBlock({ children, title, lang = 'css' }) {
  return (
    <div style={{ background: '#0F1115', borderRadius: 12, overflow: 'hidden' }}>
      <div style={{ padding: '8px 14px', borderBottom: '1px solid #1F2128', display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
        <span style={{ fontSize: 11, fontWeight: 600, color: '#A8AAB0', fontFamily: "'JetBrains Mono',monospace" }}>{title}</span>
        <span style={{ fontSize: 10, fontWeight: 600, color: '#6B6A75', textTransform: 'uppercase', letterSpacing: '.08em' }}>{lang}</span>
      </div>
      <pre style={{ margin: 0, padding: 16, fontFamily: "'JetBrains Mono',monospace", fontSize: 11.5, lineHeight: 1.6, color: '#D1D5DB', whiteSpace: 'pre-wrap', overflowX: 'auto' }}>{children}</pre>
    </div>
  );
}

function ComponentSpec({ name, desc, props, file, html }) {
  return (
    <div style={{ background: '#fff', borderRadius: 14, padding: 18, border: '1px solid #ECE9E2' }}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 8, marginBottom: 4 }}>
        <h4 style={{ margin: 0, fontSize: 15, fontWeight: 700, fontFamily: "'JetBrains Mono',monospace", color: '#1A1B26' }}>&lt;{name}&gt;</h4>
        <span style={{ fontSize: 11, color: '#6B6A75', fontFamily: "'JetBrains Mono',monospace" }}>{file}</span>
      </div>
      <p style={{ margin: '0 0 12px', fontSize: 12, color: '#6B6A75', lineHeight: 1.5 }}>{desc}</p>
      <div style={{ display: 'grid', gap: 4 }}>
        {props.map((p, i) => (
          <div key={i} style={{ display: 'grid', gridTemplateColumns: '110px 100px 1fr', gap: 8, fontSize: 11, padding: '4px 0', borderBottom: i < props.length - 1 ? '1px solid #F4F1EB' : 'none', alignItems: 'baseline' }}>
            <code style={{ color: '#FF6008', fontFamily: "'JetBrains Mono',monospace", fontWeight: 600 }}>{p.name}</code>
            <code style={{ color: '#6B6A75', fontFamily: "'JetBrains Mono',monospace" }}>{p.type}</code>
            <span style={{ color: '#3A3B47' }}>{p.desc}</span>
          </div>
        ))}
      </div>
    </div>
  );
}

function HandoffBoard() {
  const cssVars = `:root {
  /* color · semantic, not literal */
  --color-bg:           #FAF8F4;
  --color-surface:      #FFFFFF;
  --color-surface-alt:  #F4F4F8;
  --color-border:       #E8E5DE;
  --color-text:         #1A1B26;
  --color-text-mute:    #6B6A75;
  --color-primary:      #FF6008;
  --color-primary-hover:#E5530A;
  --color-primary-soft: #FFEEDC;
  --color-accent:       #2A2C41;
  --color-success:      #0E8A5F;
  --color-warn:         #D97706;
  --color-danger:       #D9342B;

  /* radius */
  --radius-sm: 8px;
  --radius:    14px;
  --radius-lg: 18px;
  --radius-card:16px;
  --radius-pill:999px;

  /* spacing — 4px baseline */
  --space-1: 4px;
  --space-2: 8px;
  --space-3: 12px;
  --space-4: 16px;
  --space-6: 24px;
  --space-8: 32px;

  /* type */
  --font-ui: 'Inter', system-ui, sans-serif;
  --font-size-body: 14px;
  --font-size-body-lg: 16px;
  --font-size-h1: 26px;
  --font-size-h2: 22px;
  --font-size-h3: 16px;

  /* shadows */
  --shadow-sm: 0 1px 2px rgba(20,15,5,.04);
  --shadow:    0 1px 2px rgba(20,15,5,.04), 0 8px 24px rgba(20,15,5,.06);
  --shadow-lg: 0 12px 40px rgba(20,15,5,.10);

  /* layout */
  --container-page: 1200px;
  --container-text:  680px;
  --tab-bar-h:       72px;
  --header-h:        56px;
}`;

  const tsTokens = `// design/tokens.ts — single source of truth.
// Mirrors :root CSS vars. Use for TS-typed access.
export const tokens = {
  color: {
    bg: 'var(--color-bg)',
    surface: 'var(--color-surface)',
    surfaceAlt: 'var(--color-surface-alt)',
    border: 'var(--color-border)',
    text: 'var(--color-text)',
    textMute: 'var(--color-text-mute)',
    primary: 'var(--color-primary)',
    primarySoft: 'var(--color-primary-soft)',
    success: 'var(--color-success)',
    warn: 'var(--color-warn)',
    danger: 'var(--color-danger)',
  },
  radius: { sm: 8, md: 14, lg: 18, card: 16, pill: 999 },
  space:  { 1:4, 2:8, 3:12, 4:16, 6:24, 8:32 },
  bp:     { mobile: 0, tablet: 768, desktop: 1024, wide: 1280 },
} as const;`;

  return (
    <div style={{ width: 1480, background: '#fff', borderRadius: 12, padding: 32, border: '1px solid #ECE9E2' }}>
      <div style={{ marginBottom: 24 }}>
        <div style={{ fontSize: 11, fontWeight: 700, letterSpacing: '.12em', color: '#FF6008', textTransform: 'uppercase' }}>08 · Implementation Handoff</div>
        <h2 style={{ margin: '4px 0 6px', fontSize: 32, fontWeight: 800, color: '#1A1B26', letterSpacing: '-0.02em' }}>
          Built for Angular + NgRx, friendly to Cursor &amp; Claude Code
        </h2>
        <p style={{ margin: 0, fontSize: 14, color: '#6B6A75', maxWidth: 800, lineHeight: 1.55 }}>
          Every visual choice maps to a token. Every screen maps to a fixed set of components.
          Drop these into <code style={{ background: '#F4F1EB', padding: '2px 6px', borderRadius: 4, fontSize: 13 }}>src/app/shared/ui/</code>
          and the rest of the app is decorating.
        </p>
      </div>

      {/* Two-col: tokens code | component map */}
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 16, marginBottom: 24 }}>
        <div>
          <h3 style={{ margin: '0 0 12px', fontSize: 16, fontWeight: 700 }}>Tokens — drop in <code style={{ fontSize: 14 }}>styles.css</code></h3>
          <CodeBlock title="src/styles/tokens.css" lang="css">{cssVars}</CodeBlock>
        </div>
        <div>
          <h3 style={{ margin: '0 0 12px', fontSize: 16, fontWeight: 700 }}>TS mirror — for component props &amp; codegen</h3>
          <CodeBlock title="src/app/design/tokens.ts" lang="ts">{tsTokens}</CodeBlock>
          <div style={{ marginTop: 12, padding: 14, background: '#FAF8F4', borderRadius: 10, border: '1px dashed #ECE9E2', fontSize: 12, color: '#3A3B47', lineHeight: 1.6 }}>
            <strong>Cursor / Claude tip:</strong> add{' '}
            <code style={{ background: '#fff', padding: '2px 6px', borderRadius: 4 }}>.cursor/rules</code>{' '}
            instructing the agent to only use semantic vars (<code>var(--color-primary)</code>)
            and never literal hex codes in components. Tokens become enforceable contract.
          </div>
        </div>
      </div>

      {/* Component map */}
      <h3 style={{ margin: '0 0 14px', fontSize: 18, fontWeight: 700 }}>Component map — Angular standalone</h3>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: 14, marginBottom: 24 }}>
        <ComponentSpec
          name="tr-button"
          desc="Primary CTA + 4 variants. Pill radius, three sizes. 44px min touch target."
          file="shared/ui/button"
          props={[
            { name: 'variant', type: 'string', desc: "'primary' | 'secondary' | 'ghost' | 'dark' | 'soft'" },
            { name: 'size', type: 'string', desc: "'sm' | 'md' | 'lg' (32 / 44 / 52 px)" },
            { name: 'icon', type: 'string?', desc: 'Icon name from icon set' },
            { name: 'full', type: 'bool', desc: 'Stretch to container width (mobile sticky CTAs)' },
            { name: 'loading', type: 'bool', desc: 'Replaces label with spinner; keeps width' },
          ]}
        />
        <ComponentSpec
          name="tr-toy-card"
          desc="The marketplace atom. Trust signals are part of the schema, not optional sprinkles."
          file="shared/ui/toy-card"
          props={[
            { name: 'toy', type: 'Toy', desc: 'NgRx Toy entity. Maps name, image, price, ageRange.' },
            { name: 'owner', type: 'Owner', desc: 'avatar, name, distanceKm, verified flag' },
            { name: 'size', type: 'string', desc: "'sm' (browse) | 'md' (home) | 'lg' (feature)" },
            { name: 'showTrust', type: 'bool', desc: 'Verified / hygiene badges. Defaults true.' },
          ]}
        />
        <ComponentSpec
          name="tr-bottom-tabs"
          desc="5-tab mobile primary nav with prominent center +. Only renders &lt; 768px."
          file="shared/ui/bottom-tabs"
          props={[
            { name: 'active', type: 'TabName', desc: "'home' | 'browse' | 'list' | 'rentals' | 'profile'" },
          ]}
        />
        <ComponentSpec
          name="tr-badge"
          desc="Status, age-range, trust markers. Tone-driven."
          file="shared/ui/badge"
          props={[
            { name: 'tone', type: 'string', desc: "'success' | 'warn' | 'info' | 'primary' | 'default'" },
            { name: 'icon', type: 'string?', desc: 'Optional leading icon' },
          ]}
        />
        <ComponentSpec
          name="tr-sticky-cta"
          desc="Bottom-anchored bar with price + CTA. Used on listing detail mobile."
          file="shared/ui/sticky-cta"
          props={[
            { name: 'price', type: 'string', desc: 'Display price (already formatted)' },
            { name: 'ctaText', type: 'string', desc: 'e.g. "Request to rent"' },
            { name: 'disabled', type: 'bool', desc: 'No dates yet → soft disabled with hint' },
          ]}
        />
        <ComponentSpec
          name="tr-date-sheet"
          desc="Bottom-sheet date picker with price breakdown. Drives NgRx booking slice."
          file="features/booking/date-sheet"
          props={[
            { name: 'toyId', type: 'string', desc: 'NgRx selector for availability' },
            { name: '(submit)', type: 'event', desc: 'Emits {start, end, total} → bookingActions.request()' },
          ]}
        />
      </div>

      {/* File tree */}
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 16, marginBottom: 24 }}>
        <div>
          <h3 style={{ margin: '0 0 12px', fontSize: 16, fontWeight: 700 }}>Suggested folder structure</h3>
          <CodeBlock title="src/app/" lang="tree">{`shared/
├─ ui/                        ← presentational, standalone
│  ├─ button/
│  ├─ badge/
│  ├─ chip/
│  ├─ toy-card/
│  ├─ bottom-tabs/
│  ├─ sticky-cta/
│  ├─ empty-state/
│  └─ icon/                   ← single <tr-icon name=""/>
├─ feature/
│  ├─ home/                   ← composed of shared/ui
│  ├─ browse/
│  ├─ listing-detail/
│  ├─ create-listing/         ← 4-step wizard (NgRx slice)
│  ├─ booking/
│  ├─ my-toys/                ← owner dashboard
│  └─ admin/                  ← moderation queue
├─ design/
│  ├─ tokens.ts               ← typed token mirror
│  └─ tokens.css              ← :root CSS vars
└─ core/                      ← interceptors, auth, i18n`}</CodeBlock>
        </div>

        <div>
          <h3 style={{ margin: '0 0 12px', fontSize: 16, fontWeight: 700 }}>Cursor / Claude rules — drop in .cursor/rules</h3>
          <CodeBlock title=".cursor/rules/toyrent-design.md" lang="md">{`# ToyRent design rules

## Tokens
- Never use literal hex codes. Only var(--color-*).
- Never use literal px for radius/spacing — use var(--radius-*) / var(--space-*).
- Inter only. No new font families.

## Mobile-first
- All components must render correctly at 360px width.
- All tappable targets ≥ 44px tall.
- Sticky CTAs at bottom on listing/create/booking screens.
- Bottom-tab nav < 768px; top-nav ≥ 768px.

## Trust schema
- ToyCard must always render owner block + verified/hygiene badges
  when the data is present. Never hide because of layout.

## i18n
- No hard-coded UI strings. Wrap with i18n key.
  Pattern: 'home.hero.title' → src/i18n/{en,ru,am}.json.

## Forms
- Validation message goes UNDER the field, red, with X icon.
- Helper text under the field, muted, no icon.
- Field has 4px focus ring (var(--color-primary) at 10% alpha).

## Imagery
- ToyCard image: aspect 1/1, object-fit cover.
- If no image: fallback SVG by category (lego, plush, wooden, ride-on…).
- Lazy-load + skeleton with shimmer.`}</CodeBlock>
        </div>
      </div>

      {/* Screen → Component matrix */}
      <h3 style={{ margin: '0 0 14px', fontSize: 18, fontWeight: 700 }}>Screen × component matrix</h3>
      <div style={{ background: '#fff', borderRadius: 12, border: '1px solid #ECE9E2', overflow: 'hidden' }}>
        <div style={{ display: 'grid', gridTemplateColumns: '180px repeat(7, 1fr)', borderBottom: '1px solid #ECE9E2', background: '#FAF8F4' }}>
          {['Screen', 'TopBar', 'BottomTabs', 'ToyCard', 'StickyCta', 'DateSheet', 'Wizard', 'EmptyState'].map((h, i) => (
            <div key={i} style={{ padding: '10px 12px', fontSize: 11, fontWeight: 700, color: '#1A1B26', letterSpacing: '.04em', textTransform: 'uppercase', borderRight: i < 7 ? '1px solid #ECE9E2' : 'none' }}>{h}</div>
          ))}
        </div>
        {[
          ['Home',         '●', '●', '●', '',  '',  '',  ''],
          ['Browse',       '●', '●', '●', '',  '',  '',  '●'],
          ['Listing detail','', '',  '',  '●', '●', '',  ''],
          ['Create listing','', '',  '',  '',  '',  '●', ''],
          ['Booking',      '',  '',  '',  '',  '●', '',  ''],
          ['My toys',      '●', '●', '●', '',  '',  '',  '●'],
          ['Admin queue',  '●', '',  '',  '',  '',  '',  ''],
          ['Auth',         '',  '',  '',  '',  '',  '',  ''],
        ].map((row, i) => (
          <div key={i} style={{ display: 'grid', gridTemplateColumns: '180px repeat(7, 1fr)', borderBottom: i < 7 ? '1px solid #F4F1EB' : 'none' }}>
            {row.map((cell, j) => (
              <div key={j} style={{
                padding: '10px 12px', fontSize: 13, color: j === 0 ? '#1A1B26' : '#FF6008',
                fontWeight: j === 0 ? 600 : 700, borderRight: j < 7 ? '1px solid #F4F1EB' : 'none',
              }}>{cell}</div>
            ))}
          </div>
        ))}
      </div>

      <div style={{ marginTop: 28, padding: 18, background: '#FAF8F4', borderRadius: 14, border: '1px dashed #ECE9E2' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginBottom: 8 }}>
          <Icon name="sparkle" size={16} color="#FF6008" />
          <h4 style={{ margin: 0, fontSize: 14, fontWeight: 700 }}>How to use this with Cursor / Claude Code</h4>
        </div>
        <ol style={{ margin: 0, paddingLeft: 20, fontSize: 12.5, color: '#3A3B47', lineHeight: 1.7 }}>
          <li>Copy <code style={{ background: '#fff', padding: '1px 6px', borderRadius: 4 }}>tokens.css</code> + <code style={{ background: '#fff', padding: '1px 6px', borderRadius: 4 }}>tokens.ts</code> into the repo. Wire <code>tokens.css</code> into <code>angular.json</code> styles array.</li>
          <li>Generate stubs: <code style={{ background: '#fff', padding: '1px 6px', borderRadius: 4 }}>ng g c shared/ui/toy-card --standalone</code> for each component above.</li>
          <li>Paste this file into a Claude / Cursor agent and prompt: <em>"Implement &lt;tr-toy-card&gt; per the spec in the screen × component matrix, using only token vars."</em></li>
          <li>Iterate screen-by-screen. The matrix tells the agent which components compose each screen.</li>
        </ol>
      </div>
    </div>
  );
}

Object.assign(window, { HandoffBoard });
