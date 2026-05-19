// flows.jsx — Key flows in Direction A (the recommended evolution baseline)

// =====================================================
// AUTH — Sign in + Sign up sheet
// =====================================================
function AuthScreen({ mode = 'signin', dir = 'A' }) {
  const t = TOKENS[dir];
  const isSignIn = mode === 'signin';
  return (
    <MScreen dir={dir}>
      <div style={{ padding: '20px 16px 0', position: 'relative' }}>
        <button style={{ width: 36, height: 36, borderRadius: 999, background: t.surface, border: `1px solid ${t.border}`, display: 'grid', placeItems: 'center' }}>
          <Icon name="chevronL" size={18} color={t.text} />
        </button>
      </div>
      <div style={{ padding: '24px 24px 0' }}>
        <div style={{ width: 56, height: 56, borderRadius: 18, background: t.primary, display: 'grid', placeItems: 'center', marginBottom: 16 }}>
          <Icon name="heart" size={28} color="#fff" />
        </div>
        <h1 style={{ margin: 0, fontSize: 26, fontWeight: 800, color: t.text, letterSpacing: '-0.02em' }}>
          {isSignIn ? 'Welcome back' : 'Join ToyRent'}
        </h1>
        <p style={{ margin: '6px 0 24px', fontSize: 13, color: t.textMute, lineHeight: 1.5 }}>
          {isSignIn ? 'Sign in to manage rentals and list toys.' : 'Free for parents. List up to 3 toys to start.'}
        </p>

        {/* Social */}
        <div style={{ display: 'grid', gap: 10 }}>
          <button style={{ height: 48, borderRadius: 12, background: t.surface, border: `1.5px solid ${t.border}`, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 10, fontSize: 14, fontWeight: 600, color: t.text }}>
            <svg width="18" height="18" viewBox="0 0 24 24"><path fill="#4285F4" d="M22.5 12.3c0-.8-.1-1.6-.2-2.3H12v4.4h5.9c-.3 1.4-1 2.5-2.2 3.3v2.7h3.5c2.1-1.9 3.3-4.7 3.3-8.1z"/><path fill="#34A853" d="M12 22.5c3 0 5.5-1 7.3-2.7l-3.5-2.7c-1 .7-2.3 1.1-3.8 1.1-2.9 0-5.4-2-6.3-4.6H2v2.8c1.8 3.6 5.5 6.1 9.8 6.1z"/><path fill="#FBBC05" d="M5.7 13.6c-.2-.7-.3-1.4-.3-2.1s.1-1.4.3-2.1V6.6H2.1A10.5 10.5 0 001 11.5c0 1.7.4 3.4 1.1 4.9l3.6-2.8z"/><path fill="#EA4335" d="M12 5.4c1.6 0 3.1.6 4.2 1.6L19.3 4C17.5 2.3 15 1.5 12 1.5 7.7 1.5 4 4 2.1 7.6l3.6 2.8c.9-2.6 3.3-5 6.3-5z"/></svg>
            Continue with Google
          </button>
          <button style={{ height: 48, borderRadius: 12, background: '#0F1115', border: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 10, fontSize: 14, fontWeight: 600, color: '#fff' }}>
            <svg width="18" height="18" viewBox="0 0 24 24" fill="#fff"><path d="M16.5 3c.1 1.6-.5 3.2-1.5 4.3-1 1.1-2.6 2-4.2 1.9-.2-1.5.6-3 1.5-4 1-1 2.7-1.9 4.2-2.2zm4 14c-1 2.2-1.6 3.2-2.8 5.1-1.8 2.7-4.3 6-7.4 6-2.8 0-3.5-1.8-7.3-1.8C0 26.3-.7 28-3 28v-.5C-1.5 26-.7 24.4-.7 22c0-3.6 1.8-5.5 3.5-6.5 1.5-.8 3.1-.9 4.4-.9 1.4 0 3 .9 4.1.9 1.1 0 2.8-1 4.8-.9 1.7.1 4.8.9 6.4 3-5.3 3-4.4 10.6 1.5 13z"/></svg>
            Continue with Apple
          </button>
        </div>

        <div style={{ display: 'flex', alignItems: 'center', gap: 10, margin: '18px 0' }}>
          <div style={{ flex: 1, height: 1, background: t.border }} />
          <span style={{ fontSize: 11, color: t.textMute, fontWeight: 500 }}>or email</span>
          <div style={{ flex: 1, height: 1, background: t.border }} />
        </div>

        {/* Email field */}
        <div style={{ marginBottom: 12 }}>
          <label style={{ fontSize: 12, fontWeight: 600, color: t.text, display: 'block', marginBottom: 6 }}>Email</label>
          <div style={{ height: 48, borderRadius: 12, background: t.surface, border: `1.5px solid ${t.primary}`, padding: '0 14px', display: 'flex', alignItems: 'center', fontSize: 14, color: t.text, boxShadow: `0 0 0 4px ${t.primary}1A` }}>
            anna@toyrent.am
          </div>
        </div>
        <div style={{ marginBottom: 4 }}>
          <label style={{ fontSize: 12, fontWeight: 600, color: t.text, display: 'block', marginBottom: 6 }}>Password</label>
          <div style={{ height: 48, borderRadius: 12, background: t.surface, border: `1.5px solid ${t.border}`, padding: '0 14px', display: 'flex', alignItems: 'center', gap: 8, fontSize: 14, color: t.text }}>
            <span style={{ flex: 1, letterSpacing: 2 }}>••••••••</span>
            <Icon name="image" size={16} color={t.textMute} />
          </div>
        </div>
        {isSignIn && (
          <div style={{ textAlign: 'right', marginBottom: 18 }}>
            <span style={{ fontSize: 12, fontWeight: 600, color: t.primary }}>Forgot password?</span>
          </div>
        )}
        <Btn dir={dir} variant="primary" size="lg" full>{isSignIn ? 'Sign in' : 'Create account'}</Btn>

        <div style={{ marginTop: 16, textAlign: 'center', fontSize: 13, color: t.textMute }}>
          {isSignIn ? "Don't have an account? " : 'Already have one? '}
          <span style={{ color: t.primary, fontWeight: 700 }}>{isSignIn ? 'Sign up' : 'Sign in'}</span>
        </div>

        {/* Trust strip */}
        <div style={{ marginTop: 24, padding: 12, background: t.surface, borderRadius: 12, border: `1px solid ${t.border}`, display: 'flex', alignItems: 'center', gap: 10 }}>
          <Icon name="shield" size={18} color={t.success} />
          <span style={{ fontSize: 11, color: t.textMute, lineHeight: 1.4 }}>
            <strong style={{ color: t.text }}>3,200+ Armenian families</strong> already share toys here.
          </span>
        </div>
      </div>
    </MScreen>
  );
}

