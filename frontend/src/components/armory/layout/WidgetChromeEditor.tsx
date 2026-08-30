import type { ArmoryLayoutWidgetDto, WidgetChromeDto, WidgetShadowPreset } from '@/types/armory.types'
import { WIDGET_CATALOG } from '@/lib/armory-layout'

const SHADOW_OPTIONS: { value: WidgetShadowPreset; label: string }[] = [
  { value: 'Theme', label: 'Theme default' },
  { value: 'None', label: 'None' },
  { value: 'Sm', label: 'Small' },
  { value: 'Md', label: 'Medium' },
  { value: 'Lg', label: 'Large' },
]

interface WidgetChromeEditorProps {
  widget: ArmoryLayoutWidgetDto | null
  onChange: (widget: ArmoryLayoutWidgetDto) => void
  onResetChrome: () => void
}

export default function WidgetChromeEditor({ widget, onChange, onResetChrome }: WidgetChromeEditorProps) {
  if (!widget) {
    return (
      <p className="text-sm text-gray-500">
        Select a widget on the grid to edit its title, settings, and visual chrome.
      </p>
    )
  }

  const meta = WIDGET_CATALOG[widget.type]
  const chrome = widget.chrome ?? {}

  const patchSettings = (key: string, value: unknown) => {
    onChange({
      ...widget,
      settings: { ...(widget.settings ?? {}), [key]: value },
    })
  }

  const patchChrome = (patch: Partial<WidgetChromeDto>) => {
    onChange({
      ...widget,
      chrome: { ...chrome, ...patch },
    })
  }

  return (
    <div className="space-y-4 rounded-lg border border-gray-200 bg-gray-50/50 p-4">
      <div className="flex items-start justify-between gap-3">
        <div>
          <h3 className="font-semibold text-gray-900">{meta.label}</h3>
          <p className="text-xs text-gray-500">{meta.description}</p>
        </div>
        <label className="flex items-center gap-2 text-sm text-gray-600">
          <input
            type="checkbox"
            checked={widget.visible !== false}
            onChange={(e) => onChange({ ...widget, visible: e.target.checked })}
          />
          Visible
        </label>
      </div>

      {(widget.type === 'News' || widget.type === 'RecentCharacters') && (
        <div className="grid grid-cols-2 gap-3">
          <label className="text-sm">
            <span className="mb-1 block text-gray-600">Panel title</span>
            <input
              className="w-full rounded-md border border-gray-300 px-2 py-1.5 text-sm"
              value={String(widget.settings?.title ?? '')}
              onChange={(e) => patchSettings('title', e.target.value)}
            />
          </label>
          <label className="text-sm">
            <span className="mb-1 block text-gray-600">Item limit</span>
            <input
              type="number"
              min={1}
              max={20}
              className="w-full rounded-md border border-gray-300 px-2 py-1.5 text-sm"
              value={Number(widget.settings?.limit ?? (widget.type === 'News' ? 3 : 5))}
              onChange={(e) => patchSettings('limit', Number.parseInt(e.target.value, 10) || 1)}
            />
          </label>
        </div>
      )}

      {widget.type === 'News' && (
        <label className="flex items-center gap-2 text-sm text-gray-600">
          <input
            type="checkbox"
            checked={widget.settings?.showViewAll !== false}
            onChange={(e) => patchSettings('showViewAll', e.target.checked)}
          />
          Show &quot;View all&quot; link
        </label>
      )}

      <div className="border-t pt-4">
        <div className="mb-3 flex items-center justify-between">
          <h4 className="text-sm font-medium text-gray-900">Visual chrome</h4>
          <button type="button" onClick={onResetChrome} className="text-xs text-blue-600 hover:underline">
            Reset to theme
          </button>
        </div>

        <div className="grid grid-cols-2 gap-3">
          <label className="col-span-2 flex items-center gap-2 text-sm text-gray-600">
            <input
              type="checkbox"
              checked={chrome.borderEnabled !== false}
              onChange={(e) => patchChrome({ borderEnabled: e.target.checked })}
            />
            Show border
          </label>

          <ColorField
            label="Border color"
            value={chrome.borderColor ?? 'theme'}
            onChange={(v) => patchChrome({ borderColor: v })}
            allowTheme
          />
          <label className="text-sm">
            <span className="mb-1 block text-gray-600">Border width (px)</span>
            <input
              type="number"
              min={0}
              max={8}
              className="w-full rounded-md border border-gray-300 px-2 py-1.5 text-sm"
              value={chrome.borderWidth ?? 1}
              onChange={(e) => patchChrome({ borderWidth: Number.parseInt(e.target.value, 10) })}
            />
          </label>

          <label className="text-sm">
            <span className="mb-1 block text-gray-600">Corner radius (px)</span>
            <input
              type="range"
              min={0}
              max={24}
              className="w-full"
              value={chrome.borderRadius ?? 6}
              onChange={(e) => patchChrome({ borderRadius: Number.parseInt(e.target.value, 10) })}
            />
            <span className="text-xs text-gray-500">{chrome.borderRadius ?? 6}px</span>
          </label>

          <ColorField
            label="Background"
            value={chrome.backgroundColor ?? 'theme'}
            onChange={(v) => patchChrome({ backgroundColor: v })}
            allowTheme
            allowTransparent
          />

          <label className="text-sm">
            <span className="mb-1 block text-gray-600">Shadow</span>
            <select
              className="w-full rounded-md border border-gray-300 px-2 py-1.5 text-sm"
              value={chrome.shadow ?? 'Theme'}
              onChange={(e) => patchChrome({ shadow: e.target.value as WidgetShadowPreset })}
            >
              {SHADOW_OPTIONS.map((opt) => (
                <option key={opt.value} value={opt.value}>
                  {opt.label}
                </option>
              ))}
            </select>
          </label>

          <ColorField
            label="Title color"
            value={chrome.titleColor ?? 'theme'}
            onChange={(v) => patchChrome({ titleColor: v })}
            allowTheme
          />
        </div>
      </div>
    </div>
  )
}

function ColorField({
  label,
  value,
  onChange,
  allowTheme,
  allowTransparent,
}: {
  label: string
  value: string
  onChange: (value: string) => void
  allowTheme?: boolean
  allowTransparent?: boolean
}) {
  const isHex = value.startsWith('#')

  return (
    <label className="text-sm">
      <span className="mb-1 block text-gray-600">{label}</span>
      <div className="flex gap-2">
        <select
          className="min-w-0 flex-1 rounded-md border border-gray-300 px-2 py-1.5 text-sm"
          value={isHex ? '__custom__' : value}
          onChange={(e) => {
            if (e.target.value !== '__custom__') onChange(e.target.value)
          }}
        >
          {allowTheme && <option value="theme">Theme default</option>}
          {allowTransparent && <option value="transparent">Transparent</option>}
          <option value="__custom__">Custom…</option>
        </select>
        <input
          type="color"
          className="h-9 w-10 shrink-0 cursor-pointer rounded border border-gray-300"
          value={isHex ? value : '#5a4628'}
          onChange={(e) => onChange(e.target.value)}
          title="Pick custom color"
        />
      </div>
    </label>
  )
}

