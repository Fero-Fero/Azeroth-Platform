import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Play, Square, RefreshCw, Plus, Loader2, Trash2, Hammer, AlertCircle, Download, Database, HardDrive, Cloud } from 'lucide-react'
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
import { stackApi, buildApi } from '@/services/api'
import type { StackDetailsDto } from '@/types/stack.types'
import { DeploymentTarget, StackStatus } from '@/types/stack.types'

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
    default:
      return 'bg-gray-100 text-gray-800'
  }
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
    mutationFn: (stackId: string) => stackApi.delete(stackId),
    onSuccess: (_data, deletedStackId) => {
      setDeletingStack(null)
      queryClient.setQueryData<StackDetailsDto[]>(stackKeys.lists(), (current) =>
        current?.filter((stack) => stack.stackId !== deletedStackId),
      )
      void probeAll.mutate()
    },
    onError: () => {
      setDeletingStack(null)
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

      <div className="mb-6 flex items-center justify-between">
        <div>
          <h1 className="text-3xl font-bold text-gray-900">Your Stacks</h1>
          <p className="mt-1 text-gray-500">
            Manage your AzerothCore server instances. Status is probed once when you open this page and
            cached until you refresh or open a stack for a live update.
          </p>
        </div>
        <div className="flex items-center gap-3">
          <button
            type="button"
            onClick={() => probeAll.mutate()}
            disabled={isProbing || stacks.length === 0}
            className="inline-flex items-center gap-2 rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
          >
            {isProbing ? <Loader2 className="h-4 w-4 animate-spin" /> : <RefreshCw className="h-4 w-4" />}
            Refresh all
          </button>
          <button
            onClick={() => setImportDialogOpen(true)}
            className="flex items-center gap-2 rounded-md bg-white border border-gray-300 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50"
          >
            <Download className="h-4 w-4" />
            Import Existing Stacks
          </button>
          <Link
            to="/stacks/new"
            className="flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700"
          >
            <Plus className="h-4 w-4" />
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
          <div className="mt-4 flex items-center justify-center gap-3">
            <button
              onClick={() => setImportDialogOpen(true)}
              className="inline-flex items-center gap-2 rounded-md bg-white border border-gray-300 px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50"
            >
              <Download className="h-4 w-4" />
              Import Existing Stacks
            </button>
            <Link
              to="/stacks/new"
              className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700"
            >
              <Plus className="h-4 w-4" />
              Create Stack
            </Link>
          </div>
        </div>
      ) : (
        <div className={`space-y-4 ${isProbing ? 'opacity-90' : ''}`}>
          {stacks.map((stack) => {
            const displayStatus =
              stack.status === StackStatus.Failed ? StackStatus.Stopped : stack.status
            const isRunning = displayStatus === StackStatus.Running
            const isStopped = displayStatus === StackStatus.Stopped
            const isDegraded = displayStatus === StackStatus.Degraded
            const uptime = calculateStackUptime(stack)
            const isExternal = stack.configuration.deployment?.target === DeploymentTarget.External

            const lifecycleJob = lifecycleJobs[stack.stackId]
            const lifecycleBusy = isStackBusy(stack.stackId)
            const startActionBusy = lifecycleBusy && lifecycleJob?.action === 'Start'
            const startDbActionBusy = lifecycleBusy && lifecycleJob?.action === 'StartDatabase'
            const stopActionBusy = lifecycleBusy && lifecycleJob?.action === 'Stop'
            const restartActionBusy = lifecycleBusy && lifecycleJob?.action === 'Restart'

            return (
              <div
                key={stack.stackId}
                className="rounded-lg border border-gray-200 bg-white p-6 shadow-sm hover:shadow-md transition-shadow"
              >
                <div className="flex items-start justify-between">
                  <div className="flex-1">
                    <div className="flex items-center gap-3">
                      <Link
                        to={`/stacks/${stack.stackId}`}
                        className="text-xl font-semibold text-gray-900 hover:text-blue-600"
                      >
                        {stack.stackName}
                      </Link>
                      <span
                        className={`rounded-full px-2.5 py-0.5 text-xs font-medium ${getStatusBadgeColor(displayStatus)}`}
                      >
                        {displayStatus}
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
                    </p>
                    <div className="mt-3 flex gap-4 text-sm text-gray-600">
                      <span>Auth: {stack.configuration.ports.authServer}</span>
                      <span>World: {stack.configuration.ports.worldServer}</span>
                      <span>DB: {stack.configuration.database.port}</span>
                    </div>
                  </div>

                  <div className="flex gap-2">
                    {(isStopped || isDegraded) && (
                      <button
                        onClick={() => startStack.mutate(stack.stackId)}
                        disabled={lifecycleBusy}
                        className="flex items-center gap-2 rounded-md bg-green-600 px-3 py-2 text-sm font-medium text-white hover:bg-green-700 disabled:opacity-50"
                      >
                        {startActionBusy ? (
                          <Loader2 className="h-4 w-4 animate-spin" />
                        ) : (
                          <Play className="h-4 w-4" />
                        )}
                        {startActionBusy ? 'Starting…' : 'Start'}
                      </button>
                    )}
                    {isStopped && (
                      <button
                        onClick={() => startDatabase.mutate(stack.stackId)}
                        disabled={lifecycleBusy}
                        title="Start only the database container (for patches/maintenance)"
                        className="flex items-center gap-2 rounded-md border border-green-600 bg-white px-3 py-2 text-sm font-medium text-green-700 hover:bg-green-50 disabled:opacity-50"
                      >
                        {startDbActionBusy ? (
                          <Loader2 className="h-4 w-4 animate-spin" />
                        ) : (
                          <Database className="h-4 w-4" />
                        )}
                        {startDbActionBusy ? 'Starting DB…' : 'Start DB'}
                      </button>
                    )}
                    {isRunning && (
                      <button
                        onClick={() => restartStack.mutate(stack.stackId)}
                        disabled={lifecycleBusy}
                        className="flex items-center gap-2 rounded-md bg-yellow-600 px-3 py-2 text-sm font-medium text-white hover:bg-yellow-700 disabled:opacity-50"
                      >
                        {restartActionBusy ? (
                          <Loader2 className="h-4 w-4 animate-spin" />
                        ) : (
                          <RefreshCw className="h-4 w-4" />
                        )}
                        {restartActionBusy ? 'Restarting…' : 'Restart'}
                      </button>
                    )}
                    {(isRunning || isDegraded) && (
                      <button
                        onClick={() => stopStack.mutate(stack.stackId)}
                        disabled={lifecycleBusy}
                        className="flex items-center gap-2 rounded-md bg-red-600 px-3 py-2 text-sm font-medium text-white hover:bg-red-700 disabled:opacity-50"
                      >
                        {stopActionBusy ? (
                          <Loader2 className="h-4 w-4 animate-spin" />
                        ) : (
                          <Square className="h-4 w-4" />
                        )}
                        {stopActionBusy ? 'Stopping…' : 'Stop'}
                      </button>
                    )}
                    {stack.status === 'Building' && (
                      <Link
                        to={`/stacks/${stack.stackId}/build`}
                        className="flex items-center gap-2 rounded-md border border-blue-300 bg-blue-50 px-3 py-2 text-sm font-medium text-blue-800 hover:bg-blue-100"
                      >
                        <Hammer className="h-4 w-4" />
                        View progress
                      </Link>
                    )}
                    {/* Rebuild button - available for Building/Failed states, or anytime really */}
                    {(stack.status === 'Building' || stack.status === 'Failed' || stack.status === 'Stopped') && (
                      <button
                        onClick={() => rebuildStack.mutate(stack.stackId)}
                        disabled={rebuildStack.isPending}
                        className="flex items-center gap-2 rounded-md bg-blue-600 px-3 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
                        title={stack.status === 'Building' ? 'Retry build' : 'Rebuild stack'}
                      >
                        {rebuildStack.isPending ? (
                          <Loader2 className="h-4 w-4 animate-spin" />
                        ) : (
                          <Hammer className="h-4 w-4" />
                        )}
                        Rebuild
                      </button>
                    )}
                    <button
                      onClick={() =>
                        setDeletingStack({
                          id: stack.stackId,
                          name: stack.stackName,
                          isExternal,
                        })
                      }
                      className="flex items-center gap-2 rounded-md border border-gray-300 bg-white px-3 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50"
                      title="Delete stack"
                    >
                      <Trash2 className="h-4 w-4" />
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
          onConfirm={() => deleteStack.mutate(deletingStack.id)}
          onCancel={() => setDeletingStack(null)}
          isDeleting={deleteStack.isPending}
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
