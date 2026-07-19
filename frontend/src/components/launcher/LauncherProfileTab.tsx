import { useEffect, useMemo, useState } from 'react'
import { Loader2, Save, Upload, CheckCircle2, Trash2, Eye, EyeOff } from 'lucide-react'
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
import { apiErrorMessage as errorMessage } from '@/lib/utils'

export default function LauncherProfileTab({ stackId }: { stackId: string }) {
  const { data, isLoading, error } = useLauncherProfile(stackId)
  const saveProfile = useSaveLauncherProfile(stackId)
  const uploadAsset = useUploadProfileAsset(stackId)
  const deleteAsset = useDeleteProfileAsset(stackId)
  const { data: templates } = useLauncherTemplates()
  // The theme (accent + default wallpaper/logo) is a single global choice; this tab only overrides
  // the wallpaper and logo, so we read the global config to resolve the effective preview visuals.
  const { data: globalConfig } = useLauncherConfig()

  const [form, setForm] = useState<LauncherProfileConfigDto | null>(null)
  const [saved, setSaved] = useState(false)
  const [formError, setFormError] = useState<string | null>(null)
  // Bump to bust the preview <img> cache after an asset upload/remove.
  const [assetVersion, setAssetVersion] = useState(0)

  useEffect(() => {
    if (data && !form) {
      setForm(data)
    }
  }, [data, form])

  // Resolve the effective launcher visuals for this stack: an uploaded per-stack wallpaper/logo wins,
  // otherwise the global theme's asset.
  const preview = useMemo(() => {
    const theme = templates?.find((t) => t.id === globalConfig?.template)
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
  }, [templates, globalConfig?.template, form?.displayName, form?.hasBackground, form?.hasLogo, assetVersion, stackId])

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
      <div className="flex items-center justify-center py-12 text-gray-500">
        <Loader2 className="h-6 w-6 animate-spin" />
      </div>
    )
  }

  if (error) {
    return <div className="rounded-md bg-red-50 p-4 text-red-700">{errorMessage(error)}</div>
  }

  return (
    <div className="max-w-3xl space-y-6">
      {formError && (
        <div className="rounded-md bg-red-50 p-3 text-sm text-red-700">{formError}</div>
      )}

      <div
        className={`rounded-lg border p-5 shadow-sm ${
          form.visible ? 'border-green-200 bg-green-50/70' : 'border-amber-300 bg-amber-50'
        }`}
      >
        <div className="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
          <div className="flex gap-3">
            <div
              className={`mt-0.5 flex h-10 w-10 shrink-0 items-center justify-center rounded-full ${
                form.visible ? 'bg-green-100 text-green-700' : 'bg-amber-100 text-amber-800'
              }`}
            >
              {form.visible ? <Eye className="h-5 w-5" /> : <EyeOff className="h-5 w-5" />}
            </div>
            <div>
              <div className="flex flex-wrap items-center gap-2">
                <h3 className="text-base font-semibold text-gray-900">Launcher visibility</h3>
                <span
                  className={`rounded-full px-2.5 py-0.5 text-xs font-semibold ${
                    form.visible ? 'bg-green-100 text-green-800' : 'bg-amber-200 text-amber-950'
                  }`}
                >
                  {form.visible ? 'Visible to players' : 'Hidden from launcher'}
                </span>
              </div>
              <p className="mt-1.5 max-w-2xl text-sm leading-relaxed text-gray-700">
                {form.visible ? (
                  <>
                    Players can select this server in the desktop launcher. It is included in the shared
                    server list that is pushed to every visible stack.
                  </>
                ) : (
                  <>
                    This server is <strong>not listed</strong> in the desktop launcher and is omitted from
                    the cross-stack server registry. Enable visibility below and click{' '}
                    <strong>Save profile</strong> to make it appear for players.
                  </>
                )}
              </p>
            </div>
          </div>

          <label className="inline-flex shrink-0 cursor-pointer items-center gap-3 rounded-lg border border-gray-300 bg-white px-4 py-3 shadow-sm transition hover:bg-gray-50">
            <input
              type="checkbox"
              className="h-5 w-5 rounded border-gray-300 text-blue-600 focus:ring-2 focus:ring-blue-500"
              checked={form.visible}
              onChange={(e) => update({ visible: e.target.checked })}
            />
            <span className="text-sm font-semibold text-gray-900">Show this server in the launcher</span>
          </label>
        </div>
      </div>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
        <label className="block">
          <span className="text-sm font-medium text-gray-700">Display name</span>
          <input
            className="mt-1 w-full rounded-md border px-3 py-2"
            value={form.displayName}
            onChange={(e) => update({ displayName: e.target.value })}
            placeholder="My Realm"
          />
        </label>
        <label className="block">
          <span className="text-sm font-medium text-gray-700">Sort order</span>
          <input
            type="number"
            className="mt-1 w-full rounded-md border px-3 py-2"
            value={form.sortOrder}
            onChange={(e) => update({ sortOrder: Number(e.target.value) || 0 })}
          />
        </label>
        <label className="block md:col-span-2">
          <span className="text-sm font-medium text-gray-700">Client version label</span>
          <input
            className="mt-1 w-full rounded-md border px-3 py-2"
            value={form.clientVersion}
            onChange={(e) => update({ clientVersion: e.target.value })}
            placeholder="3.3.5a (12340)"
          />
          <span className="mt-1 block text-xs text-gray-500">
            Informational WoW client label shown in the launcher when this server is selected. Leave
            blank to use the global default.
          </span>
        </label>
        <label className="block md:col-span-2">
          <span className="text-sm font-medium text-gray-700">Description</span>
          <input
            className="mt-1 w-full rounded-md border px-3 py-2"
            value={form.description}
            onChange={(e) => update({ description: e.target.value })}
          />
        </label>
      </div>

      <div>
        <span className="text-sm font-medium text-gray-700">Branding overrides</span>
        <p className="mt-0.5 text-xs text-gray-500">
          The launcher&rsquo;s theme and accent color are set once on the global{' '}
          <strong>Launcher</strong> page. Here you can override just this server&rsquo;s wallpaper and
          logo; leave them empty to use the global theme&rsquo;s artwork.
        </p>
      </div>

      <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
        <div>
          <span className="text-sm font-medium text-gray-700">Wallpaper</span>
          <p className="mt-0.5 text-xs text-gray-500">
            Static image (PNG, JPG or WebP). Animated backgrounds aren&rsquo;t supported.
          </p>
          <div className="mt-2 flex flex-wrap items-center gap-3">
            <label className="inline-flex cursor-pointer items-center gap-2 rounded-md border px-3 py-2 text-sm hover:bg-gray-50">
              <Upload className="h-4 w-4" /> Upload
              <input
                type="file"
                accept="image/*"
                className="hidden"
                onChange={(e) => onUpload('background', e.target.files?.[0])}
              />
            </label>
            {form.hasBackground ? (
              <>
                <span className="inline-flex items-center gap-1 text-sm text-green-600">
                  <CheckCircle2 className="h-4 w-4" /> Uploaded
                </span>
                <button
                  type="button"
                  onClick={() => onRemove('background')}
                  disabled={deleteAsset.isPending}
                  className="inline-flex items-center gap-1 rounded-md border border-red-200 px-2 py-1 text-xs text-red-600 hover:bg-red-50 disabled:opacity-50"
                >
                  <Trash2 className="h-3.5 w-3.5" /> Remove
                </button>
              </>
            ) : (
              <span className="text-sm text-gray-400">Uses global theme</span>
            )}
          </div>
        </div>
        <div>
          <span className="text-sm font-medium text-gray-700">Logo</span>
          <p className="mt-0.5 text-xs text-gray-500">Transparent PNG works best.</p>
          <div className="mt-2 flex flex-wrap items-center gap-3">
            <label className="inline-flex cursor-pointer items-center gap-2 rounded-md border px-3 py-2 text-sm hover:bg-gray-50">
              <Upload className="h-4 w-4" /> Upload
              <input
                type="file"
                accept="image/*"
                className="hidden"
                onChange={(e) => onUpload('logo', e.target.files?.[0])}
              />
            </label>
            {form.hasLogo ? (
              <>
                <span className="inline-flex items-center gap-1 text-sm text-green-600">
                  <CheckCircle2 className="h-4 w-4" /> Uploaded
                </span>
                <button
                  type="button"
                  onClick={() => onRemove('logo')}
                  disabled={deleteAsset.isPending}
                  className="inline-flex items-center gap-1 rounded-md border border-red-200 px-2 py-1 text-xs text-red-600 hover:bg-red-50 disabled:opacity-50"
                >
                  <Trash2 className="h-3.5 w-3.5" /> Remove
                </button>
              </>
            ) : (
              <span className="text-sm text-gray-400">Uses global theme</span>
            )}
          </div>
        </div>
      </div>

      <div className="border-t pt-6">
        <div className="mb-2 text-sm font-medium text-gray-700">Preview</div>
        <div className="max-w-xl">
          <LauncherPreview
            title={preview.title}
            accent={preview.accent}
            backgroundUrl={preview.backgroundUrl}
            logoUrl={preview.logoUrl}
            iconUrl={preview.iconUrl}
          />
        </div>
        <p className="mt-2 text-xs text-gray-400">
          Static mock — the launcher applies the global theme&rsquo;s accent color to the nav, buttons
          and progress bar, and shows this server&rsquo;s wallpaper (or the global theme&rsquo;s) behind
          the content when a player selects it.
        </p>
      </div>

      <div className="flex items-center gap-3">
        <button
          onClick={onSave}
          disabled={saveProfile.isPending}
          className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-white hover:bg-blue-700 disabled:opacity-50"
        >
          {saveProfile.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
          Save profile
        </button>
        {saved && (
          <span className="inline-flex items-center gap-1 text-sm text-green-600">
            <CheckCircle2 className="h-4 w-4" /> Saved
          </span>
        )}
      </div>
    </div>
  )
}
