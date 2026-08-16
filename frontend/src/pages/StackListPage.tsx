import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Play, Square, RefreshCw, Plus, Loader2, Trash2, Hammer, AlertCircle, Download, Database, HardDrive, Cloud, PowerOff, Settings } from 'lucide-react'
import { Link, useNavigate } from 'react-router-dom'
import { useEffect, useRef, useState } from 'react'
import DeleteStackDialog from '@/components/DeleteStackDialog'
import { ImportStacksDialog } from '@/components/ImportStacksDialog'
import { DiskUsageBar, formatBytes } from '@/components/docker/DockerDiskUsage'
import StackListSkeleton from '@/components/stacks/StackListSkeleton'
import StackRefreshBar from '@/components/stacks/StackRefreshBar'
import { useDockerDiskUsage } from '@/hooks/useStackDocker'
import { useStackLifecycleJobs } from '@/hooks/useStackLifecycleJobs'
import { useStacks, stackKeys } from '@/hooks/useStacks'
import { isStackJobRunning } from '@/lib/stackJob'
import { getVpcListHostAlert, apiErrorMessage } from '@/lib/utils'
import { stackApi, buildApi } from '@/services/api'
import type { StackDetailsDto } from '@/types/stack.types'
import { CloudProvider, DeploymentTarget, StackStatus } from '@/types/stack.types'
import { providerDisplayName } from '@/lib/cloud-auth'

// Calculate stack uptime from containers
function calculateStackUptime(stack: StackDetailsDto): string | null {
  const runningContainers = stack.containers.filter(c => 
    c.status.toLowerCase().includes('running') || c.status.toLowerCase().includes('up')
  )
  
  if (runningContainers.length === 0) return null
  
  // Find the earliest start time
  const earliestStart = runningContainers.reduce((earliest, container) => {
    const startTime = new Date(container.startedAt).getTime()
    return startTime < earliest ? startTime : earliest
  }, new Date(runningContainers[0].startedAt).getTime())
  
  const uptimeMs = Date.now() - earliestStart
  const uptimeMinutes = Math.floor(uptimeMs / 60000)
  const uptimeHours = Math.floor(uptimeMinutes / 60)
  const remainingMinutes = uptimeMinutes % 60
  
  if (uptimeHours > 0) {
    return `${uptimeHours}h ${remainingMinutes}m`
  } else {
    return `${uptimeMinutes}m`
  }
}

function getStatusBadgeColor(status: StackStatus): string {
  switch (status) {
    case StackStatus.Running:
      return 'bg-green-100 text-green-800'
    case StackStatus.Initializing:
      return 'bg-blue-100 text-blue-800'
    case StackStatus.Starting:
      return 'bg-yellow-100 text-yellow-800'
    case StackStatus.Degraded:
      return 'bg-orange-100 text-orange-800'
    case StackStatus.Stopped:
      return 'bg-gray-100 text-gray-800'
    case StackStatus.Building:
      return 'bg-blue-100 text-blue-800'
    case StackStatus.Failed:
      return 'bg-red-100 text-red-800'
    case StackStatus.SetupIncomplete:
      return 'bg-amber-100 text-amber-900'
    default:
      return 'bg-gray-100 text-gray-800'
  }
}

function formatStackStatus(status: StackStatus): string {
  return status === StackStatus.SetupIncomplete ? 'Setup incomplete' : status
}

function stackListName(stack: StackDetailsDto): string {
  const display = stack.displayName?.trim()
  if (display) {
    return display
  }

  const name = stack.stackName?.trim() ?? ''
  if (
    stack.status === StackStatus.SetupIncomplete
    && (!name || name.toLowerCase().startsWith('unnamed-instance') || /^vpc-[a-f0-9]{8}$/i.test(name))
  ) {
    return 'Unnamed instance'
  }

  return stack.stackName
}

function isCloudProvider(value: string): value is CloudProvider {
  return Object.values(CloudProvider).includes(value as CloudProvider)
}

function formatCloudProviderTag(provider?: string): string | null {
  const value = provider?.trim()
  if (!value) {
    return null
  }

  return isCloudProvider(value) ? providerDisplayName(value) : value
}

