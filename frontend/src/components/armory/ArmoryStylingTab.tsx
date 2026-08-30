import { useEffect, useMemo, useRef, useState } from 'react'
import { CheckCircle2, Image, Loader2, Palette, Save, Upload } from 'lucide-react'
import {
  armoryAssetsInfoKey,
  armoryStylingPreviewKey,
  useArmoryStyling,
  useArmoryStylingDefaults,
  useSaveArmoryStyling,
  useUploadArmoryWallpaper,
} from '@/hooks/useArmoryAssets'
import { useArmoryJobContext } from '@/contexts/ArmoryJobContext'
import { apiErrorMessage } from '@/lib/utils'
import {
  armoryPreviewCssVars,
  armoryPreviewWallpaperStyle,
  CLASSIC_STYLING_FALLBACK,
  STYLING_TEMPLATE_COPY,
} from '@/lib/armory-styling'
import type { ArmoryStyleTemplate, ArmoryStylingDto } from '@/types/armory.types'
import { useQueryClient } from '@tanstack/react-query'

const TEMPLATE_COPY = STYLING_TEMPLATE_COPY

const COLOR_FIELDS: { key: keyof Pick<ArmoryStylingDto, 'primaryColor' | 'secondaryColor' | 'accentColor' | 'backgroundColor' | 'surfaceColor' | 'panelColor' | 'borderColor' | 'navbarColor' | 'linkColor' | 'headingColor' | 'mutedTextColor' | 'inputColor' | 'buttonTextColor' | 'textColor'>; label: string }[] = [
  { key: 'primaryColor', label: 'Primary' },
  { key: 'secondaryColor', label: 'Secondary' },
  { key: 'accentColor', label: 'Accent' },
  { key: 'backgroundColor', label: 'Background' },
  { key: 'surfaceColor', label: 'Surface' },
  { key: 'panelColor', label: 'Panels' },
  { key: 'borderColor', label: 'Borders' },
  { key: 'navbarColor', label: 'Navigation' },
  { key: 'linkColor', label: 'Links' },
  { key: 'headingColor', label: 'Headings' },
  { key: 'mutedTextColor', label: 'Muted text' },
  { key: 'inputColor', label: 'Inputs' },
  { key: 'buttonTextColor', label: 'Button text' },
  { key: 'textColor', label: 'Text' },
]

