import { useEffect, useRef, useState } from 'react'
import { Loader2, Upload } from 'lucide-react'
import ClientJobProgress from '@/components/client/ClientJobProgress'
import DownloadClientUrlDialog from '@/components/client/DownloadClientUrlDialog'
import { useClientBaseInfo, useDownloadBaseClient, useUploadBaseClient } from '@/hooks/useClient'
import { useClientJobContext } from '@/contexts/ClientJobContext'
import { useExpressProvision } from '@/hooks/useExpressProvision'
import { apiErrorMessage } from '@/lib/utils'
import { WipBadge } from '@/components/common/WipBadge'
import { setupActionButton } from '@/setup/ui'
import type { SetupStep, SetupStepContext } from '@/setup/types'
import { ServerType } from '@/types/stack.types'
import {
  EXPRESS_PHASE_TITLE,
  EXPRESS_PIPELINE_PHASES,
  expressCurrentPhaseIndex,
  expressPhaseLabel,
  expressPhaseStepId,
  expressPipelineStepApplies,
  isExpressProvisionActive,
  type ExpressPipelinePhase,
} from '@/setup/steps/express/expressPhases'

const ACCEPTED_EXTENSIONS = ['.zip', '.rar', '.7z', '.tar', '.tar.gz', '.tgz', '.tar.bz2', '.tbz2', '.tar.xz']
const ACCEPT_ATTR = ACCEPTED_EXTENSIONS.join(',')

function hasAcceptedExtension(name: string): boolean {
  const lower = name.toLowerCase()
  return ACCEPTED_EXTENSIONS.some((ext) => lower.endsWith(ext))
}

function isExpress(ctx: SetupStepContext) {
  return ctx.stack.configuration.serverType === ServerType.Express
}

function AdminAccountNotice() {
  return (
    <p className="rounded-md border border-green-200 bg-green-50 px-3 py-2 text-sm text-green-900">
      Game account created: username <strong>admin</strong>, password <strong>admin</strong> (GM level 3 on
      all realms). The same notice is on the Accounts tab.
    </p>
  )
}