// =====================================================
// CREATE LISTING — wizard step 2 of 4 (Basics)
// =====================================================
function CreateListingStep({ dir = 'A', step = 2 }) {
  const t = TOKENS[dir];
  const steps = ['Photos', 'Basics', 'Pricing', 'Hygiene'];
  return (
    <MScreen dir={dir}>
      <div style={{ padding: '12px 16px', background: t.surface, borderBottom: `1px solid ${t.border}` }}>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', marginBottom: 12 }}>
          <button style={{ width: 36, height: 36, borderRadius: 999, background: 'transparent', display: 'grid', placeItems: 'center', border: 0 }}>
            <Icon name="chevronL" size={20} color={t.text} />
          </button>
          <span style={{ fontSize: 12, fontWeight: 600, color: t.textMute }}>Step {step + 1} of 4</span>
          <button style={{ background: 'transparent', border: 0, fontSize: 13, fontWeight: 600, color: t.textMute }}>Save draft</button>
        </div>
        {/* Stepper */}
        <div style={{ display: 'flex', gap: 4 }}>
          {steps.map((s, i) => (
            <div key={i} style={{ flex: 1, height: 4, borderRadius: 2, background: i <= step ? t.primary : t.surfaceAlt }} />
          ))}
        </div>
      </div>

      <div style={{ height: 'calc(100% - 86px - 90px)', overflow: 'hidden', padding: '16px 16px 24px' }}>
        <h1 style={{ margin: 0, fontSize: 22, fontWeight: 800, color: t.text, letterSpacing: '-0.02em' }}>Tell us about your toy</h1>
        <p style={{ margin: '4px 0 18px', fontSize: 13, color: t.textMute }}>The clearer the basics, the faster you'll get a renter.</p>

        {/* Photos preview thumbnail strip */}
        <div style={{ marginBottom: 18 }}>
          <label style={{ fontSize: 12, fontWeight: 600, color: t.text, display: 'block', marginBottom: 8 }}>Photos · 3 added</label>
          <div style={{ display: 'flex', gap: 8 }}>
            <div style={{ width: 56, height: 56, borderRadius: 10, overflow: 'hidden', position: 'relative' }}>
              <img src={TOY_IMGS.lego} style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
              <div style={{ position: 'absolute', top: -2, left: -2, width: 16, height: 16, background: t.primary, color: '#fff', fontSize: 9, fontWeight: 700, borderRadius: '0 0 6px 0', display: 'grid', placeItems: 'center' }}>★</div>
            </div>
            <div style={{ width: 56, height: 56, borderRadius: 10, overflow: 'hidden' }}>
              <img src={TOY_IMGS.blocks} style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
            </div>
            <div style={{ width: 56, height: 56, borderRadius: 10, overflow: 'hidden' }}>
              <img src={TOY_IMGS.cars} style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
            </div>
            <button style={{ width: 56, height: 56, borderRadius: 10, background: t.surface, border: `1.5px dashed ${t.border}`, display: 'grid', placeItems: 'center' }}>
              <Icon name="plus" size={20} color={t.textMute} />
            </button>
          </div>
        </div>

        {/* Title */}
        <div style={{ marginBottom: 14 }}>
          <label style={{ fontSize: 12, fontWeight: 600, color: t.text, display: 'block', marginBottom: 6 }}>Toy name</label>
          <div style={{ height: 48, borderRadius: 12, background: t.surface, border: `1.5px solid ${t.primary}`, padding: '0 14px', display: 'flex', alignItems: 'center', boxShadow: `0 0 0 4px ${t.primary}1A`, fontSize: 14 }}>
            LEGO Duplo Town Set
          </div>
          <div style={{ fontSize: 11, color: t.success, marginTop: 4, display: 'flex', alignItems: 'center', gap: 4 }}>
            <Icon name="check" size={11} color={t.success} /> Looks great
          </div>
        </div>

        {/* Category */}
        <div style={{ marginBottom: 14 }}>
          <label style={{ fontSize: 12, fontWeight: 600, color: t.text, display: 'block', marginBottom: 6 }}>Category</label>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: 8 }}>
            {[
              { l: 'LEGO', i: 'grid', active: true },
              { l: 'Plush', i: 'heart' },
              { l: 'Wooden', i: 'home' },
            ].map((c, i) => (
              <button key={i} style={{
                height: 64, borderRadius: 12,
                background: c.active ? t.primary : t.surface,
                border: c.active ? 'none' : `1.5px solid ${t.border}`,
                color: c.active ? '#fff' : t.text,
                display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 4,
                fontWeight: 600, fontSize: 12,
              }}>
                <Icon name={c.i} size={20} color={c.active ? '#fff' : t.text} />
                {c.l}
              </button>
            ))}
          </div>
        </div>

        {/* Age range slider */}
        <div style={{ marginBottom: 14 }}>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline', marginBottom: 8 }}>
            <label style={{ fontSize: 12, fontWeight: 600, color: t.text }}>Age range</label>
            <span style={{ fontSize: 12, fontWeight: 700, color: t.primary }}>2 – 5 yrs</span>
          </div>
          <div style={{ height: 6, background: t.surfaceAlt, borderRadius: 999, position: 'relative' }}>
            <div style={{ position: 'absolute', left: '14%', right: '60%', height: '100%', background: t.primary, borderRadius: 999 }} />
            <div style={{ position: 'absolute', left: '14%', top: -7, width: 20, height: 20, borderRadius: 999, background: '#fff', border: `2px solid ${t.primary}`, boxShadow: '0 2px 6px rgba(0,0,0,.15)', transform: 'translateX(-50%)' }} />
            <div style={{ position: 'absolute', left: '40%', top: -7, width: 20, height: 20, borderRadius: 999, background: '#fff', border: `2px solid ${t.primary}`, boxShadow: '0 2px 6px rgba(0,0,0,.15)', transform: 'translateX(-50%)' }} />
          </div>
          <div style={{ display: 'flex', justifyContent: 'space-between', marginTop: 6, fontSize: 10, color: t.textMute }}>
            <span>0</span><span>3</span><span>6</span><span>9</span><span>12+</span>
          </div>
        </div>

        {/* Condition */}
        <div style={{ marginBottom: 14 }}>
          <label style={{ fontSize: 12, fontWeight: 600, color: t.text, display: 'block', marginBottom: 6 }}>Condition</label>
          <div style={{ display: 'flex', gap: 6 }}>
            {['Like new', 'Gently used', 'Used', 'Worn'].map((c, i) => (
              <span key={i} style={{
                flex: 1, height: 36, borderRadius: 999,
                background: i === 1 ? t.text : t.surface,
                color: i === 1 ? '#fff' : t.text,
                border: i === 1 ? 'none' : `1px solid ${t.border}`,
                display: 'grid', placeItems: 'center',
                fontSize: 11, fontWeight: 600,
              }}>{c}</span>
            ))}
          </div>
        </div>
      </div>

      {/* Sticky bottom */}
      <div style={{ position: 'absolute', bottom: 0, left: 0, right: 0, padding: '12px 16px 28px', background: t.surface, borderTop: `1px solid ${t.border}`, display: 'flex', gap: 10 }}>
        <Btn dir={dir} variant="secondary" size="lg">Back</Btn>
        <Btn dir={dir} variant="primary" size="lg" style={{ flex: 1 }}>Continue to pricing</Btn>
      </div>
    </MScreen>
  );
}

