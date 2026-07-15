interface LauncherPreviewProps {
  /** Window title shown in the chrome. */
  title?: string
  /** Accent color driving nav underline, progress bar and Play button. */
  accent?: string
  /** Resolved background image URL, or null for the default dark backdrop. */
  backgroundUrl?: string | null
  /** Resolved top-right logo URL, or null for a placeholder tile. */
  logoUrl?: string | null
  /** Resolved app-icon URL, or null for an accent-tinted initial tile. */
  iconUrl?: string | null
}

/**
 * A scaled, static mock of the desktop launcher window so the admin can see how the selected
 * style template + branding will look. Mirrors the launcher's real layout (nav top-left, logo
 * top-right, a horizontal news strip, and a footer with progress + Update/Play). Callers resolve
 * the effective visuals (template vs uploaded asset) and pass the final URLs in.
 */
export default function LauncherPreview({
  title: rawTitle,
  accent: rawAccent,
  backgroundUrl = null,
  logoUrl = null,
  iconUrl = null,
}: LauncherPreviewProps) {
  const accent = rawAccent || '#4FA8D8'
  const title = rawTitle || 'Azeroth Platform Launcher'
  const initial = (title.trim()[0] || 'A').toUpperCase()

  return (
    <div className="overflow-hidden rounded-xl border border-gray-800 bg-gray-950 shadow-lg">
      {/* Window chrome: the app icon (.ico) + window title, then muted window controls. The icon
          tile falls back to an accent-tinted square so it visibly reflects the selected style. */}
      <div className="flex items-center gap-2 bg-gray-900 px-3 py-2">
        {iconUrl ? (
          <img src={iconUrl} alt="" className="h-4 w-4 rounded-sm object-contain" />
        ) : (
          <span
            className="flex h-4 w-4 items-center justify-center rounded-sm text-[9px] font-bold text-black/80"
            style={{ backgroundColor: accent }}
          >
            {initial}
          </span>
        )}
        <span className="truncate text-xs text-gray-300">{title}</span>
          <span className="ml-auto flex items-center gap-3 text-gray-500">
            <span className="h-px w-3 bg-gray-500" />
          <span className="h-2.5 w-2.5 border border-gray-500" />
          <span className="text-[13px] leading-none">×</span>
        </span>
      </div>

      {/* Window body with the launcher's 880:600 aspect. */}
      <div className="relative aspect-880/600 w-full bg-[#15100a] text-gray-100">
        {backgroundUrl && (
          <img
            src={backgroundUrl}
            alt=""
            className="absolute inset-0 h-full w-full object-cover opacity-60"
          />
        )}
        {/* Warm scrim for legibility, matching the launcher's gradient. */}
        <div className="absolute inset-0 bg-linear-to-b from-[#15100a]/20 to-[#120d07]/70" />

        <div className="relative flex h-full flex-col p-4">
          {/* Top bar: nav (top-left) + logo (top-right). */}
          <div className="flex items-start justify-between gap-3">
            <nav className="flex items-start gap-4 text-[13px] font-bold">
              {[
                { label: 'Play', active: true },
                { label: 'Addons', active: false },
                { label: 'Settings', active: false },
              ].map((item) => (
                <div key={item.label} className="flex flex-col items-center">
                  <span style={{ color: item.active ? '#FFD980' : '#B3A384' }}>{item.label}</span>
                  <span
                    className="mt-1 h-[2px] w-full rounded"
                    style={{ backgroundColor: item.active ? accent : 'transparent' }}
                  />
                </div>
              ))}
            </nav>

            {logoUrl ? (
              <img src={logoUrl} alt="logo" className="max-h-12 max-w-[150px] object-contain" />
            ) : (
              <div className="flex h-10 w-24 items-center justify-center rounded bg-white/10 text-[10px] text-gray-400">
                logo
              </div>
            )}
          </div>

          {/* Middle: horizontal "Latest News" strip like the launcher (cards + an Armory card). */}
          <div className="mt-4 flex-1 overflow-hidden">
            <div className="mb-2 flex items-center gap-3">
              <span className="text-sm font-bold" style={{ color: '#F0C869' }}>
                Latest News
              </span>
              <span className="text-[11px] text-gray-300/70">View all</span>
            </div>
            <div className="flex gap-2">
              {[0, 1, 2].map((i) => (
                <div
                  key={i}
                  className="flex w-1/4 flex-col overflow-hidden rounded-md border border-white/10 bg-black/30"
                >
                  <div className="flex h-12 items-center justify-center bg-white/5">
                    <svg
                      className="h-4 w-4 text-gray-500"
                      fill="none"
                      viewBox="0 0 24 24"
                      stroke="currentColor"
                      strokeWidth={1.5}
                    >
                      <path
                        strokeLinecap="round"
                        strokeLinejoin="round"
                        d="M2.25 15.75l5.159-5.159a2.25 2.25 0 013.182 0l5.159 5.159m-1.5-1.5l1.409-1.409a2.25 2.25 0 013.182 0l2.909 2.909M18 6.75h.008v.008H18V6.75z"
                      />
                    </svg>
                  </div>
                  <div className="p-2">
                    <div className="mb-1 h-1.5 w-4/5 rounded bg-white/25" />
                    <div className="h-1 w-2/5 rounded bg-white/15" />
                  </div>
                </div>
              ))}
              {/* Armory card. */}
              <div
                className="flex w-1/4 flex-col items-center justify-center gap-1 rounded-md border p-2 text-center"
                style={{ backgroundColor: '#26F0C869', borderColor: '#8A6D3B' }}
              >
                <span className="text-lg leading-none" style={{ color: '#F0C869' }}>
                  →
                </span>
                <span className="text-[11px] font-bold" style={{ color: '#FFD980' }}>
                  Armory
                </span>
              </div>
            </div>
          </div>

          {/* Footer: status + progress (left), Update/Play + burger (right), server picker below. */}
          <div className="mt-3 flex items-end gap-4">
            <div className="min-w-0 flex-1">
              <div className="mb-1 flex items-center justify-between gap-2 text-[11px]">
                <span className="truncate font-semibold text-[#E8DCC4]">Ready to play</span>
                <span className="shrink-0 text-gray-300/70">3.3.5a (12340)</span>
              </div>
              <div className="h-2 w-full overflow-hidden rounded bg-white/10">
                <div className="h-full w-2/3 rounded" style={{ backgroundColor: accent }} />
              </div>
            </div>

            <div className="flex shrink-0 flex-col items-end gap-1.5">
              <div className="flex items-center gap-2">
                <span
                  className="rounded px-4 py-1 text-[11px] font-bold text-[#1A1208] shadow"
                  style={{ backgroundColor: accent }}
                >
                  Play
                </span>
                <span className="rounded border border-white/15 bg-black/30 px-2 py-1 text-[11px] text-gray-200">
                  ☰
                </span>
              </div>
              <div className="flex w-36 items-center justify-between rounded border border-white/15 bg-black/30 px-2 py-1 text-[11px]">
                <span className="truncate">My Realm</span>
                <span className="text-gray-400">▾</span>
              </div>
              <span className="text-[10px] text-gray-300/70">logon.myrealm.example:3724</span>
            </div>
          </div>
        </div>
      </div>
    </div>
  )
}