export default function ArmoryStylingTab({ stackId }: { stackId: string }) {
  const qc = useQueryClient()
  const { data: styling, isLoading, error } = useArmoryStyling(stackId)
  const { data: stylingDefaults } = useArmoryStylingDefaults(stackId)
  const saveStyling = useSaveArmoryStyling(stackId)
  const uploadWallpaper = useUploadArmoryWallpaper(stackId)
  const { job } = useArmoryJobContext()

  const [draft, setDraft] = useState<ArmoryStylingDto>(CLASSIC_STYLING_FALLBACK)
  const [message, setMessage] = useState<string | null>(null)
  const [pageError, setPageError] = useState<string | null>(null)
  const [uploadPercent, setUploadPercent] = useState<number | null>(null)
  const fileRef = useRef<HTMLInputElement | null>(null)
  const prevRunningRef = useRef(false)

  useEffect(() => {
    if (styling) setDraft(styling)
  }, [styling])

  useEffect(() => {
    qc.setQueryData(armoryStylingPreviewKey(stackId), draft)
  }, [draft, qc, stackId])

  useEffect(() => {
    const running = job?.isRunning ?? false
    if (prevRunningRef.current && !running && job?.action === 'Rebuild') {
      qc.invalidateQueries({ queryKey: armoryAssetsInfoKey(stackId) })
      if (job.success) flash(job.message || 'Armory image rebuilt.')
      else if (job.error) setPageError(job.error)
    }
    prevRunningRef.current = running
  }, [job?.isRunning, job?.action, job?.success, job?.error, job?.message, qc, stackId])

  const previewVars = useMemo(() => armoryPreviewCssVars(draft, stylingDefaults), [draft, stylingDefaults])
  const previewWallpaper = useMemo(
    () => armoryPreviewWallpaperStyle(draft, stylingDefaults, stackId),
    [draft, stylingDefaults, stackId],
  )

  const flash = (text: string) => {
    setMessage(text)
    setTimeout(() => setMessage(null), 5000)
  }

  const selectTemplate = (template: ArmoryStyleTemplate) => {
    const defaults = stylingDefaults?.[template] ?? CLASSIC_STYLING_FALLBACK
    setDraft((current) => ({
      ...defaults,
      advancedEnabled: template === 'Custom',
      wallpaperUrl: template === 'Custom' ? current.wallpaperUrl : defaults.wallpaperUrl,
    }))
  }

  const onSave = async () => {
    setPageError(null)
    setMessage(null)
    try {
      await saveStyling.mutateAsync(draft)
      flash('Styling saved. Rebuild when prompted to apply changes to the armory image.')
    } catch (err) {
      setPageError(apiErrorMessage(err))
    }
  }

  const onWallpaper = async (file?: File | null) => {
    if (!file) return
    setPageError(null)
    setUploadPercent(0)
    try {
      const updated = await uploadWallpaper.mutateAsync({ file, onProgress: setUploadPercent })
      setDraft(updated)
      flash('Wallpaper uploaded. Rebuild when prompted to apply changes to the armory image.')
    } catch (err) {
      setPageError(apiErrorMessage(err))
    } finally {
      setUploadPercent(null)
      if (fileRef.current) fileRef.current.value = ''
    }
  }

  if (isLoading) {
    return (
      <div className="flex items-center justify-center py-16 text-gray-500">
        <Loader2 className="h-6 w-6 animate-spin" />
      </div>
    )
  }

  if (error) {
    return <div className="rounded-md bg-red-50 p-4 text-red-700">{apiErrorMessage(error)}</div>
  }

  return (
    <div className="space-y-6">
      {pageError && <div className="rounded-md bg-red-50 p-3 text-sm text-red-700">{pageError}</div>}
      {message && (
        <div className="inline-flex items-center gap-1 rounded-md bg-green-50 px-3 py-2 text-sm text-green-700">
          <CheckCircle2 className="h-4 w-4" /> {message}
        </div>
      )}

      <section className="rounded-lg border bg-white p-6 shadow-sm">
        <div className="mb-4 flex items-start justify-between gap-4">
          <div>
            <div className="mb-1 flex items-center gap-2">
              <Palette className="h-5 w-5 text-blue-600" />
              <h2 className="text-lg font-semibold text-gray-900">Armory Styling</h2>
            </div>
            <p className="text-sm text-gray-500">
              Choose a WoW expansion template for this stack&apos;s armory. Save styling and layout changes,
              then rebuild once when you&apos;re ready.
            </p>
          </div>
          <button
            type="button"
            onClick={onSave}
            disabled={saveStyling.isPending}
            className="inline-flex shrink-0 items-center gap-1.5 rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {saveStyling.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
            Save styling
          </button>
        </div>

        <div className="grid grid-cols-1 gap-3 lg:grid-cols-4">
          {(Object.keys(TEMPLATE_COPY) as ArmoryStyleTemplate[]).map((template) => {
            const active = draft.template === template
            return (
              <button
                key={template}
                type="button"
                onClick={() => selectTemplate(template)}
                className={`rounded-lg border p-4 text-left transition-colors ${
                  active
                    ? 'border-blue-500 bg-blue-50 ring-1 ring-blue-500'
                    : 'border-gray-200 bg-white hover:border-gray-300 hover:bg-gray-50'
                }`}
              >
                <div className="font-medium text-gray-900">{TEMPLATE_COPY[template].label}</div>
                <p className="mt-1 text-sm text-gray-500">{TEMPLATE_COPY[template].description}</p>
              </button>
            )
          })}
        </div>

        <div
          className="mt-6 overflow-hidden rounded-lg border p-5"
          style={{
            ...previewVars,
            color: 'var(--armory-text)',
            ...(previewWallpaper ?? {
              background:
                'linear-gradient(color-mix(in srgb, var(--armory-background) 86%, transparent), color-mix(in srgb, var(--armory-background) 86%, transparent))',
            }),
            borderColor: 'var(--armory-border)',
            boxShadow:
              '0 0 0 1px color-mix(in srgb, var(--armory-border) 45%, transparent), 0 24px 70px rgba(0, 0, 0, 0.35)',
          }}
        >
          <div
            className="rounded-md border p-4"
            style={{
              background:
                'linear-gradient(180deg, color-mix(in srgb, var(--armory-panel) 96%, var(--armory-primary) 4%), color-mix(in srgb, var(--armory-panel) 92%, black 8%))',
              borderColor: 'var(--armory-border)',
              boxShadow: 'inset 0 0 0 1px color-mix(in srgb, var(--armory-border) 35%, transparent)',
            }}
          >
            <div
              className="mb-3 rounded border px-3 py-2 text-sm"
              style={{
                background:
                  'linear-gradient(180deg, color-mix(in srgb, var(--armory-navbar) 90%, var(--armory-primary) 10%), var(--armory-navbar))',
                borderColor: 'var(--armory-border)',
                color: 'var(--armory-text)',
              }}
            >
              Navigation preview
            </div>
            <div className="text-sm" style={{ color: 'var(--armory-muted)' }}>Generated theme preview</div>
            <div className="mt-1 text-xl font-semibold" style={{ color: 'var(--armory-heading)' }}>
              {TEMPLATE_COPY[draft.template].label} armory
            </div>
            <a className="mt-1 inline-block text-sm" style={{ color: 'var(--armory-link)' }}>
              Character link
            </a>
            <div
              className="mt-3 rounded border p-3 text-sm"
              style={{
                background:
                  'linear-gradient(180deg, color-mix(in srgb, var(--armory-panel) 88%, transparent), color-mix(in srgb, var(--armory-surface) 92%, transparent))',
                borderColor: 'color-mix(in srgb, var(--armory-border) 72%, transparent)',
                color: 'var(--armory-text)',
              }}
            >
              <div className="font-medium" style={{ color: 'var(--armory-accent)' }}>
                News / card surface
              </div>
              <div className="mt-1" style={{ color: 'var(--armory-muted)' }}>
                This mirrors the themed gradients used for armory cards.
              </div>
            </div>
            <div
              className="mt-3 rounded border px-3 py-2 text-sm"
              style={{
                backgroundColor: 'var(--armory-input)',
                borderColor: 'var(--armory-border)',
                color: 'var(--armory-text)',
              }}
            >
              Search input
            </div>
            <button
              type="button"
              className="mt-4 rounded px-3 py-1.5 text-sm font-medium text-white"
              style={{
                backgroundColor: 'var(--armory-primary)',
                borderColor: 'var(--armory-primary)',
                color: 'var(--armory-button-text)',
              }}
            >
              Primary action
            </button>
          </div>
        </div>
      </section>

      <section className="rounded-lg border bg-white p-6 shadow-sm">
        <div className="flex items-start gap-3">
          <div className="mt-1 rounded-full bg-blue-50 p-1 text-blue-600">
            <Palette className="h-4 w-4" />
          </div>
          <div>
            <h3 className="font-medium text-gray-900">Custom styling</h3>
            <p className="text-sm text-gray-500">
              Select the Custom template to modify key colors yourself and upload a custom wallpaper.
              Expansion templates use their fixed theme wallpapers.
            </p>
          </div>
        </div>

        <div className="mt-5 grid grid-cols-1 gap-4 md:grid-cols-2 lg:grid-cols-3">
          {COLOR_FIELDS.map((field) => (
            <label key={field.key} className={draft.template !== 'Custom' ? 'opacity-50' : undefined}>
              <span className="mb-1 block text-sm font-medium text-gray-700">{field.label}</span>
              <div className="flex overflow-hidden rounded-md border border-gray-300">
                <input
                  type="color"
                  value={draft[field.key]}
                  disabled={draft.template !== 'Custom'}
                  onChange={(e) => setDraft((current) => ({ ...current, [field.key]: e.target.value }))}
                  className="h-10 w-12 border-0 bg-transparent p-1 disabled:cursor-not-allowed"
                />
                <input
                  type="text"
                  value={draft[field.key]}
                  disabled={draft.template !== 'Custom'}
                  onChange={(e) => setDraft((current) => ({ ...current, [field.key]: e.target.value }))}
                  className="min-w-0 flex-1 border-0 px-3 text-sm uppercase focus:ring-0 disabled:cursor-not-allowed disabled:bg-gray-50"
                />
              </div>
            </label>
          ))}
        </div>

        {draft.template === 'Custom' && (
          <div className="mt-5 rounded-md border border-dashed border-gray-300 bg-gray-50/50 p-4">
            <div className="mb-2 flex items-center gap-2 text-sm font-medium text-gray-700">
              <Image className="h-4 w-4 text-blue-600" /> Custom wallpaper
            </div>
            <div className="flex flex-wrap items-center gap-3">
              <button
                type="button"
                onClick={() => fileRef.current?.click()}
                disabled={uploadWallpaper.isPending || uploadPercent !== null}
                className="inline-flex items-center gap-1.5 rounded-md bg-gray-900 px-3 py-1.5 text-sm font-medium text-white hover:bg-gray-800 disabled:opacity-50"
              >
                {uploadWallpaper.isPending || uploadPercent !== null ? (
                  <Loader2 className="h-4 w-4 animate-spin" />
                ) : (
                  <Upload className="h-4 w-4" />
                )}
                {uploadPercent !== null && uploadPercent < 100 ? `Uploading… ${uploadPercent}%` : 'Upload wallpaper'}
              </button>
              {draft.wallpaperUrl && <span className="text-sm text-gray-500">Current: {draft.wallpaperUrl}</span>}
              <input
                ref={fileRef}
                type="file"
                accept=".jpg,.jpeg,.png,.webp,.gif,.avif"
                className="hidden"
                onChange={(e) => onWallpaper(e.target.files?.[0])}
              />
            </div>
          </div>
        )}
      </section>
    </div>
  )
}
