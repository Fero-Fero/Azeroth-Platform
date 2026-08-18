import { useEffect, useMemo, useState } from 'react'
import {
  Loader2,
  Save,
  Upload,
  CheckCircle2,
  Trash2,
  Eye,
  EyeOff,
  Palette,
  Settings2,
} from 'lucide-react'
import {
  useLauncherConfig,
  useLauncherProfile,
  useLauncherTemplates,
  useSaveLauncherProfile,
  useUploadProfileAsset,
  useDeleteProfileAsset,
} from '@/hooks/useLauncher'
import type { LauncherProfileConfigDto } from '@/types/launcher.types'
import LauncherPreview from '@/components/launcher/LauncherPreview'
import {
  StackSectionTabs,
  StackTabHeader,
  StackTabInfoDetails,
  StackTabPanel,
  StackTabPanelHeader,
  StackTabSideCard,
} from '@/components/layout/StackTabChrome'
import { apiErrorMessage as errorMessage, cn } from '@/lib/utils'

type LauncherSection = 'profile' | 'branding' | 'preview'

export default function LauncherProfileTab({ stackId }: { stackId: string }) {
  const { data, isLoading, error } = useLauncherProfile(stackId)
  const saveProfile = useSaveLauncherProfile(stackId)
  const uploadAsset = useUploadProfileAsset(stackId)
  const deleteAsset = useDeleteProfileAsset(stackId)
  const { data: templates } = useLauncherTemplates()
  const { data: globalConfig } = useLauncherConfig()

  const [section, setSection] = useState<LauncherSection>('profile')
  const [form, setForm] = useState<LauncherProfileConfigDto | null>(null)
  const [saved, setSaved] = useState(false)
  const [formError, setFormError] = useState<string | null>(null)
  const [assetVersion, setAssetVersion] = useState(0)

  useEffect(() => {
    if (data && !form) {
      setForm(data)
    }
  }, [data, form])

  const preview = useMemo(() => {
    const effectiveTemplateId = form?.template?.trim() || globalConfig?.template || ''
    const theme = templates?.find((t) => t.id === effectiveTemplateId)
    const bust = assetVersion ? `?v=${assetVersion}` : ''
    return {
      title: form?.displayName || 'My Realm',
      accent: theme?.accentColor || '#4FA8D8',
      backgroundUrl: form?.hasBackground
        ? `/api/stacks/${stackId}/launcher/profile-asset/background${bust}`
        : theme?.backgroundUrl || null,
      logoUrl: form?.hasLogo
        ? `/api/stacks/${stackId}/launcher/profile-asset/logo${bust}`
        : theme?.logoUrl || null,
      iconUrl: theme?.iconUrl || null,
    }
  }, [
    templates,
    globalConfig?.template,
    form?.template,
    form?.displayName,
    form?.hasBackground,
    form?.hasLogo,
    assetVersion,
    stackId,
  ])

  const globalThemeName = useMemo(
    () => templates?.find((t) => t.id === globalConfig?.template)?.name ?? 'Global default',
    [templates, globalConfig?.template],
  )

  const selectedThemeDescription = useMemo(() => {
    const effectiveId = form?.template?.trim() || globalConfig?.template || ''
    return templates?.find((t) => t.id === effectiveId)?.description
  }, [form?.template, globalConfig?.template, templates])

  const update = (patch: Partial<LauncherProfileConfigDto>) =>
    setForm((prev) => (prev ? { ...prev, ...patch } : prev))

  const onSave = async () => {
    if (!form) return
    setFormError(null)
    try {
      const updated = await saveProfile.mutateAsync(form)
      setForm(updated)
      setSaved(true)
      setTimeout(() => setSaved(false), 2000)
    } catch (err) {
      setFormError(errorMessage(err))
    }
  }

  const onUpload = async (kind: 'background' | 'logo', file?: File | null) => {
    if (!file) return
    setFormError(null)
    try {
      const updated = await uploadAsset.mutateAsync({ kind, file })
      setForm(updated)
      setAssetVersion((v) => v + 1)
    } catch (err) {
      setFormError(errorMessage(err))
    }
  }

  const onRemove = async (kind: 'background' | 'logo') => {
    setFormError(null)
    try {
      const updated = await deleteAsset.mutateAsync(kind)
      setForm(updated)
      setAssetVersion((v) => v + 1)
    } catch (err) {
      setFormError(errorMessage(err))
    }
  }

  if (isLoading || !form) {
    return (
      <div className="flex items-center justify-center py-12 text-slate-500">
        <Loader2 className="h-6 w-6 animate-spin" />
      </div>
    )
  }

  if (error) {
    return <div className="rounded-lg border border-red-200 bg-red-50 p-4 text-red-700">{errorMessage(error)}</div>
  }

  return (
    <div className="space-y-5">
      <StackTabHeader
        title="Launcher profile"
        subtitle="How this server appears in the desktop launcher - name, branding, and visibility."
      />

      <div
        className={cn(
          'overflow-hidden rounded-xl border shadow-sm',
          form.visible
            ? 'border-emerald-200 bg-linear-to-br from-emerald-50 to-white'
            : 'border-amber-200 bg-linear-to-br from-amber-50 to-white',
        )}
      >
        <div className="flex flex-col gap-4 px-5 py-5 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex gap-3">
            <div
              className={cn(
                'flex h-12 w-12 shrink-0 items-center justify-center rounded-full',
                form.visible ? 'bg-emerald-100 text-emerald-700' : 'bg-amber-100 text-amber-800',
              )}
            >
              {form.visible ? <Eye className="h-6 w-6" /> : <EyeOff className="h-6 w-6" />}
            </div>
            <div>
              <div className="flex flex-wrap items-center gap-2">
                <h3 className="text-base font-semibold text-slate-900">Launcher visibility</h3>
                <span
                  className={cn(
                    'rounded-full px-2.5 py-0.5 text-xs font-semibold',
                    form.visible ? 'bg-emerald-100 text-emerald-800' : 'bg-amber-200 text-amber-950',
                  )}
                >
                  {form.visible ? 'Visible to players' : 'Hidden from launcher'}
                </span>
              </div>
              <p className="mt-1 max-w-xl text-sm text-slate-600">
                {form.visible
                  ? 'Players can select this server in the desktop launcher.'
                  : 'Hidden servers are omitted from the launcher server list until you enable visibility and save.'}
              </p>
            </div>
          </div>
          <label className="inline-flex shrink-0 cursor-pointer items-center gap-3 rounded-lg border border-slate-300 bg-white px-4 py-3 shadow-sm transition hover:bg-slate-50">
            <input
              type="checkbox"
              className="h-5 w-5 rounded border-slate-300 text-blue-600 focus:ring-2 focus:ring-blue-500"
              checked={form.visible}
              onChange={(e) => update({ visible: e.target.checked })}
            />
            <span className="text-sm font-semibold text-slate-900">Show in launcher</span>
          </label>
        </div>
      </div>

      <div className="grid gap-4 lg:grid-cols-5">
        <StackTabSideCard
          className="lg:col-span-3"
          title="Launcher theme"
          description="Override the global theme for this stack, or inherit the platform default."
          icon={<Palette className="h-5 w-5" />}
          variant="light"
        >
          <label className="block">
            <span className="text-sm font-medium text-slate-700">Style template</span>
            <select
              className="mt-1.5 w-full rounded-lg border border-slate-300 bg-white px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
              value={form.template ?? ''}
              onChange={(e) => update({ template: e.target.value })}
            >
              <option value="">Inherit global theme ({globalThemeName})</option>
              {templates?.map((t) => (
                <option key={t.id} value={t.id}>
                  {t.name}
                </option>
              ))}
            </select>
          </label>
          {selectedThemeDescription && (
            <p className="mt-2 text-xs text-slate-500">{selectedThemeDescription}</p>
          )}
          <p className="mt-3 text-xs text-slate-500">
            Wallpaper and logo overrides are on the <strong>Branding</strong> tab. Change the platform-wide
            default on the{' '}
            <a href="/launcher" className="font-medium text-blue-700 hover:underline">
              global Launcher page
            </a>
            .
          </p>
        </StackTabSideCard>

        <StackTabSideCard
          className="lg:col-span-2"
          title="Save profile"
          description="Visibility and profile changes apply after saving."
          icon={<Save className="h-5 w-5" />}
        >
          <button
            type="button"
            onClick={() => void onSave()}
            disabled={saveProfile.isPending}
            className="inline-flex w-full items-center justify-center gap-2 rounded-lg bg-blue-600 px-4 py-2.5 text-sm font-semibold text-white hover:bg-blue-700 disabled:opacity-50"
          >
            {saveProfile.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
            Save profile
          </button>
          {saved && (
            <p className="mt-3 inline-flex items-center gap-1 text-sm text-emerald-300">
              <CheckCircle2 className="h-4 w-4" /> Saved
            </p>
          )}
        </StackTabSideCard>
      </div>

      <StackTabInfoDetails summary="Branding vs theme">
        Each stack can pick its own style template or inherit the global one. Custom wallpaper and logo
        uploads on the Branding tab take precedence over the template artwork.
      </StackTabInfoDetails>

      {formError && (
        <div className="rounded-lg border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">{formError}</div>
      )}

      <StackSectionTabs
        active={section}
        onChange={setSection}
        tabs={[
          { id: 'profile', label: 'Profile', hint: 'Name & description', icon: <Settings2 className="h-4 w-4" /> },
          { id: 'branding', label: 'Branding', hint: 'Wallpaper & logo', icon: <Palette className="h-4 w-4" /> },
          { id: 'preview', label: 'Preview', hint: 'Launcher mockup', icon: <Eye className="h-4 w-4" /> },
        ]}
      />

      <div role="tabpanel">
        {section === 'profile' && (
          <StackTabPanel>
            <StackTabPanelHeader title="Server profile" subtitle="Text shown when players browse servers in the launcher." />
            <div className="grid grid-cols-1 gap-4 px-4 py-4 sm:grid-cols-2 sm:px-5">
              <label className="block">
                <span className="text-sm font-medium text-slate-700">Display name</span>
                <input
                  className="mt-1.5 w-full rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                  value={form.displayName}
                  onChange={(e) => update({ displayName: e.target.value })}
                  placeholder="My Realm"
                />
              </label>
              <label className="block">
                <span className="text-sm font-medium text-slate-700">Sort order</span>
                <input
                  type="number"
                  className="mt-1.5 w-full rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                  value={form.sortOrder}
                  onChange={(e) => update({ sortOrder: Number(e.target.value) || 0 })}
                />
              </label>
              <label className="block sm:col-span-2">
                <span className="text-sm font-medium text-slate-700">Client version label</span>
                <input
                  className="mt-1.5 w-full rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                  value={form.clientVersion}
                  onChange={(e) => update({ clientVersion: e.target.value })}
                  placeholder="3.3.5a (12340)"
                />
                <span className="mt-1 block text-xs text-slate-500">Leave blank to use the global default.</span>
              </label>
              <label className="block sm:col-span-2">
                <span className="text-sm font-medium text-slate-700">Description</span>
                <input
                  className="mt-1.5 w-full rounded-lg border border-slate-300 px-3 py-2 text-sm focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500"
                  value={form.description}
                  onChange={(e) => update({ description: e.target.value })}
                />
              </label>
            </div>
          </StackTabPanel>
        )}

        {section === 'branding' && (
          <div className="grid gap-4 md:grid-cols-2">
            <BrandingUploadCard
              title="Wallpaper"
              hint="PNG, JPG or WebP. Static images only."
              hasAsset={form.hasBackground}
              busy={uploadAsset.isPending || deleteAsset.isPending}
              onUpload={(file) => void onUpload('background', file)}
              onRemove={() => void onRemove('background')}
            />
            <BrandingUploadCard
              title="Logo"
              hint="Transparent PNG works best."
              hasAsset={form.hasLogo}
              busy={uploadAsset.isPending || deleteAsset.isPending}
              onUpload={(file) => void onUpload('logo', file)}
              onRemove={() => void onRemove('logo')}
            />
          </div>
        )}

        {section === 'preview' && (
          <StackTabPanel>
            <StackTabPanelHeader
              title="Launcher preview"
              subtitle="Uses this stack's theme override when set, otherwise the global theme."
            />
            <div className="px-4 py-4 sm:px-5">
              <div className="max-w-xl">
                <LauncherPreview
                  title={preview.title}
                  accent={preview.accent}
                  backgroundUrl={preview.backgroundUrl}
                  logoUrl={preview.logoUrl}
                  iconUrl={preview.iconUrl}
                />
              </div>
            </div>
          </StackTabPanel>
        )}
      </div>
    </div>
  )
}

function BrandingUploadCard({
  title,
  hint,
  hasAsset,
  busy,
  onUpload,
  onRemove,
}: {
  title: string
  hint: string
  hasAsset: boolean
  busy: boolean
  onUpload: (file: File) => void
  onRemove: () => void
}) {
  return (
    <StackTabPanel>
      <StackTabPanelHeader title={title} subtitle={hint} />
      <div className="space-y-3 px-4 py-4 sm:px-5">
        <label className="flex cursor-pointer flex-col items-center gap-2 rounded-xl border-2 border-dashed border-blue-200 bg-blue-50/50 px-4 py-6 text-center transition hover:border-blue-300 hover:bg-blue-50">
          <Upload className="h-6 w-6 text-blue-600" />
          <span className="text-sm font-semibold text-slate-800">Upload {title.toLowerCase()}</span>
          <span className="text-xs text-slate-500">{hasAsset ? 'Replace current file' : 'Uses global theme when empty'}</span>
          <input
            type="file"
            accept="image/*"
            className="hidden"
            disabled={busy}
            onChange={(e) => {
              const file = e.target.files?.[0]
              if (file) onUpload(file)
              e.target.value = ''
            }}
          />
        </label>
        {hasAsset ? (
          <div className="flex flex-wrap items-center justify-between gap-2">
            <span className="inline-flex items-center gap-1 text-sm text-green-700">
              <CheckCircle2 className="h-4 w-4" /> Custom {title.toLowerCase()} uploaded
            </span>
            <button
              type="button"
              onClick={onRemove}
              disabled={busy}
              className="inline-flex items-center gap-1 rounded-lg border border-red-200 px-3 py-1.5 text-xs font-medium text-red-600 hover:bg-red-50 disabled:opacity-50"
            >
              <Trash2 className="h-3.5 w-3.5" /> Remove
            </button>
          </div>
        ) : (
          <p className="text-sm text-slate-500">No override - global theme artwork is used.</p>
        )}
      </div>
    </StackTabPanel>
  )
}