function ClientWaitPanel({ stackId }: { stackId: string }) {
  const { data: info, refetch, isLoading, isFetching } = useClientBaseInfo(stackId)
  const uploadBase = useUploadBaseClient(stackId)
  const downloadBase = useDownloadBaseClient(stackId)
  const { job: clientJob, isClientBusy, applyStatus: applyClientStatus } = useClientJobContext()
  const downloading = isClientBusy && clientJob?.action === 'DownloadBase'
  const installing = isClientBusy && clientJob?.action === 'InstallBase'
  const [showUrlDialog, setShowUrlDialog] = useState(false)
  const [uploadPercent, setUploadPercent] = useState<number | null>(null)
  const [error, setError] = useState<string | null>(null)
  const fileRef = useRef<HTMLInputElement | null>(null)
  const uploading = uploadBase.isPending || (uploadPercent !== null && uploadPercent < 100)
  const busy = uploading || installing || downloading
  const uploaded = info?.exists === true
  const ready = uploaded && !busy
  const { continueAfterClient: continueMutation } = useExpressProvision(stackId)

  useEffect(() => {
    if (!ready) {
      const timer = window.setInterval(() => {
        void refetch()
      }, 3000)
      return () => window.clearInterval(timer)
    }
  }, [ready, refetch])

  useEffect(() => {
    if ((clientJob?.action === 'DownloadBase' || clientJob?.action === 'InstallBase') && clientJob.success) {
      void refetch()
    }
    if ((clientJob?.action === 'DownloadBase' || clientJob?.action === 'InstallBase') && clientJob.error) {
      setError(clientJob.error)
    }
  }, [clientJob, refetch])

  const onFile = async (file?: File | null) => {
    if (!file) return
    setError(null)
    if (!hasAcceptedExtension(file.name)) {
      setError(`Unsupported file type. Accepted archives: ${ACCEPTED_EXTENSIONS.join(', ')}.`)
      return
    }
    setUploadPercent(0)
    try {
      const job = await uploadBase.mutateAsync({ file, onProgress: setUploadPercent })
      applyClientStatus(job)
    } catch (err) {
      setError(apiErrorMessage(err, 'Upload failed.'))
    } finally {
      setUploadPercent(null)
      if (fileRef.current) fileRef.current.value = ''
    }
  }

  return (
    <div className="space-y-3 text-sm text-gray-700">
      <AdminAccountNotice />
      <p>
        The launcher can no longer fetch a client automatically. Upload a 3.3.5a archive or paste a direct
        download link, then continue.
      </p>
      {isLoading && !uploaded && (
        <p className="text-gray-600">Checking whether a client is already uploaded…</p>
      )}
      {(installing || downloading) && (
        <ClientJobProgress
          message={
            clientJob?.message
            || (installing
              ? 'Extracting and copying client files into the volume…'
              : 'Downloading the base client…')
          }
          bytesCompleted={clientJob?.bytesCompleted}
          bytesTotal={clientJob?.bytesTotal}
        />
      )}
      {ready && (
        <p className="font-medium text-green-700">
          Client is uploaded, you can now continue.
          {info.hasWowExe ? ' (Wow.exe found)' : isFetching ? ' (refreshing file list…)' : ''}
        </p>
      )}
      {error && <p className="text-red-700">{error}</p>}
      {continueMutation.isError && (
        <p className="text-red-700">{apiErrorMessage(continueMutation.error, 'Could not continue Express Setup.')}</p>
      )}
      <input
        ref={fileRef}
        type="file"
        accept={ACCEPT_ATTR}
        className="hidden"
        onChange={(event) => void onFile(event.target.files?.[0])}
      />
      <div className="flex flex-wrap items-center gap-2">
        <button
          type="button"
          disabled={busy}
          onClick={() => fileRef.current?.click()}
          className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
        >
          {busy ? <Loader2 className="h-4 w-4 animate-spin" /> : <Upload className="h-4 w-4" />}
          {installing
            ? 'Installing…'
            : uploading
              ? `Uploading ${uploadPercent}%`
              : downloading
                ? 'Downloading…'
                : 'Upload client'}
        </button>
        <button
          type="button"
          disabled={busy}
          onClick={() => setShowUrlDialog(true)}
          className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
        >
          Download from URL
        </button>
        <button
          type="button"
          disabled={!ready || continueMutation.isPending}
          onClick={() => continueMutation.mutate()}
          className="rounded-md bg-green-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-green-700 disabled:opacity-50"
        >
          {continueMutation.isPending ? 'Continuing…' : 'Continue'}
        </button>
      </div>
      {uploading && (
        <div className="h-2 w-full overflow-hidden rounded bg-gray-200">
          <div className="h-full bg-blue-600 transition-all" style={{ width: `${uploadPercent}%` }} />
        </div>
      )}
      {showUrlDialog && (
        <DownloadClientUrlDialog
          onClose={() => setShowUrlDialog(false)}
          onSubmit={async (url) => {
            const job = await downloadBase.mutateAsync(url)
            applyClientStatus(job)
          }}
        />
      )}
    </div>
  )
}

function FailedRetryAction({ stackId, phase }: { stackId: string; phase: ExpressPipelinePhase }) {
  const { retry: retryMutation } = useExpressProvision(stackId)

  return (
    <div className="flex flex-col items-end gap-1">
      {retryMutation.isError && (
        <p className="text-xs text-red-700">{apiErrorMessage(retryMutation.error, 'Could not retry.')}</p>
      )}
      {setupActionButton(`Retry from ${expressPhaseLabel(phase)}`, () => retryMutation.mutate(), {
        pending: retryMutation.isPending,
        tone: 'red',
      })}
    </div>
  )
}

