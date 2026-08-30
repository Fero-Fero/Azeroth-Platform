import type { CSSProperties, ReactNode } from 'react'
import type { ArmoryLayoutWidgetDto, ArmoryPageId } from '@/types/armory.types'
import { getIntSetting, getStringSetting } from '@/lib/armory-layout'
import { widgetChromeStyle } from '@/lib/armory-layout'
import {
  MOCK_CHARACTER,
  MOCK_CHARACTERS,
  MOCK_GUILD,
  MOCK_GUILD_ROWS,
  MOCK_LOG_ROWS,
  MOCK_NEWS,
  MOCK_REALMS,
  MOCK_SEARCH_ROWS,
} from '@/data/armory-layout-mock'

interface WidgetPreviewContentProps {
  widget: ArmoryLayoutWidgetDto
  pageId: ArmoryPageId
  siteName: string
  multiRealm?: boolean
}

/** Rough preview row budget from grid height (accounts for chrome + padding). */
function previewRowBudget(widget: ArmoryLayoutWidgetDto, reserved = 2): number {
  return Math.max(1, widget.h - reserved)
}

export default function WidgetPreviewContent({
  widget,
  pageId,
  siteName,
  multiRealm = false,
}: WidgetPreviewContentProps) {
  const panelStyle = widgetChromeStyle(widget.chrome)
  const titleColor =
    widget.chrome?.titleColor && widget.chrome.titleColor !== 'theme'
      ? widget.chrome.titleColor
      : 'var(--armory-heading)'

  if (widget.type === 'PageTitle') {
    const title =
      pageId === 'home'
        ? `${siteName} Armory`
        : pageId === 'top-logs'
          ? 'Top Logs'
          : pageId === 'connect'
            ? 'Connect'
            : pageId === 'news-list'
              ? 'News'
              : `${siteName} Armory`
    return (
      <PreviewRoot allowOverflow>
        <div className="flex h-full min-h-0 items-center">
          <h2 className="m-0 w-full text-sm font-bold leading-none" style={{ color: 'var(--armory-heading)' }}>
            {title}
          </h2>
        </div>
      </PreviewRoot>
    )
  }

  if (widget.type === 'Spacer') {
    return (
      <PreviewRoot>
        <div className="h-full w-full rounded border border-dashed opacity-30" style={{ borderColor: 'var(--armory-border)' }} />
      </PreviewRoot>
    )
  }

  if (widget.type === 'News' || widget.type === 'NewsFeed') {
    const limit = Math.min(
      getIntSetting(widget, 'limit', widget.type === 'NewsFeed' ? 12 : 3),
      previewRowBudget(widget, 1),
    )
    return (
      <PreviewRoot>
        <Panel shellStyle={panelStyle} title={getStringSetting(widget, 'title', 'Latest News')} titleColor={titleColor}>
          <div className="space-y-1.5 p-2">
            {MOCK_NEWS.slice(0, limit).map((item) => (
              <div
                key={item.id}
                className="rounded border px-2 py-1.5 text-[11px] leading-snug"
                style={{ borderColor: 'var(--armory-border)', color: 'var(--armory-text)' }}
              >
                <div className="truncate font-semibold" style={{ color: 'var(--armory-heading)' }}>
                  {item.title}
                </div>
                <div className="truncate text-[10px] opacity-70">{item.excerpt}</div>
              </div>
            ))}
          </div>
        </Panel>
      </PreviewRoot>
    )
  }

  if (widget.type === 'RecentCharacters') {
    const limit = Math.min(getIntSetting(widget, 'limit', 5), previewRowBudget(widget, 1))
    return (
      <PreviewRoot>
        <Panel
          shellStyle={panelStyle}
          title={getStringSetting(widget, 'title', 'Recently Active')}
          titleColor={titleColor}
        >
          <div className="flex flex-wrap gap-1.5 p-2">
            {MOCK_CHARACTERS.slice(0, limit).map((c) => (
              <div
                key={c.name}
                className="flex min-w-0 flex-1 basis-[calc(50%-0.375rem)] items-center gap-1.5 rounded border px-1.5 py-1 text-[10px] leading-tight"
                style={{ borderColor: 'var(--armory-border)', color: 'var(--armory-text)' }}
              >
                <div
                  className="h-6 w-6 shrink-0 rounded border"
                  style={{ borderColor: 'var(--armory-border)', background: 'var(--armory-surface)' }}
                />
                <div className="min-w-0">
                  <div className="truncate font-bold" style={{ color: 'var(--armory-heading)' }}>
                    {c.name}
                  </div>
                  <div className="opacity-70">Lvl {c.level}</div>
                </div>
              </div>
            ))}
          </div>
        </Panel>
      </PreviewRoot>
    )
  }

  if (widget.type === 'CharacterSearch') {
    const rowLimit = previewRowBudget(widget, multiRealm ? 2 : 1)
    return (
      <PreviewRoot>
        <div className="flex h-full min-h-0 flex-col gap-1.5">
          {multiRealm && (
            <div className="flex shrink-0 items-center gap-1.5 px-0.5 text-[11px]" style={{ color: 'var(--armory-text)' }}>
              <span>Realm:</span>
              <select
                className="max-w-full rounded border px-1.5 py-0.5 text-[11px]"
                style={{ borderColor: 'var(--armory-border)', background: 'var(--armory-input)' }}
                defaultValue={MOCK_REALMS[0]}
              >
                {MOCK_REALMS.map((r) => (
                  <option key={r}>{r}</option>
                ))}
              </select>
            </div>
          )}
          <div
            className="min-h-0 flex-1 overflow-hidden text-[10px] leading-tight"
            style={{
              ...panelStyle,
              border: panelStyle.border ?? '1px solid var(--armory-border)',
              borderRadius: panelStyle.borderRadius ?? 6,
              background:
                panelStyle.background ??
                'linear-gradient(180deg, color-mix(in srgb, var(--armory-panel) 95%, #fff), var(--armory-panel))',
            }}
          >
            <table className="w-full table-fixed border-collapse">
              <thead>
                <tr style={{ background: 'color-mix(in srgb, var(--armory-navbar) 50%, transparent)' }}>
                  {['Name', 'Online', 'Level', 'Class', 'Guild'].map((h) => (
                    <th
                      key={h}
                      className="truncate border px-1 py-0.5 text-left font-semibold"
                      style={{ borderColor: 'var(--armory-border)', color: 'var(--armory-heading)' }}
                    >
                      {h}
                    </th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {MOCK_SEARCH_ROWS.slice(0, rowLimit).map((row) => (
                  <tr key={row.name} style={{ color: 'var(--armory-text)' }}>
                    <td
                      className="truncate border px-1 py-0.5"
                      style={{ borderColor: 'var(--armory-border)', color: 'var(--armory-link)' }}
                    >
                      {row.name}
                    </td>
                    <td className="border px-1 py-0.5 text-center" style={{ borderColor: 'var(--armory-border)' }}>
                      {row.online ? '🟢' : '🔴'}
                    </td>
                    <td className="border px-1 py-0.5" style={{ borderColor: 'var(--armory-border)' }}>
                      {row.level}
                    </td>
                    <td className="truncate border px-1 py-0.5" style={{ borderColor: 'var(--armory-border)' }}>
                      {row.class}
                    </td>
                    <td className="truncate border px-1 py-0.5" style={{ borderColor: 'var(--armory-border)' }}>
                      {row.guild ?? '-'}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      </PreviewRoot>
    )
  }

  if (widget.type === 'CharacterHeader') {
    return (
      <PreviewRoot>
        <div
          className="flex h-full min-h-0 flex-wrap items-center gap-2 overflow-hidden rounded-md border p-2"
          style={{
            borderColor: panelStyle.borderColor,
            background: panelStyle.background,
            color: 'var(--armory-text)',
          }}
        >
          <div className="min-w-0 flex-1">
            <div className="truncate text-base font-bold" style={{ color: 'var(--armory-heading)' }}>
              {MOCK_CHARACTER.name}
            </div>
            <div className="truncate text-[11px] opacity-80">
              Level {MOCK_CHARACTER.level} {MOCK_CHARACTER.raceName} {MOCK_CHARACTER.className}
            </div>
            <div className="truncate text-[10px] opacity-70">&lt;{MOCK_CHARACTER.guild}&gt;</div>
          </div>
          <div className="shrink-0 text-right">
            <div className="text-[10px] opacity-70">Item Level</div>
            <div className="text-xl font-bold leading-none" style={{ color: 'var(--armory-heading)' }}>
              {MOCK_CHARACTER.ilvl}
            </div>
          </div>
        </div>
      </PreviewRoot>
    )
  }

  if (widget.type === 'CharacterModelViewer') {
    return (
      <PreviewRoot>
        <div
          className="flex h-full w-full items-center justify-center overflow-hidden rounded-md border border-dashed text-[11px]"
          style={{
            borderColor: 'var(--armory-border)',
            background: 'color-mix(in srgb, var(--armory-surface) 85%, transparent)',
            color: 'var(--armory-muted)',
          }}
        >
          3D model
        </div>
      </PreviewRoot>
    )
  }

  if (widget.type === 'CharacterStats') {
    const rows = ['Strength 142', 'Agility 98', 'Stamina 312', 'Intellect 45', 'Spirit 67'].slice(
      0,
      previewRowBudget(widget, 1),
    )
    return (
      <PreviewRoot>
        <Panel shellStyle={panelStyle} title="Stats" titleColor={titleColor}>
          <div className="space-y-0.5 p-2 text-[10px] leading-tight" style={{ color: 'var(--armory-text)' }}>
            {rows.map((row) => (
              <div key={row} className="flex justify-between border-b py-0.5" style={{ borderColor: 'var(--armory-border)' }}>
                {row}
              </div>
            ))}
          </div>
        </Panel>
      </PreviewRoot>
    )
  }

  if (widget.type === 'CharacterOverviewCards') {
    const cards = ['Talents', 'Professions', 'Honorable Kills', 'Mounts', 'Recent Achievements']
    return (
      <PreviewRoot>
        <div className="grid h-full min-h-0 grid-cols-2 gap-1.5 overflow-hidden p-0.5">
          {cards.map((label) => (
            <div
              key={label}
              className="flex min-h-0 flex-col overflow-hidden rounded-md border px-1 py-1.5 text-[10px] leading-tight"
              style={{ borderColor: 'var(--armory-border)', color: 'var(--armory-text)' }}
            >
              <div className="truncate font-semibold" style={{ color: 'var(--armory-heading)' }}>
                {label}
              </div>
              <div className="truncate opacity-60">Preview</div>
            </div>
          ))}
        </div>
      </PreviewRoot>
    )
  }

  if (widget.type === 'CharacterSubnav') {
    const tabs = ['Overview', 'Talents', 'Skills', 'Achievements', 'Progression', 'Logs']
    return (
      <PreviewRoot>
        <div className="flex h-full min-h-0 flex-wrap items-center gap-1 overflow-visible text-[10px] font-semibold uppercase leading-tight">
          {tabs.map((tab, i) => (
            <span
              key={tab}
              className="truncate rounded px-1.5 py-0.5"
              style={{
                background:
                  i === 0
                    ? 'linear-gradient(180deg, color-mix(in srgb, var(--armory-primary) 82%, var(--armory-navbar)), var(--armory-secondary))'
                    : 'transparent',
                color: i === 0 ? 'var(--armory-heading)' : 'var(--armory-text-muted)',
                textShadow: i === 0 ? '0 1px 2px rgba(0, 0, 0, 0.45)' : undefined,
              }}
            >
              {tab}
            </span>
          ))}
        </div>
      </PreviewRoot>
    )
  }

  if (widget.type === 'ConnectCta') {
    return (
      <PreviewRoot>
        <div
          className="flex h-full min-h-0 flex-col items-center justify-center gap-2 overflow-hidden rounded-md border p-3 text-center"
          style={{
            borderColor: 'var(--armory-border)',
            background: 'linear-gradient(180deg, color-mix(in srgb, var(--armory-panel) 92%, #fff), var(--armory-panel))',
          }}
        >
          <div className="truncate text-sm font-bold" style={{ color: 'var(--armory-heading)' }}>
            Download the Launcher
          </div>
          <p className="line-clamp-2 text-[11px] leading-snug opacity-80" style={{ color: 'var(--armory-text)' }}>
            Connect to {siteName} and keep your client up to date.
          </p>
          <button
            type="button"
            className="shrink-0 rounded-md px-3 py-1.5 text-[11px] font-medium"
            style={{ background: 'var(--armory-accent)', color: 'var(--armory-button-text)' }}
          >
            Get launcher
          </button>
        </div>
      </PreviewRoot>
    )
  }

  if (widget.type === 'GuildHeader') {
    return (
      <PreviewRoot>
        <div
          className="flex h-full min-h-0 flex-col justify-center overflow-hidden rounded-md border p-2"
          style={{ borderColor: 'var(--armory-border)' }}
        >
          <div className="truncate text-base font-bold" style={{ color: 'var(--armory-heading)' }}>
            &lt;{MOCK_GUILD.name}&gt;
          </div>
          <div className="truncate text-[11px] opacity-70" style={{ color: 'var(--armory-text)' }}>
            {MOCK_GUILD.realm} · {MOCK_GUILD.members} members
          </div>
        </div>
      </PreviewRoot>
    )
  }

  if (widget.type === 'GuildRoster' || widget.type === 'TopLogsTable') {
    const rowLimit = previewRowBudget(widget, 1)
    const rows = (widget.type === 'GuildRoster' ? MOCK_GUILD_ROWS : MOCK_LOG_ROWS).slice(0, rowLimit)
    const headers =
      widget.type === 'GuildRoster' ? ['Name', 'Level', 'Class', 'Rank'] : ['Rank', 'Character', 'Encounter', 'Time']
    return (
      <PreviewRoot>
        <Panel
          shellStyle={panelStyle}
          title={widget.type === 'GuildRoster' ? 'Roster' : 'Leaderboard'}
          titleColor={titleColor}
        >
          <table className="w-full table-fixed border-collapse text-[10px] leading-tight">
            <thead>
              <tr style={{ background: 'color-mix(in srgb, var(--armory-navbar) 50%, transparent)' }}>
                {headers.map((h) => (
                  <th
                    key={h}
                    className="truncate border px-1 py-0.5 text-left font-semibold"
                    style={{ borderColor: 'var(--armory-border)', color: 'var(--armory-heading)' }}
                  >
                    {h}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.map((row, i) => (
                <tr key={i} style={{ color: 'var(--armory-text)' }}>
                  {Object.values(row).map((cell, j) => (
                    <td key={j} className="truncate border px-1 py-0.5" style={{ borderColor: 'var(--armory-border)' }}>
                      {cell}
                    </td>
                  ))}
                </tr>
              ))}
            </tbody>
          </table>
        </Panel>
      </PreviewRoot>
    )
  }

  if (widget.type === 'MapCanvas') {
    return (
      <PreviewRoot>
        <div
          className="flex h-full w-full items-center justify-center overflow-hidden rounded-md border border-dashed text-[11px]"
          style={{
            borderColor: 'var(--armory-border)',
            background: 'color-mix(in srgb, var(--armory-surface) 85%, transparent)',
            color: 'var(--armory-muted)',
          }}
        >
          World map
        </div>
      </PreviewRoot>
    )
  }

  return (
    <PreviewRoot>
      <div className="flex h-full items-center justify-center text-[11px] opacity-50" style={{ color: 'var(--armory-text)' }}>
        {widget.type}
      </div>
    </PreviewRoot>
  )
}

function PreviewRoot({ children, allowOverflow = false }: { children: ReactNode; allowOverflow?: boolean }) {
  return (
    <div
      className={`widget-preview-root h-full max-h-full min-h-0 ${allowOverflow ? 'overflow-visible' : 'overflow-hidden'}`}
    >
      {children}
    </div>
  )
}

function Panel({
  title,
  titleColor,
  shellStyle,
  children,
}: {
  title: string
  titleColor: string
  shellStyle: CSSProperties
  children: ReactNode
}) {
  return (
    <div
      className="flex h-full min-h-0 flex-col overflow-hidden"
      style={{
        ...shellStyle,
        background:
          shellStyle.background ??
          'linear-gradient(180deg, color-mix(in srgb, var(--armory-panel) 95%, #fff), var(--armory-panel))',
        border: shellStyle.border ?? '1px solid var(--armory-border)',
        borderRadius: shellStyle.borderRadius ?? 6,
      }}
    >
      <div
        className="shrink-0 truncate px-2 py-1 text-[10px] font-bold uppercase tracking-wide"
        style={{
          borderBottom: '1px solid var(--armory-border)',
          color: titleColor,
          background: 'color-mix(in srgb, var(--armory-navbar) 60%, transparent)',
        }}
      >
        {title}
      </div>
      <div className="min-h-0 flex-1 overflow-hidden">{children}</div>
    </div>
  )
}