// =====================================================
// MY TOYS — owner dashboard
// =====================================================
function MyToysScreen({ dir = 'A' }) {
  const t = TOKENS[dir];
  return (
    <MScreen dir={dir}>
      <div style={{ padding: '14px 16px 10px', background: t.surface, borderBottom: `1px solid ${t.border}` }}>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
          <h1 style={{ margin: 0, fontSize: 22, fontWeight: 800, color: t.text, letterSpacing: '-0.01em' }}>My toys</h1>
          <Btn dir={dir} variant="primary" size="sm" icon="plus">List new</Btn>
        </div>
        {/* Earnings strip */}
        <div style={{ marginTop: 12, padding: 14, borderRadius: 14, background: `linear-gradient(135deg, ${t.accent}, #1f2235)`, color: '#fff' }}>
          <div style={{ fontSize: 11, opacity: .7, letterSpacing: '.04em', textTransform: 'uppercase' }}>This month</div>
          <div style={{ display: 'flex', alignItems: 'baseline', gap: 6, marginTop: 4 }}>
            <span style={{ fontSize: 26, fontWeight: 800, letterSpacing: '-0.02em' }}>₽18,400</span>
            <span style={{ fontSize: 12, opacity: .7 }}>earned · 9 rentals</span>
          </div>
          <div style={{ height: 36, marginTop: 10, display: 'flex', alignItems: 'flex-end', gap: 4 }}>
            {[10, 18, 14, 22, 28, 16, 30, 26, 20, 34, 24, 32].map((h, i) => (
              <div key={i} style={{ flex: 1, height: h, background: t.primary, borderRadius: 2, opacity: i > 8 ? 1 : .6 }} />
            ))}
          </div>
        </div>
        {/* Tabs */}
        <div style={{ display: 'flex', gap: 18, marginTop: 14, marginBottom: -10 }}>
          {[
            { l: 'Active', c: 5, active: true },
            { l: 'Pending', c: 2 },
            { l: 'Rented out', c: 1 },
            { l: 'Drafts', c: 1 },
          ].map((tab, i) => (
            <div key={i} style={{ paddingBottom: 10, borderBottom: tab.active ? `2px solid ${t.primary}` : '2px solid transparent', display: 'flex', alignItems: 'center', gap: 6 }}>
              <span style={{ fontSize: 13, fontWeight: 700, color: tab.active ? t.primary : t.textMute }}>{tab.l}</span>
              <span style={{ fontSize: 10, fontWeight: 700, padding: '1px 6px', borderRadius: 999, background: tab.active ? t.primarySoft : t.surfaceAlt, color: tab.active ? t.primary : t.textMute }}>{tab.c}</span>
            </div>
          ))}
        </div>
      </div>

      <div style={{ height: 'calc(100% - 56px - 248px - 80px)', overflow: 'hidden', padding: '14px 16px' }}>
        {/* Toy list rows */}
        <div style={{ display: 'grid', gap: 10 }}>
          {[
            { img: TOY_IMGS.lego, title: 'LEGO Duplo Town', price: '₽1,500', status: 'active', meta: '8 views today · 1 saved' },
            { img: TOY_IMGS.kitchen, title: 'Wooden play kitchen', price: '₽1,200', status: 'active', meta: '12 views · book request' },
            { img: TOY_IMGS.plush, title: 'Plush family pack', price: '₽400', status: 'pending', meta: 'In review · ~4h' },
          ].map((toy, i) => (
            <div key={i} style={{ background: t.surface, borderRadius: t.radiusCard, border: `1px solid ${t.border}`, padding: 10, display: 'flex', gap: 12, alignItems: 'center' }}>
              <img src={toy.img} style={{ width: 56, height: 56, borderRadius: 10, objectFit: 'cover' }} />
              <div style={{ flex: 1, minWidth: 0 }}>
                <div style={{ fontSize: 13, fontWeight: 700, color: t.text, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{toy.title}</div>
                <div style={{ fontSize: 11, color: t.textMute, marginTop: 2 }}>{toy.meta}</div>
                <div style={{ display: 'flex', alignItems: 'center', gap: 6, marginTop: 6 }}>
                  <Badge tone={toy.status === 'active' ? 'success' : 'warn'} dir={dir}>{toy.status === 'active' ? '● Live' : '● Pending'}</Badge>
                  <span style={{ fontSize: 12, fontWeight: 700, color: t.primary }}>{toy.price}/d</span>
                </div>
              </div>
              <button style={{ width: 32, height: 32, borderRadius: 999, background: 'transparent', border: 0, display: 'grid', placeItems: 'center' }}>
                <Icon name="chevron" size={16} color={t.textMute} />
              </button>
            </div>
          ))}
        </div>
      </div>
      <BottomTabs dir={dir} active={4} />
    </MScreen>
  );
}

// =====================================================
// ADMIN MODERATION — review queue (mobile)
// =====================================================
function AdminScreen({ dir = 'A' }) {
  const t = TOKENS[dir];
  return (
    <MScreen dir={dir}>
      <div style={{ padding: '14px 16px 12px', background: t.surface, borderBottom: `1px solid ${t.border}` }}>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between' }}>
          <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <button style={{ width: 36, height: 36, borderRadius: 999, background: 'transparent', display: 'grid', placeItems: 'center', border: 0 }}>
              <Icon name="chevronL" size={20} color={t.text} />
            </button>
            <div>
              <div style={{ fontSize: 16, fontWeight: 800, color: t.text }}>Review queue</div>
              <div style={{ fontSize: 11, color: t.textMute }}>14 awaiting · oldest 3h ago</div>
            </div>
          </div>
          <Badge tone="warn" dir={dir}>Admin</Badge>
        </div>
        {/* Filter chips */}
        <div style={{ display: 'flex', gap: 6, overflow: 'hidden', marginTop: 12 }}>
          <Chip dir={dir} active>All · 14</Chip>
          <Chip dir={dir}>New owners · 4</Chip>
          <Chip dir={dir}>Photos · 6</Chip>
          <Chip dir={dir}>Flagged · 2</Chip>
        </div>
      </div>

      {/* The review card */}
      <div style={{ height: 'calc(100% - 56px - 84px - 86px)', overflow: 'hidden', padding: '14px 16px' }}>
        <div style={{ background: t.surface, borderRadius: t.radiusCard, border: `1px solid ${t.border}`, overflow: 'hidden', boxShadow: t.shadow }}>
          {/* Photo grid */}
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: 2, padding: 2 }}>
            <img src={TOY_IMGS.lego} style={{ aspectRatio: '1/1', width: '100%', objectFit: 'cover', borderRadius: 6 }} />
            <img src={TOY_IMGS.blocks} style={{ aspectRatio: '1/1', width: '100%', objectFit: 'cover', borderRadius: 6 }} />
            <img src={TOY_IMGS.cars} style={{ aspectRatio: '1/1', width: '100%', objectFit: 'cover', borderRadius: 6 }} />
          </div>
          <div style={{ padding: 14 }}>
            <h3 style={{ margin: 0, fontSize: 16, fontWeight: 700, color: t.text }}>LEGO Duplo Town Set</h3>
            <div style={{ fontSize: 12, color: t.textMute, marginTop: 4, lineHeight: 1.5 }}>
              Gently-used set, all pieces accounted for. Recently sanitized…
            </div>
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 6, marginTop: 12 }}>
              <div style={{ fontSize: 11, color: t.textMute }}>Category</div>
              <div style={{ fontSize: 11, fontWeight: 600, color: t.text }}>LEGO &amp; Bricks</div>
              <div style={{ fontSize: 11, color: t.textMute }}>Age</div>
              <div style={{ fontSize: 11, fontWeight: 600, color: t.text }}>2–5 yrs</div>
              <div style={{ fontSize: 11, color: t.textMute }}>Price</div>
              <div style={{ fontSize: 11, fontWeight: 600, color: t.text }}>₽1,500 / day</div>
            </div>
            <div style={{ marginTop: 12, padding: 10, background: t.bg, borderRadius: 10, display: 'flex', alignItems: 'center', gap: 10 }}>
              <img src={FAMILY_AVS.anna} style={{ width: 30, height: 30, borderRadius: 999 }} />
              <div style={{ flex: 1 }}>
                <div style={{ fontSize: 12, fontWeight: 700, color: t.text }}>Anna Sargsyan</div>
                <div style={{ fontSize: 10, color: t.textMute }}>First listing · joined 2 days ago</div>
              </div>
              <Badge tone="warn" dir={dir}>New owner</Badge>
            </div>
            {/* AI check */}
            <div style={{ marginTop: 10, padding: 10, background: '#E6F4EE', borderRadius: 10, display: 'flex', alignItems: 'flex-start', gap: 8 }}>
              <Icon name="sparkle" size={14} color={t.success} />
              <div style={{ fontSize: 11, color: t.text, lineHeight: 1.45 }}>
                <strong>Auto-check passed:</strong> photos clear, no banned items, age within range.
              </div>
            </div>
          </div>
        </div>
      </div>

      {/* Decision bar */}
      <div style={{ position: 'absolute', bottom: 0, left: 0, right: 0, background: t.surface, borderTop: `1px solid ${t.border}`, padding: '12px 16px 28px', display: 'grid', gridTemplateColumns: '1fr 1fr 1fr', gap: 8 }}>
        <button style={{ height: 48, borderRadius: 12, background: t.surfaceAlt, border: 0, fontSize: 13, fontWeight: 700, color: t.text }}>Skip</button>
        <button style={{ height: 48, borderRadius: 12, background: '#FFE8E5', border: 0, color: t.danger, fontSize: 13, fontWeight: 700, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 6 }}>
          <Icon name="x" size={16} color={t.danger} /> Reject
        </button>
        <button style={{ height: 48, borderRadius: 12, background: t.success, border: 0, color: '#fff', fontSize: 13, fontWeight: 700, display: 'flex', alignItems: 'center', justifyContent: 'center', gap: 6 }}>
          <Icon name="check" size={16} color="#fff" /> Approve
        </button>
      </div>
    </MScreen>
  );
}