function pipelineStep(phase: ExpressPipelinePhase, index: number): SetupStep {
  const title = EXPRESS_PHASE_TITLE[phase]

  const isCurrent = (ctx: SetupStepContext) =>
    isExpressProvisionActive(ctx.stack.expressProvisionStatus)
    && expressCurrentPhaseIndex(ctx.stack.expressProvisionStatus, ctx.stack.expressProvisionPhase) === index

  const isDone = (ctx: SetupStepContext) =>
    expressCurrentPhaseIndex(ctx.stack.expressProvisionStatus, ctx.stack.expressProvisionPhase) > index

  return {
    id: expressPhaseStepId(phase),
    sequenced: false,
    defaultExpanded: (ctx) => isCurrent(ctx),
    compactWhenComplete: true,
    title,
    titleEnd: phase === 'SwpSync' ? <WipBadge /> : undefined,
    applies: (ctx) =>
      isExpress(ctx)
      && expressPipelineStepApplies(
        phase,
        ctx.stack.expressProvisionStatus,
        ctx.stack.expressProvisionPhase,
      ),
    progressApplies: (ctx) => isExpress(ctx) && isExpressProvisionActive(ctx.stack.expressProvisionStatus),
    isComplete: (ctx) => isDone(ctx),
    showWhenComplete: () => true,
    level: (ctx) => {
      if (isDone(ctx)) return 'success'
      if (ctx.stack.expressProvisionStatus === 'Failed' && isCurrent(ctx)) return 'error'
      if (phase === 'WaitClient' && isCurrent(ctx)) return 'warning'
      return 'loading'
    },
    summary: (ctx) => {
      if (isDone(ctx)) return 'Done'
      if (isCurrent(ctx)) return ctx.stack.expressProvisionMessage || 'Working…'
      return title
    },
    Component: (ctx) => {
      if (!isCurrent(ctx)) return null
      if (phase === 'WaitClient') {
        return <ClientWaitPanel stackId={ctx.stack.stackId} />
      }
      if (ctx.stack.expressProvisionStatus === 'Failed') {
        return (
          <p className="text-sm text-red-700">
            {ctx.stack.expressProvisionMessage || `Failed during ${title}.`}
          </p>
        )
      }
      if (phase === 'StartStack') {
        return (
          <div className="space-y-2 text-sm text-gray-700">
            <p>{ctx.stack.expressProvisionMessage || 'Starting the stack…'}</p>
            <p className="text-gray-600">
              This first start can take a while. Don't worry - grab a snack.
            </p>
            <p className="text-gray-600">
              MySQL comes up, database import runs, then auth and world. If you selected an Ollama
              module, the image and model download in the background and can take several minutes.
            </p>
          </div>
        )
      }
      if (phase === 'GameAccount') {
        return (
          <p className="text-sm text-gray-700">
            Creating game account admin / admin and setting GM level 3 on all realms. This is separate from
            the SOAP manager account.
          </p>
        )
      }
      return (
        <p className="text-sm text-gray-700">{ctx.stack.expressProvisionMessage || 'Working…'}</p>
      )
    },
    Action: (ctx) => {
      if (!isCurrent(ctx)) return null
      if (ctx.stack.expressProvisionStatus === 'Failed') {
        return <FailedRetryAction stackId={ctx.stack.stackId} phase={phase} />
      }
      if (ctx.stack.expressProvisionStatus === 'Running') {
        return (
          <span className="inline-flex items-center gap-2 text-sm text-blue-700">
            <Loader2 className="h-4 w-4 animate-spin" />
            Working
          </span>
        )
      }
      return null
    },
  }
}

const EXPRESS_PIPELINE_STEP_LIST: SetupStep[] = EXPRESS_PIPELINE_PHASES.map((phase, index) =>
  pipelineStep(phase, index),
)

export function expressPipelineSteps(): SetupStep[] {
  return EXPRESS_PIPELINE_STEP_LIST
}