export default function StackListPage() {
  const navigate = useNavigate()
  const { data: stacks = [], isPending, isFetching, isPlaceholderData, probeAll, isProbing } = useStacks()
  const { data: diskUsage } = useDockerDiskUsage()
  const queryClient = useQueryClient()
  const [deletingStack, setDeletingStack] = useState<{
    id: string
    name: string
    isExternal: boolean
  } | null>(null)
  const [importDialogOpen, setImportDialogOpen] = useState(false)
  const { trackJob, isStackBusy, jobs: lifecycleJobs } = useStackLifecycleJobs()
  const lastHandledLifecycleJobRef = useRef<Record<string, string>>({})

  useEffect(() => {
    for (const [stackId, job] of Object.entries(lifecycleJobs)) {
      if (!job || isStackJobRunning(job)) continue
      if (lastHandledLifecycleJobRef.current[stackId] === job.jobId) continue
      lastHandledLifecycleJobRef.current[stackId] = job.jobId
      void probeAll.mutate()
    }
  }, [lifecycleJobs, probeAll])

  useEffect(() => {
    if (stacks.length === 0) return

    let cancelled = false
    void (async () => {
      for (const stack of stacks) {
        if (stack.status === StackStatus.SetupIncomplete) continue
        try {
          const res = await stackApi.jobStatus(stack.stackId)
          if (!cancelled && res.data && isStackJobRunning(res.data)) {
            trackJob(stack.stackId, res.data)
          }
        } catch {
          // Ignore — list view still works without reattached jobs.
        }
      }
    })()

    return () => {
      cancelled = true
    }
  }, [stacks, trackJob])

  const startStack = useMutation({
    mutationFn: (stackId: string) => stackApi.start(stackId),
    onSuccess: (res, stackId) => {
      trackJob(stackId, res.data)
    },
  })

  const startDatabase = useMutation({
    mutationFn: (stackId: string) => stackApi.startDatabase(stackId),
    onSuccess: (res, stackId) => {
      trackJob(stackId, res.data)
    },
  })

  const stopStack = useMutation({
    mutationFn: (stackId: string) => stackApi.stop(stackId),
    onSuccess: (res, stackId) => {
      trackJob(stackId, res.data)
    },
  })

  const restartStack = useMutation({
    mutationFn: (stackId: string) => stackApi.restart(stackId),
    onSuccess: (res, stackId) => {
      trackJob(stackId, res.data)
    },
  })

  const deleteStack = useMutation({
    mutationFn: ({
      stackId,
      terminateCloudInstance,
    }: {
      stackId: string
      terminateCloudInstance: boolean
    }) => stackApi.delete(stackId, { terminateCloudInstance }),
    onSuccess: (_data, variables) => {
      setDeletingStack(null)
      queryClient.setQueryData<StackDetailsDto[]>(stackKeys.lists(), (current) =>
        current?.filter((stack) => stack.stackId !== variables.stackId),
      )
      void probeAll.mutate()
    },
  })

  const rebuildStack = useMutation({
    mutationFn: (stackId: string) => buildApi.start(stackId), // No config = rebuild with existing config
    onSuccess: (_data, stackId) => {
      queryClient.invalidateQueries({ queryKey: ['stacks'] })
      // Navigate to build progress page
      navigate(`/stacks/${stackId}/build`)
    },
  })

  const showInitialSkeleton = isPending && stacks.length === 0

  return (
    <div>
      <StackRefreshBar
        active={(isFetching || isProbing) && !showInitialSkeleton}
        className="mb-4"
        label={
          isProbing
            ? 'Checking live status for all stacks…'
            : isPlaceholderData
              ? 'Loading cached stack list…'
              : 'Refreshing stacks…'
        }
      />

      <div className="mb-6 flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between">
        <div>
          <h1 className="text-3xl font-bold text-gray-900">Your Stacks</h1>
          <p className="mt-1 text-gray-500">
            Manage your AzerothCore server instances. Live status is checked once per session and
            cached until you refresh or open a stack.
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2 shrink-0">
          <button
            type="button"
            onClick={() => probeAll.mutate()}
            disabled={isProbing || stacks.length === 0}
            className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-2.5 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
          >
            {isProbing ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : <RefreshCw className="h-3.5 w-3.5" />}
            Refresh all
          </button>
          <button
            onClick={() => setImportDialogOpen(true)}
            className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-2.5 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50"
          >
            <Download className="h-3.5 w-3.5" />
            <span className="hidden sm:inline">Import Existing Stacks</span>
            <span className="sm:hidden">Import</span>
          </button>
          <Link
            to="/stacks/new"
            className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-2.5 py-1.5 text-xs font-medium text-white hover:bg-blue-700"
          >
            <Plus className="h-3.5 w-3.5" />
            Create Stack
          </Link>
        </div>
      </div>

      {diskUsage?.isWarning && (
        <div className="mb-6 rounded-lg border border-amber-300 bg-amber-50 p-4 shadow-sm">
          <div className="flex items-start gap-3">
            <HardDrive className="mt-0.5 h-5 w-5 shrink-0 text-amber-700" />
            <div className="flex-1">
              <h2 className="font-semibold text-amber-950">Docker disk space is running low</h2>
              <p className="mt-1 text-sm text-amber-900">
                The Docker engine is {diskUsage.usedPercent.toFixed(1)}% full ({formatBytes(diskUsage.usedBytes)} of{' '}
                {formatBytes(diskUsage.totalBytes)} used). Old build cache and unused images may be consuming space.
                Open the global <Link to="/docker" className="font-medium text-amber-950 underline">Docker</Link> page
                or any stack&apos;s <strong>Advanced → Docker</strong> tab and use <strong>Reclaim disk space</strong>.
              </p>
              <div className="mt-3">
                <DiskUsageBar disk={diskUsage} showDetails={false} />
              </div>
            </div>
          </div>
        </div>
      )}

      {showInitialSkeleton ? (
        <StackListSkeleton count={Math.max(stacks.length, 3)} />
      ) : stacks.length === 0 ? (
        <div className="rounded-lg border-2 border-dashed border-gray-300 p-12 text-center">
          <h3 className="text-lg font-medium text-gray-900">No stacks yet</h3>
          <p className="mt-2 text-gray-500">Get started by creating your first AzerothCore server stack or import an existing one.</p>
          <div className="mt-4 flex flex-wrap items-center justify-center gap-2">
            <button
              onClick={() => setImportDialogOpen(true)}
              className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-2.5 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50"
            >
              <Download className="h-3.5 w-3.5" />
              Import Existing Stacks
            </button>
            <Link
              to="/stacks/new"
              className="inline-flex items-center gap-1.5 rounded-md bg-blue-600 px-2.5 py-1.5 text-xs font-medium text-white hover:bg-blue-700"
            >
              <Plus className="h-3.5 w-3.5" />
              Create Stack
            </Link>
          </div>
        </div>
      ) : (
        <div className={`space-y-4 ${isProbing ? 'opacity-90' : ''}`}>
          {stacks.map((stack) => {
            const isSetupIncomplete = stack.status === StackStatus.SetupIncomplete
            const displayStatus =
              stack.status === StackStatus.Failed ? StackStatus.Stopped : stack.status
            const isRunning = !isSetupIncomplete && displayStatus === StackStatus.Running
            const isStopped = !isSetupIncomplete && displayStatus === StackStatus.Stopped
            const isDegraded = !isSetupIncomplete && displayStatus === StackStatus.Degraded
            const uptime = isSetupIncomplete ? null : calculateStackUptime(stack)
            const isExternal = stack.configuration.deployment?.target === DeploymentTarget.External
            const vpcHostAlert = !isSetupIncomplete && !isProbing ? getVpcListHostAlert(stack) : null
            const vpcHostOffline = vpcHostAlert?.kind === 'offline'
            const providerTag = isSetupIncomplete
              ? formatCloudProviderTag(stack.configuration.deployment?.cloudProvider)
              : null
            const instanceTypeTag = isSetupIncomplete
              ? stack.configuration.deployment?.cloudInstanceType?.trim() || null
              : null
            const stackHref = isSetupIncomplete
              ? `/stacks/new?draft=${encodeURIComponent(stack.stackId)}`
              : `/stacks/${stack.stackId}`

            const lifecycleJob = lifecycleJobs[stack.stackId]
            const lifecycleBusy = isStackBusy(stack.stackId)
            const startActionBusy = lifecycleBusy && lifecycleJob?.action === 'Start'
            const startDbActionBusy = lifecycleBusy && lifecycleJob?.action === 'StartDatabase'
            const stopActionBusy = lifecycleBusy && lifecycleJob?.action === 'Stop'
            const restartActionBusy = lifecycleBusy && lifecycleJob?.action === 'Restart'

            return (
              <div
                key={stack.stackId}
                className={`rounded-lg border bg-white p-4 shadow-sm transition-shadow hover:shadow-md sm:p-5 ${
                  vpcHostOffline
                    ? 'border-amber-300 border-l-4 border-l-amber-500'
                    : 'border-gray-200'
                }`}
              >
                <div className="flex flex-col gap-4 sm:flex-row sm:items-start sm:justify-between">
                  <div className="min-w-0 flex-1">
                    <div className="flex flex-wrap items-center gap-3">
                      <Link
                        to={stackHref}
                        className="text-xl font-semibold text-gray-900 hover:text-blue-600"
                      >
                        {stackListName(stack)}
                      </Link>
                      <span
                        className={`rounded-full px-2.5 py-0.5 text-xs font-medium ${getStatusBadgeColor(displayStatus)}`}
                      >
                        {formatStackStatus(displayStatus)}
                      </span>
                      <span
                        className={`inline-flex items-center gap-1 rounded-full px-2.5 py-0.5 text-xs font-medium ${
                          isExternal ? 'bg-violet-100 text-violet-800' : 'bg-slate-100 text-slate-700'
                        }`}
                        title={isExternal ? 'External VPC stack' : 'Local Docker stack'}
                      >
                        {isExternal ? <Cloud className="h-3 w-3" /> : <HardDrive className="h-3 w-3" />}
                        {isExternal ? 'VPC' : 'Local'}
                      </span>
                      {providerTag && (
                        <span className="inline-flex items-center rounded-full bg-sky-100 px-2.5 py-0.5 text-xs font-medium text-sky-900">
                          {providerTag}
                        </span>
                      )}
                      {instanceTypeTag && (
                        <span
                          className="inline-flex items-center rounded-full bg-slate-100 px-2.5 py-0.5 font-mono text-xs font-medium text-slate-800"
                          title="Cloud instance type"
                        >
                          {instanceTypeTag}
                        </span>
                      )}
                      {vpcHostOffline && (
                        <span
                          className="inline-flex items-center gap-1 rounded-full px-2.5 py-0.5 text-xs font-medium bg-amber-100 text-amber-900"
                          title={vpcHostAlert.message}
                        >
                          <PowerOff className="h-3 w-3" aria-hidden="true" />
                          VPC host offline
                        </span>
                      )}
                      {vpcHostAlert?.kind === 'docker-stopped' && (
                        <span
                          className="inline-flex items-center gap-1 rounded-full px-2.5 py-0.5 text-xs font-medium bg-orange-100 text-orange-900"
                          title={vpcHostAlert.message}
                        >
                          <AlertCircle className="h-3 w-3" aria-hidden="true" />
                          Docker stopped
                        </span>
                      )}
                      {isExternal && isProbing && !isSetupIncomplete && (
                        <span className="inline-flex items-center gap-1 text-xs text-gray-500">
                          <Loader2 className="h-3 w-3 animate-spin" aria-hidden="true" />
                          Checking VPC…
                        </span>
                      )}
                      {stack.updateStatus?.hasUpdates && (
                        <span className="flex items-center gap-1 rounded-full px-2.5 py-0.5 text-xs font-medium bg-amber-100 text-amber-800">
                          <AlertCircle className="h-3 w-3" />
                          Updates Available
                        </span>
                      )}
                      {stack.updateStatus?.isRuntimeConfigOutdated && (
                        <span
                          className="flex items-center gap-1 rounded-full px-2.5 py-0.5 text-xs font-medium bg-orange-100 text-orange-800"
                          title="This stack's generated configuration is out of date. Restart / re-apply it to regenerate .env and docker-compose.override.yml with the current security fixes."
                        >
                          <AlertCircle className="h-3 w-3" />
                          Re-apply required
                        </span>
                      )}
                      {uptime && (
                        <span className="text-xs text-green-600 font-medium">
                          Uptime: {uptime}
                        </span>
                      )}
                    </div>
                    <p className="mt-2 text-sm text-gray-500">
                      Created {new Date(stack.createdAt).toLocaleDateString()}
                      {isExternal && stack.configuration.deployment?.externalHost?.trim() && (
                        <>
                          {' '}
                          · Host{' '}
                          <span className="font-mono text-gray-600">
                            {stack.configuration.deployment.externalHost.trim()}
                          </span>
                        </>
                      )}
                    </p>
                    {vpcHostAlert && (
                      <div
                        className={`mt-3 flex items-start gap-2 rounded-md border px-3 py-2 text-xs ${
                          vpcHostAlert.kind === 'offline'
                            ? 'border-amber-200 bg-amber-50 text-amber-950'
                            : 'border-orange-200 bg-orange-50 text-orange-950'
                        }`}
                        role="status"
                      >
                        {vpcHostAlert.kind === 'offline' ? (
                          <PowerOff className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
                        ) : (
                          <AlertCircle className="mt-0.5 h-3.5 shrink-0" aria-hidden="true" />
                        )}
                        <span>{vpcHostAlert.message}</span>
                      </div>
                    )}
                    {isExternal && isStopped && !vpcHostAlert && !isProbing && stack.dockerEngineAvailable === true && (
                      <p className="mt-2 text-xs text-gray-500">
                        Stack containers are stopped. The VPC host is online — use Start to bring the server back up.
                      </p>
                    )}
                    {isSetupIncomplete ? (
                      <p className="mt-2 text-sm text-gray-500">
                        VPC instance is ready. Finish server setup when you are ready to continue.
                      </p>
                    ) : (
                    <div className="mt-3 flex gap-4 text-sm text-gray-600">
                      <span>Auth: {stack.configuration.ports.authServer}</span>
                      <span>World: {stack.configuration.ports.worldServer}</span>
                      <span>DB: {stack.configuration.database.port}</span>
                    </div>
                    )}
                  </div>

                  <div className="flex flex-wrap justify-end gap-1.5 shrink-0">
                    {isSetupIncomplete && (
                      <Link
                        to={stackHref}
                        className="inline-flex items-center gap-1 rounded-md bg-amber-600 px-2 py-1 text-xs font-medium text-white hover:bg-amber-700"
                      >
                        <Settings className="h-3.5 w-3.5" />
                        Finish setup
                      </Link>
                    )}
                    {!isSetupIncomplete && (isStopped || isDegraded) && (
                      <button
                        onClick={() => startStack.mutate(stack.stackId)}
                        disabled={lifecycleBusy}
                        className="inline-flex items-center gap-1 rounded-md bg-green-600 px-2 py-1 text-xs font-medium text-white hover:bg-green-700 disabled:opacity-50"
                      >
                        {startActionBusy ? (
                          <Loader2 className="h-3.5 w-3.5 animate-spin" />
                        ) : (
                          <Play className="h-3.5 w-3.5" />
                        )}
                        {startActionBusy ? 'Starting…' : 'Start'}
                      </button>
                    )}
                    {isStopped && (
                      <button
                        onClick={() => startDatabase.mutate(stack.stackId)}
                        disabled={lifecycleBusy}
                        title="Start only the database container (for patches/maintenance)"
                        className="inline-flex items-center gap-1 rounded-md border border-green-600 bg-white px-2 py-1 text-xs font-medium text-green-700 hover:bg-green-50 disabled:opacity-50"
                      >
                        {startDbActionBusy ? (
                          <Loader2 className="h-3.5 w-3.5 animate-spin" />
                        ) : (
                          <Database className="h-3.5 w-3.5" />
                        )}
                        {startDbActionBusy ? 'Starting DB…' : 'Start DB'}
                      </button>
                    )}
                    {isRunning && (
                      <button
                        onClick={() => restartStack.mutate(stack.stackId)}
                        disabled={lifecycleBusy}
                        className="inline-flex items-center gap-1 rounded-md bg-yellow-600 px-2 py-1 text-xs font-medium text-white hover:bg-yellow-700 disabled:opacity-50"
                      >
                        {restartActionBusy ? (
                          <Loader2 className="h-3.5 w-3.5 animate-spin" />
                        ) : (
                          <RefreshCw className="h-3.5 w-3.5" />
                        )}
                        {restartActionBusy ? 'Restarting…' : 'Restart'}
                      </button>
                    )}
                    {(isRunning || isDegraded) && (
                      <button
                        onClick={() => stopStack.mutate(stack.stackId)}
                        disabled={lifecycleBusy}
                        className="inline-flex items-center gap-1 rounded-md bg-red-600 px-2 py-1 text-xs font-medium text-white hover:bg-red-700 disabled:opacity-50"
                      >
                        {stopActionBusy ? (
                          <Loader2 className="h-3.5 w-3.5 animate-spin" />
                        ) : (
                          <Square className="h-3.5 w-3.5" />
                        )}
                        {stopActionBusy ? 'Stopping…' : 'Stop'}
                      </button>
                    )}
                    {stack.status === 'Building' && (
                      <Link
                        to={`/stacks/${stack.stackId}/build`}
                        className="inline-flex items-center gap-1 rounded-md border border-blue-300 bg-blue-50 px-2 py-1 text-xs font-medium text-blue-800 hover:bg-blue-100"
                      >
                        <Hammer className="h-3.5 w-3.5" />
                        View progress
                      </Link>
                    )}
                    {/* Rebuild button - available for Building/Failed states, or anytime really */}
                    {!isSetupIncomplete && (stack.status === 'Building' || stack.status === 'Failed' || stack.status === 'Stopped') && (
                      <button
                        onClick={() => rebuildStack.mutate(stack.stackId)}
                        disabled={rebuildStack.isPending}
                        className="inline-flex items-center gap-1 rounded-md bg-blue-600 px-2 py-1 text-xs font-medium text-white hover:bg-blue-700 disabled:opacity-50"
                        title={stack.status === 'Building' ? 'Retry build' : 'Rebuild stack'}
                      >
                        {rebuildStack.isPending ? (
                          <Loader2 className="h-3.5 w-3.5 animate-spin" />
                        ) : (
                          <Hammer className="h-3.5 w-3.5" />
                        )}
                        Rebuild
                      </button>
                    )}
                    <button
                      onClick={() =>
                        setDeletingStack({
                          id: stack.stackId,
                          name: stackListName(stack),
                          isExternal,
                        })
                      }
                      className="inline-flex items-center gap-1 rounded-md border border-gray-300 bg-white px-2 py-1 text-xs font-medium text-gray-700 hover:bg-gray-50"
                      title="Delete stack"
                    >
                      <Trash2 className="h-3.5 w-3.5" />
                    </button>
                  </div>
                </div>
              </div>
            )
          })}
        </div>
      )}

      {/* Delete Confirmation Dialog */}
      {deletingStack && (
        <DeleteStackDialog
          stackName={deletingStack.name}
          isExternal={deletingStack.isExternal}
          onConfirm={(terminateCloudInstance) =>
            deleteStack.mutate({ stackId: deletingStack.id, terminateCloudInstance })
          }
          onCancel={() => {
            if (!deleteStack.isPending) {
              setDeletingStack(null)
              deleteStack.reset()
            }
          }}
          isDeleting={deleteStack.isPending}
          error={deleteStack.error ? apiErrorMessage(deleteStack.error) : null}
        />
      )}
      
      {/* Import Stacks Dialog */}
      <ImportStacksDialog
        isOpen={importDialogOpen}
        onClose={() => setImportDialogOpen(false)}
        onImportSuccess={() => {
          probeAll.mutate()
        }}
      />
    </div>
  )
}