// =====================================================
// EMPTY STATES — 3 small variants in a "screen"
// =====================================================
function EmptyStatesScreen({ dir = 'A' }) {
  const t = TOKENS[dir];
  return (
    <MScreen dir={dir}>
      <MHeader dir={dir} title="Empty states" back />
      <div style={{ padding: '16px', display: 'grid', gap: 14, overflow: 'hidden', height: 'calc(100% - 52px)' }}>
        {/* No rentals yet */}
        <div style={{ padding: 22, background: t.surface, borderRadius: t.radiusCard, border: `1px solid ${t.border}`, textAlign: 'center' }}>
          <div style={{ width: 64, height: 64, borderRadius: 18, background: t.primarySoft, display: 'grid', placeItems: 'center', margin: '0 auto 12px' }}>
            <Icon name="calendar" size={28} color={t.primary} />
          </div>
          <div style={{ fontSize: 15, fontWeight: 700, color: t.text }}>No rentals yet</div>
          <div style={{ fontSize: 12, color: t.textMute, marginTop: 4, lineHeight: 1.5, maxWidth: 240, margin: '4px auto 14px' }}>
            When you rent a toy, it'll show up here. Start by browsing nearby.
          </div>
          <Btn dir={dir} variant="primary" size="md">Browse toys</Btn>
        </div>

        {/* No listings yet */}
        <div style={{ padding: 18, background: t.surface, borderRadius: t.radiusCard, border: `1px dashed ${t.primary}`, display: 'flex', alignItems: 'center', gap: 12 }}>
          <div style={{ width: 48, height: 48, borderRadius: 14, background: t.primarySoft, display: 'grid', placeItems: 'center' }}>
            <Icon name="plus" size={22} color={t.primary} />
          </div>
          <div style={{ flex: 1 }}>
            <div style={{ fontSize: 13, fontWeight: 700, color: t.text }}>List your first toy</div>
            <div style={{ fontSize: 11, color: t.textMute, marginTop: 2, lineHeight: 1.4 }}>Takes ~2 minutes. Earn from toys gathering dust.</div>
          </div>
          <Btn dir={dir} variant="primary" size="sm">Start</Btn>
        </div>

        {/* Search returned nothing */}
        <div style={{ padding: 22, background: t.surface, borderRadius: t.radiusCard, border: `1px solid ${t.border}`, textAlign: 'center' }}>
          <div style={{ width: 64, height: 64, borderRadius: 18, background: t.surfaceAlt, display: 'grid', placeItems: 'center', margin: '0 auto 12px' }}>
            <Icon name="search" size={26} color={t.textMute} />
          </div>
          <div style={{ fontSize: 15, fontWeight: 700, color: t.text }}>Nothing matched "trampoline"</div>
          <div style={{ fontSize: 12, color: t.textMute, marginTop: 4, lineHeight: 1.5, maxWidth: 260, margin: '4px auto 14px' }}>
            Try broader filters or get notified when one is listed nearby.
          </div>
          <div style={{ display: 'flex', gap: 8, justifyContent: 'center' }}>
            <Btn dir={dir} variant="secondary" size="md">Clear filters</Btn>
            <Btn dir={dir} variant="dark" size="md" icon="bell">Notify me</Btn>
          </div>
        </div>
      </div>
    </MScreen>
  );
}

