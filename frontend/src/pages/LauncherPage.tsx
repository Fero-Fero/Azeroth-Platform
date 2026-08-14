import { useEffect, useMemo, useRef, useState } from 'react'
import {
  Loader2,
  Download,
  Hammer,
  Upload,
  Save,
  CheckCircle2,
  XCircle,
  RefreshCw,
  Send,
  AlertTriangle,
} from 'lucide-react'
import {
  useLauncherConfig,
  useLauncherTemplates,
  useSaveLauncherConfig,
  useUploadLauncherAsset,
  useLauncherBuildStatus,
  useStartLauncherBuild,
  useLauncherStackVersions,
  useResendLauncherToStack,
} from '@/hooks/useLauncher'
import { launcherApi } from '@/services/api'
import type { LauncherDistributionConfigDto, LauncherVersionPart } from '@/types/launcher.types'
import LauncherPreview from '@/components/launcher/LauncherPreview'
import { apiErrorMessage as errorMessage } from '@/lib/utils'

function formatBytes(bytes: number): string {
  if (!bytes) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(2)} ${units[unit]}`
}

/**
 * A single labelled asset upload control. Uses a full-height flex column with the upload row pinned
 * to the bottom (`mt-auto`) so buttons line up across a row of uploaders regardless of helper length.
 */
function AssetUploader({
  label,
  helper,
  accept,
  uploaded,
  uploadedLabel,
  emptyLabel,
  onFile,
}: {
  label: string
  helper?: string
  accept: string
  uploaded: boolean
  uploadedLabel: string
  emptyLabel: string
  onFile: (file?: File | null) => void
}) {
  return (
    <div className="flex h-full flex-col rounded-md border bg-gray-50/50 p-4">
      <span className="text-sm font-medium text-gray-700">{label}</span>
      {helper && <p className="mt-1 text-xs text-gray-500">{helper}</p>}
      <div className="mt-auto flex items-center gap-3 pt-3">
        <label className="inline-flex cursor-pointer items-center gap-2 rounded-md border bg-white px-3 py-2 text-sm hover:bg-gray-50">
          <Upload className="h-4 w-4" />
          Upload
          <input
            type="file"
            accept={accept}
            className="hidden"
            onChange={(e) => onFile(e.target.files?.[0])}
          />
        </label>
        {uploaded ? (
          <span className="inline-flex items-center gap-1 text-sm text-green-600">
            <CheckCircle2 className="h-4 w-4" /> {uploadedLabel}
          </span>
        ) : (
          <span className="text-sm text-gray-400">{emptyLabel}</span>
        )}
      </div>
    </div>
  )
}

export default function LauncherPage() {
  const { data: config, isLoading, error } = useLauncherConfig()
  const { data: templates } = useLauncherTemplates()
  const saveConfig = useSaveLauncherConfig()
  const uploadAsset = useUploadLauncherAsset()
  const startBuild = useStartLauncherBuild()

  // Propagation check: opt-in (probes every stack live) so it only runs when the admin asks.
  const [propagationOpen, setPropagationOpen] = useState(false)
  const {
    data: propagation,
    isFetching: propagationFetching,
    refetch: refetchPropagation,
  } = useLauncherStackVersions(propagationOpen)
  const resendLauncher = useResendLauncherToStack()

  const [form, setForm] = useState<LauncherDistributionConfigDto | null>(null)
  const [saved, setSaved] = useState(false)
  const [formError, setFormError] = useState<string | null>(null)

  // Which version segment the next build bumps (Release.Update.Minor.Patch).
  const [bumpPart, setBumpPart] = useState<LauncherVersionPart>('Patch')

  // Live preview of the selected global theme (accent + shipped wallpaper/logo/icon). Stacks can
  // override just the wallpaper/logo on their own Launcher tab.
  const themePreview = useMemo(() => {
    const theme = templates?.find((t) => t.id === form?.template)
    return {
      title: form?.brandingTitle || 'Azeroth Platform Launcher',
      accent: theme?.accentColor || '#4FA8D8',
      backgroundUrl: theme?.backgroundUrl || null,
      logoUrl: theme?.logoUrl || null,
      iconUrl: theme?.iconUrl || null,
    }
  }, [templates, form?.template, form?.brandingTitle])

  // Poll build status while a build is running.
  const [polling, setPolling] = useState(false)
  const { data: status } = useLauncherBuildStatus(polling)
  const logRef = useRef<HTMLDivElement | null>(null)

  useEffect(() => {
    if (config && !form) {
      setForm(config)
    }
  }, [config, form])

  useEffect(() => {
    if (status && !status.isBuilding) {
      setPolling(false)
    }
  }, [status])

  useEffect(() => {
    if (logRef.current) {
      logRef.current.scrollTop = logRef.current.scrollHeight
    }
  }, [status?.log])

  const update = (patch: Partial<LauncherDistributionConfigDto>) =>
    setForm((prev) => (prev ? { ...prev, ...patch } : prev))

  const onSave = async () => {
    if (!form) return
    setFormError(null)
    try {
      await saveConfig.mutateAsync(form)
      setSaved(true)
      setTimeout(() => setSaved(false), 2000)
    } catch (err) {
      setFormError(errorMessage(err))
    }
  }

  const onUpload = async (kind: 'background' | 'logo' | 'icon', file?: File | null) => {
    if (!file) return
    setFormError(null)
    try {
      const updated = await uploadAsset.mutateAsync({ kind, file })
      setForm((prev) => (prev ? { ...prev, ...updated } : updated))
    } catch (err) {
      setFormError(errorMessage(err))
    }
  }

  const onBuild = async () => {
    setFormError(null)
    try {
      await startBuild.mutateAsync({ part: bumpPart })
      setPolling(true)
    } catch (err) {
      setFormError(errorMessage(err))
    }
  }

  if (isLoading || !form) {
    return (
      <div className="flex items-center justify-center py-20 text-gray-500">
        <Loader2 className="h-6 w-6 animate-spin" />
      </div>
    )
  }

  if (error) {
    return <div className="rounded-md bg-red-50 p-4 text-red-700">{errorMessage(error)}</div>
  }

  const building = status?.isBuilding || startBuild.isPending
  const phase = status?.phase ?? 'Idle'

  return (
    <div className="mx-auto max-w-4xl space-y-8">
      <div>
        <h1 className="text-2xl font-bold text-gray-900">Launcher</h1>
        <p className="mt-1 text-gray-600">
          Configure the desktop launcher's identity and branding, then compile a distributable
          Windows executable your players can download. New stacks appear automatically as profiles.
        </p>
      </div>

      {formError && (
        <div className="rounded-md bg-red-50 p-3 text-sm text-red-700">{formError}</div>
      )}

      {/* Identity */}
      <section className="rounded-lg border bg-white p-6 shadow-sm">
        <h2 className="mb-4 text-lg font-semibold text-gray-900">Identity</h2>
        <div className="grid grid-cols-1 gap-4 md:grid-cols-2">
          <label className="block">
            <span className="text-sm font-medium text-gray-700">App / install name</span>
            <input
              className="mt-1 w-full rounded-md border px-3 py-2"
              value={form.appName}
              onChange={(e) => update({ appName: e.target.value })}
              placeholder="Azeroth Platform"
            />
            <span className="mt-1 block text-xs text-gray-500">
              Installs to C:/Program Files/{form.appName || '{AppName}'}
            </span>
          </label>
          <label className="block">
            <span className="text-sm font-medium text-gray-700">Branding title</span>
            <input
              className="mt-1 w-full rounded-md border px-3 py-2"
              value={form.brandingTitle}
              onChange={(e) => update({ brandingTitle: e.target.value })}
            />
          </label>
          <label className="block">
            <span className="text-sm font-medium text-gray-700">Launcher version</span>
            <input
              className="mt-1 w-full cursor-not-allowed rounded-md border bg-gray-100 px-3 py-2 text-gray-600"
              value={status?.availableVersion ?? 'Not built yet'}
              readOnly
              disabled
            />
            <span className="mt-1 block text-xs text-gray-500">
              Release.Update.Minor.Patch of the compiled launcher. Set automatically each build.
            </span>
          </label>
          <label className="block">
            <span className="text-sm font-medium text-gray-700">Game executable</span>
            <input
              className="mt-1 w-full rounded-md border px-3 py-2"
              value={form.gameExecutable}
              onChange={(e) => update({ gameExecutable: e.target.value })}
            />
          </label>
          <label className="block">
            <span className="text-sm font-medium text-gray-700">Launch arguments</span>
            <input
              className="mt-1 w-full rounded-md border px-3 py-2"
              value={form.launchArguments}
              onChange={(e) => update({ launchArguments: e.target.value })}
            />
          </label>
        </div>

        <div className="mt-4 flex items-start gap-3 rounded-md border bg-gray-50/50 p-4">
          <input
            id="require-login"
            type="checkbox"
            className="mt-1 h-4 w-4"
            checked={form.requireLogin}
            onChange={(e) => update({ requireLogin: e.target.checked })}
          />
          <label htmlFor="require-login" className="block">
            <span className="text-sm font-medium text-gray-700">Require players to log in</span>
            <span className="mt-1 block text-xs text-gray-500">
              Shows a login screen in the launcher and blocks download/play until the player signs in
              with a game account (verified against the selected server's database). The login screen
              includes a “Create an account” button that opens the armory registration page. This is
              baked into the launcher, so changing it takes effect only after you build a new launcher
              below.
            </span>
          </label>
        </div>
      </section>

      {/* App icon — the one setting baked into the single compiled exe (can't vary per stack). */}
      <section className="rounded-lg border bg-white p-6 shadow-sm">
        <h2 className="mb-1 text-lg font-semibold text-gray-900">App icon</h2>
        <p className="mb-4 text-sm text-gray-500">
          The launcher's Windows exe / taskbar icon. It is baked into the single compiled executable,
          so it's set here once for every server. Each stack can override its own wallpaper and logo on
          its <strong>Launcher</strong> tab.
        </p>
        <div className="max-w-sm">
          <AssetUploader
            label="App icon"
            helper=".ico or any image (converted to .ico automatically)."
            accept=".ico,image/png,image/jpeg,image/webp,image/gif,image/bmp,image/x-icon,image/vnd.microsoft.icon"
            uploaded={form.hasIcon}
            uploadedLabel="Uploaded"
            emptyLabel="None (default icon)"
            onFile={(file) => onUpload('icon', file)}
          />
        </div>
      </section>

      {/* Theme — a single global style (accent + default wallpaper/logo) applied to every server in
          the launcher. Each stack can override just its own wallpaper/logo on its Launcher tab. */}
      <section className="rounded-lg border bg-white p-6 shadow-sm">
        <h2 className="mb-1 text-lg font-semibold text-gray-900">Theme</h2>
        <p className="mb-4 text-sm text-gray-500">
          Pick the launcher&rsquo;s style. The theme sets the accent color and the default wallpaper and
          logo used for every server. Individual stacks can override their own wallpaper and logo on
          their <strong>Launcher</strong> tab.
        </p>
        <label className="block">
          <span className="text-sm font-medium text-gray-700">Style</span>
          <select
            className="mt-1 w-full rounded-md border px-3 py-2 md:max-w-xs"
            value={form.template}
            onChange={(e) => update({ template: e.target.value })}
          >
            {templates?.map((t) => (
              <option key={t.id} value={t.id}>
                {t.name}
              </option>
            ))}
          </select>
        </label>
        {templates?.find((t) => t.id === form.template)?.description && (
          <p className="mt-2 text-xs text-gray-500">
            {templates.find((t) => t.id === form.template)?.description}
          </p>
        )}
        <div className="mt-4">
          <div className="mb-2 text-sm font-medium text-gray-700">Preview</div>
          <LauncherPreview
            title={themePreview.title}
            accent={themePreview.accent}
            backgroundUrl={themePreview.backgroundUrl}
            logoUrl={themePreview.logoUrl}
            iconUrl={themePreview.iconUrl}
          />
        </div>
      </section>

      <div className="flex items-center gap-3">
        <button
          onClick={onSave}
          disabled={saveConfig.isPending}
          className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-white hover:bg-blue-700 disabled:opacity-50"
        >
          {saveConfig.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
          Save configuration
        </button>
        {saved && (
          <span className="inline-flex items-center gap-1 text-sm text-green-600">
            <CheckCircle2 className="h-4 w-4" /> Saved
          </span>
        )}
      </div>

      {/* Build + download */}
      <section className="rounded-lg border bg-white p-6 shadow-sm">
        <h2 className="mb-1 text-lg font-semibold text-gray-900">Build &amp; distribute</h2>
        <p className="mb-4 text-sm text-gray-500">
          The launcher is compiled once here (on the manager) with your global defaults baked in, then
          pushed to <strong>every</strong> launcher-visible stack&rsquo;s own client container — just like
          global news. Each stack then serves the download and self-update itself, and its own
          branding/realmlist/style overrides are applied on top at runtime.
        </p>
        <div className="flex flex-wrap items-center gap-3">
          <label className="inline-flex items-center gap-2 text-sm text-gray-700">
            <span className="font-medium">Version increment</span>
            <select
              className="rounded-md border px-3 py-2"
              value={bumpPart}
              onChange={(e) => setBumpPart(e.target.value as LauncherVersionPart)}
              disabled={building}
            >
              <option value="Release">Release (x.0.0.0)</option>
              <option value="Update">Update (·.x.0.0)</option>
              <option value="Minor">Minor (·.·.x.0)</option>
              <option value="Patch">Patch (·.·.·.x)</option>
            </select>
          </label>

          <button
            onClick={onBuild}
            disabled={building}
            className="inline-flex items-center gap-2 rounded-md bg-purple-600 px-4 py-2 text-white hover:bg-purple-700 disabled:opacity-50"
          >
            {building ? <Loader2 className="h-4 w-4 animate-spin" /> : <Hammer className="h-4 w-4" />}
            {building ? 'Building…' : 'Build launcher'}
          </button>

          <a
            href={launcherApi.downloadUrl()}
            className={`inline-flex items-center gap-2 rounded-md border px-4 py-2 ${
              status?.downloadAvailable
                ? 'text-gray-800 hover:bg-gray-50'
                : 'pointer-events-none text-gray-300'
            }`}
          >
            <Download className="h-4 w-4" />
            Download exe
          </a>

          {status?.availableVersion && (
            <span className="text-sm text-gray-500">
              v{status.availableVersion} · {formatBytes(status.availableSizeBytes)}
            </span>
          )}
        </div>

        <div className="mt-4 flex items-center gap-2 text-sm">
          {phase === 'Failed' ? (
            <XCircle className="h-4 w-4 text-red-600" />
          ) : phase === 'Completed' ? (
            <CheckCircle2 className="h-4 w-4 text-green-600" />
          ) : building ? (
            <Loader2 className="h-4 w-4 animate-spin text-purple-600" />
          ) : null}
          <span className="text-gray-700">{status?.message ?? 'Idle'}</span>
        </div>

        {status?.error && (
          <div className="mt-2 rounded-md bg-red-50 p-2 text-sm text-red-700">{status.error}</div>
        )}

        {status && status.log.length > 0 && (
          <div
            ref={logRef}
            className="mt-4 h-56 overflow-auto rounded-md bg-gray-900 p-3 font-mono text-xs text-gray-100"
          >
            {status.log.map((line, i) => (
              <div key={i} className="whitespace-pre-wrap">
                {line}
              </div>
            ))}
          </div>
        )}
      </section>

      {/* Propagation check — verify the built launcher actually reached every stack, and re-send to any
          stack that is stale or was offline during the build. */}
      <section className="rounded-lg border bg-white p-6 shadow-sm">
        <h2 className="mb-1 text-lg font-semibold text-gray-900">Propagation</h2>
        <p className="mb-4 text-sm text-gray-500">
          Ping every stack for the launcher version it currently serves and compare it against the last
          build{status?.availableVersion ? ` (v${status.availableVersion})` : ''}. Re-send to any stack
          that is out of date or was offline when the launcher was built.
        </p>

        <div className="flex flex-wrap items-center gap-3">
          <button
            onClick={() => {
              if (!propagationOpen) setPropagationOpen(true)
              else refetchPropagation()
            }}
            disabled={propagationFetching}
            className="inline-flex items-center gap-2 rounded-md border px-4 py-2 text-gray-800 hover:bg-gray-50 disabled:opacity-50"
          >
            {propagationFetching ? (
              <Loader2 className="h-4 w-4 animate-spin" />
            ) : (
              <RefreshCw className="h-4 w-4" />
            )}
            {propagationFetching ? 'Checking stacks…' : 'Check stacks'}
          </button>
        </div>

        {propagationOpen && propagation && (
          <div className="mt-4 divide-y rounded-md border">
            {propagation.stacks.length === 0 && (
              <div className="p-4 text-sm text-gray-500">No client-enabled stacks found.</div>
            )}
            {propagation.stacks.map((s) => {
              const resending = resendLauncher.isPending && resendLauncher.variables === s.stackId
              return (
                <div key={s.stackId} className="flex items-center justify-between gap-3 p-3">
                  <div className="min-w-0">
                    <div className="flex items-center gap-2">
                      <span className="truncate font-medium text-gray-800">{s.stackName}</span>
                      {!s.launcherVisible && (
                        <span className="rounded bg-gray-100 px-1.5 py-0.5 text-[11px] text-gray-500">
                          hidden profile
                        </span>
                      )}
                    </div>
                    <div className="text-xs text-gray-500">
                      {s.statusDetail
                        ?? (s.reachable
                          ? `Serving v${s.deployedVersion}`
                          : 'Client container offline or launcher not deployed yet.')}
                    </div>
                  </div>

                  <div className="flex items-center gap-3">
                    {!s.reachable ? (
                      <span className="inline-flex items-center gap-1 text-sm text-amber-700">
                        <AlertTriangle className="h-4 w-4" /> Not available
                      </span>
                    ) : s.upToDate ? (
                      <span className="inline-flex items-center gap-1 text-sm text-green-600">
                        <CheckCircle2 className="h-4 w-4" /> Up to date
                      </span>
                    ) : (
                      <span className="inline-flex items-center gap-1 text-sm text-amber-600">
                        <AlertTriangle className="h-4 w-4" /> Out of date
                      </span>
                    )}

                    {!s.upToDate && (status?.downloadAvailable ?? false) && (
                      <button
                        onClick={() => resendLauncher.mutate(s.stackId)}
                        disabled={resending}
                        className="inline-flex items-center gap-2 rounded-md bg-purple-600 px-3 py-1.5 text-sm text-white hover:bg-purple-700 disabled:opacity-50"
                        title="Re-push the current launcher build to this stack"
                      >
                        {resending ? (
                          <Loader2 className="h-4 w-4 animate-spin" />
                        ) : (
                          <Send className="h-4 w-4" />
                        )}
                        Re-send
                      </button>
                    )}
                  </div>
                </div>
              )
            })}
          </div>
        )}

        {resendLauncher.isError && (
          <div className="mt-3 rounded-md bg-red-50 p-2 text-sm text-red-700">
            {errorMessage(resendLauncher.error)}
          </div>
        )}
      </section>
    </div>
  )
}
