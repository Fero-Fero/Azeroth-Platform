import { Plus, Settings2, X } from 'lucide-react'
import { useState } from 'react'
import {
  createNavbarLink,
  NAVBAR_LINK_KINDS,
  normalizeNavbar,
} from '@/lib/armory-layout'
import type { ArmoryNavbarDto, ArmoryNavbarLinkDto, ArmoryNavbarLinkKind } from '@/types/armory.types'
import DraggableNavbarStrip from './DraggableNavbarStrip'

interface NavbarEditorProps {
  navbar: ArmoryNavbarDto | undefined
  siteName?: string
  topLogsEnabled?: boolean
  worldMapEnabled?: boolean
  onChange: (navbar: ArmoryNavbarDto) => void
}

const ADDABLE_KINDS: ArmoryNavbarLinkKind[] = ['Home', 'TopLogs', 'Map', 'Connect', 'News', 'Custom']

export default function NavbarEditor({
  navbar,
  siteName = 'Azeroth',
  topLogsEnabled = true,
  worldMapEnabled = true,
  onChange,
}: NavbarEditorProps) {
  const config = normalizeNavbar(navbar)
  const [editingId, setEditingId] = useState<string | null>(null)
  const editingLink = config.links.find((link) => link.id === editingId) ?? null

  const update = (patch: Partial<ArmoryNavbarDto>) => {
    onChange({ ...config, ...patch })
  }

  const updateLink = (id: string, patch: Partial<ArmoryNavbarLinkDto>) => {
    update({
      links: config.links.map((link) => (link.id === id ? { ...link, ...patch } : link)),
    })
  }

  const removeLink = (id: string, kind: ArmoryNavbarLinkKind) => {
    if (kind === 'Home') return
    update({ links: config.links.filter((link) => link.id !== id) })
    if (editingId === id) setEditingId(null)
  }

  const addLink = (kind: ArmoryNavbarLinkKind) => {
    const link = createNavbarLink(kind)
    update({ links: [...config.links, link] })
    setEditingId(link.id)
  }

  const navbarLinkMuted = (link: ArmoryNavbarLinkDto) =>
    (link.kind === 'TopLogs' && !topLogsEnabled) || (link.kind === 'Map' && !worldMapEnabled)

  const usedSingletons = new Set(
    config.links.filter((l) => NAVBAR_LINK_KINDS[l.kind]?.singleton).map((l) => l.kind),
  )
  const canAdd = ADDABLE_KINDS.filter((kind) => !NAVBAR_LINK_KINDS[kind].singleton || !usedSingletons.has(kind))

  return (
    <div className="space-y-4 rounded-lg border border-gray-200 bg-gray-50 p-4">
      <div>
        <h3 className="text-sm font-semibold text-gray-900">Navigation bar</h3>
        <p className="mt-1 text-xs text-gray-500">
          Drag links to reorder them left to right. Click a link to edit its label or URL. Account sign-in is controlled
          by stack settings.
        </p>
      </div>

      <div className="flex flex-wrap items-center gap-4 rounded-md border border-gray-200 bg-white px-3 py-2">
        <label className="flex items-center gap-2 text-sm text-gray-700">
          <input
            type="checkbox"
            checked={config.showSearch !== false}
            onChange={(e) => update({ showSearch: e.target.checked })}
            className="rounded border-gray-300"
          />
          Show search
        </label>
        <label className="flex min-w-[220px] flex-1 items-center gap-2 text-sm text-gray-700">
          <span className="shrink-0 text-xs text-gray-500">Placeholder</span>
          <input
            type="text"
            value={config.searchPlaceholder ?? ''}
            disabled={config.showSearch === false}
            onChange={(e) => update({ searchPlaceholder: e.target.value })}
            className="w-full rounded-md border border-gray-300 px-2 py-1 text-sm disabled:bg-gray-100"
          />
        </label>
      </div>

      <div className="rounded-md border border-dashed border-gray-300 bg-white px-3 py-2">
        <div className="mb-1 text-[10px] font-medium uppercase tracking-wide text-gray-400">Live preview — drag to reorder</div>
        <DraggableNavbarStrip
          links={config.links}
          siteName={siteName}
          onReorder={(links) => update({ links })}
          onLinkClick={(id) => setEditingId((current) => (current === id ? null : id))}
          selectedLinkId={editingId}
          isLinkMuted={navbarLinkMuted}
          showSearch={config.showSearch !== false}
          searchPlaceholder={config.searchPlaceholder}
        />
      </div>

      <div className="overflow-x-auto rounded-md border border-gray-200 bg-white p-3">
        <div className="flex min-w-max items-stretch gap-2">
          {config.links.map((link) => {
            const meta = NAVBAR_LINK_KINDS[link.kind]
            const defaultLabel = link.kind === 'Home' ? siteName : meta.label
            const displayLabel = link.label?.trim() || defaultLabel
            const active = editingId === link.id
            const hidden = link.visible === false
            return (
              <div
                key={link.id}
                className={`flex min-w-[140px] max-w-[180px] flex-col rounded-md border ${
                  active ? 'border-blue-500 ring-1 ring-blue-500' : 'border-gray-200'
                } ${hidden ? 'opacity-50' : ''}`}
              >
                <div className="flex items-center justify-between gap-1 border-b bg-gray-50 px-2 py-1">
                  <button
                    type="button"
                    onClick={() => setEditingId(active ? null : link.id)}
                    className="flex min-w-0 flex-1 items-center gap-1 text-left text-xs font-medium text-gray-800"
                  >
                    <Settings2 className="h-3 w-3 shrink-0 text-gray-400" />
                    <span className="truncate">{displayLabel}</span>
                  </button>
                  <div className="flex shrink-0 items-center gap-0.5">
                    {link.kind !== 'Home' && (
                      <button
                        type="button"
                        onClick={() => removeLink(link.id, link.kind)}
                        className="rounded p-0.5 text-red-500 hover:bg-red-50"
                        aria-label="Remove link"
                      >
                        <X className="h-3.5 w-3.5" />
                      </button>
                    )}
                  </div>
                </div>
                <div className="px-2 py-1.5 text-[10px] text-gray-500">{meta.label}</div>
              </div>
            )
          })}

          {canAdd.length > 0 && (
            <div className="flex min-w-[120px] flex-col justify-center gap-1 rounded-md border border-dashed border-gray-300 px-2 py-2">
              <span className="text-[10px] font-medium uppercase text-gray-400">Add</span>
              <div className="flex flex-wrap gap-1">
                {canAdd.map((kind) => (
                  <button
                    key={kind}
                    type="button"
                    onClick={() => addLink(kind)}
                    className="inline-flex items-center gap-0.5 rounded border border-gray-200 px-1.5 py-0.5 text-[10px] text-gray-700 hover:bg-gray-50"
                  >
                    <Plus className="h-3 w-3" />
                    {NAVBAR_LINK_KINDS[kind].label}
                  </button>
                ))}
              </div>
            </div>
          )}
        </div>
      </div>

      {editingLink && (
        <div className="rounded-md border border-blue-200 bg-blue-50/40 p-4">
          <div className="mb-3 flex items-center justify-between">
            <h4 className="text-sm font-semibold text-gray-900">
              Edit {NAVBAR_LINK_KINDS[editingLink.kind].label}
            </h4>
            <button
              type="button"
              onClick={() => setEditingId(null)}
              className="text-xs text-gray-500 hover:text-gray-700"
            >
              Close
            </button>
          </div>
          <div className="grid gap-3 sm:grid-cols-2">
            <label className="flex items-center gap-2 text-sm text-gray-700 sm:col-span-2">
              <input
                type="checkbox"
                checked={editingLink.visible !== false}
                onChange={(e) => updateLink(editingLink.id, { visible: e.target.checked })}
                className="rounded border-gray-300"
              />
              Visible in navbar
            </label>
            <label className="block text-sm text-gray-700">
              <span className="mb-1 block text-xs font-medium text-gray-500">
                Label {editingLink.kind !== 'Custom' ? '(optional override)' : ''}
              </span>
              <input
                type="text"
                value={editingLink.label ?? ''}
                placeholder={editingLink.kind === 'Home' ? siteName : NAVBAR_LINK_KINDS[editingLink.kind].label}
                onChange={(e) => updateLink(editingLink.id, { label: e.target.value || null })}
                className="w-full rounded-md border border-gray-300 px-2 py-1.5 text-sm"
              />
            </label>
            {editingLink.kind === 'Custom' && (
              <label className="block text-sm text-gray-700">
                <span className="mb-1 block text-xs font-medium text-gray-500">URL or path</span>
                <input
                  type="text"
                  value={editingLink.href ?? ''}
                  placeholder="/news or https://…"
                  onChange={(e) => updateLink(editingLink.id, { href: e.target.value || null })}
                  className="w-full rounded-md border border-gray-300 px-2 py-1.5 text-sm"
                />
              </label>
            )}
            <label className="flex items-center gap-2 text-sm text-gray-700 sm:col-span-2">
              <input
                type="checkbox"
                checked={!!editingLink.openInNewTab}
                onChange={(e) => updateLink(editingLink.id, { openInNewTab: e.target.checked })}
                className="rounded border-gray-300"
              />
              Open in new tab
            </label>
          </div>
          {editingLink.kind === 'TopLogs' && !topLogsEnabled && (
            <p className="mt-2 text-xs text-amber-700">
              Top Logs is hidden in preview because the logs tracker module is not installed on this stack.
            </p>
          )}
        </div>
      )}
    </div>
  )
}