// =====================================================
// REQUEST CONFIRMATION — success screen after booking sent
// =====================================================
function ConfirmScreen({ dir = 'A' }) {
  const t = TOKENS[dir];
  return (
    <MScreen dir={dir}>
      <div style={{ position: 'absolute', inset: 0, padding: '60px 24px 24px', display: 'flex', flexDirection: 'column', textAlign: 'center' }}>
        <div style={{ flex: 1, display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', gap: 8 }}>
          <div style={{ width: 96, height: 96, borderRadius: 999, background: t.primarySoft, display: 'grid', placeItems: 'center', marginBottom: 12, position: 'relative' }}>
            <div style={{ position: 'absolute', inset: -10, borderRadius: 999, border: `2px dashed ${t.primary}`, opacity: .4 }} />
            <Icon name="check" size={48} color={t.primary} strokeWidth={3} />
          </div>
          <h1 style={{ margin: 0, fontSize: 24, fontWeight: 800, color: t.text, letterSpacing: '-0.02em' }}>Request sent!</h1>
          <p style={{ margin: '4px 0 0', fontSize: 14, color: t.textMute, lineHeight: 1.5, maxWidth: 280 }}>
            Anna usually replies within 2 hours. We'll notify you here and by email.
          </p>

          {/* Mini summary */}
          <div style={{ marginTop: 24, padding: 14, background: t.surface, borderRadius: t.radiusCard, border: `1px solid ${t.border}`, width: '100%', textAlign: 'left' }}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
              <img src={TOY_IMGS.lego} style={{ width: 48, height: 48, borderRadius: 10, objectFit: 'cover' }} />
              <div style={{ flex: 1 }}>
                <div style={{ fontSize: 13, fontWeight: 700, color: t.text }}>LEGO Duplo Town Set</div>
                <div style={{ fontSize: 11, color: t.textMute }}>18 – 22 May · 4 days</div>
              </div>
              <span style={{ fontSize: 14, fontWeight: 700, color: t.primary }}>₽7,300</span>
            </div>
          </div>
        </div>

        <div style={{ display: 'grid', gap: 10 }}>
          <Btn dir={dir} variant="primary" size="lg" full icon="message">Message Anna</Btn>
          <Btn dir={dir} variant="ghost" size="md" full>Back to home</Btn>
        </div>
      </div>
    </MScreen>
  );
}

Object.assign(window, {
  AuthScreen, CreateListingStep, MyToysScreen, AdminScreen,
  EmptyStatesScreen, ConfirmScreen,
});
