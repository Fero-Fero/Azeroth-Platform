import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { CheckCircle2, Loader2, Wand2, XCircle } from 'lucide-react'
import { CiBuildStatusBadge } from '@/components/stacks/CiBuildStatusBadge'
import StackStatusItemRow from '@/components/stacks/StackStatusItemRow'
import StackSetupOverview from '@/setup/StackSetupOverview'
import { useHasActiveSetupSteps } from '@/setup/hasActiveSetupSteps'
import { formatBytes } from '@/components/docker/DockerDiskUsage'
import { useArmoryJobContext } from '@/contexts/ArmoryJobContext'
import { useLauncherBuildStatus } from '@/hooks/useLauncher'
import { stackKeys } from '@/hooks/useStacks'
import { apiErrorMessage, isSshConnectivityError, isStaleVpcProbeCache, isVpcProbeSlow } from '@/lib/utils'
import { stackApi } from '@/services/api'
import type { DockerDiskUsageDto } from '@/types/docker.types'
import {
  ServerType,
  StackStatus,
  type RemotePrerequisiteCheckDto,
  type StackDetailsDto,
} from '@/types/stack.types'

type OverviewTabId =
  | 'overview'
  | 'docker'
  | 'vpc-ssh'
  | 'vpc-security'
  | 'vpc-logs'
  | 'armory-email'
  | 'client'
  | 'armory'
  | 'addons'
  | 'patches'

const formatSha = (sha?: string | null): string => {
  if (!sha) return 'Not yet built'
  return sha.substring(0, 7)
}

const formatRelativeTime = (date: string | Date): string => {
  const now = new Date()
  const time = new Date(date)
  const diffMs = now.getTime() - time.getTime()
  const diffMinutes = Math.floor(diffMs / 60000)
  const diffHours = Math.floor(diffMs / 3600000)
  const diffDays = Math.floor(diffMs / 86400000)

  if (diffMinutes < 1) return 'just now'
  if (diffMinutes < 60) return `${diffMinutes} minute${diffMinutes > 1 ? 's' : ''} ago`
  if (diffHours < 24) return `${diffHours} hour${diffHours > 1 ? 's' : ''} ago`
  if (diffDays < 7) return `${diffDays} day${diffDays > 1 ? 's' : ''} ago`
  return time.toLocaleDateString()
}

interface StackOverviewStatusPanelProps {
  stack: StackDetailsDto
  isExternalStack: boolean
  /** True while a background refetch is probing Docker (VPC SSH for external stacks). */
  isLiveStatusRefreshing?: boolean
  diskUsage?: DockerDiskUsageDto | null
  armoryRebuildPending?: boolean
  checkUpdatesPending: boolean
  onSelectTab: (tab: OverviewTabId) => void
  onOpenUpdateDialog: () => void
  onArmoryRebuildError?: (message: string) => void
}

function panelAction(label: string, onClick: () => void, disabled = false) {
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      className="rounded-md bg-amber-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-amber-700 disabled:cursor-not-allowed disabled:opacity-50"
    >
      {label}
    </button>
  )
}

function RemoteSetupStepsList({ steps }: { steps: RemotePrerequisiteCheckDto[] }) {
  return (
    <ul className="mt-2 space-y-1">
      {steps.map((step) => (
        <li
          key={step.name}
          className={`flex items-start gap-1.5 text-xs ${step.passed ? 'text-green-900' : 'text-red-900'}`}
        >
          {step.passed ? (
            <CheckCircle2 className="mt-0.5 h-3.5 w-3.5 shrink-0 text-green-600" aria-hidden="true" />
          ) : (
            <XCircle className="mt-0.5 h-3.5 w-3.5 shrink-0 text-red-700" aria-hidden="true" />
          )}
          <span>
            {step.name}
            {step.message ? ` - ${step.message}` : ''}
          </span>
        </li>
      ))}
    </ul>
  )
}

export default function StackOverviewStatusPanel({
  stack,
  isExternalStack,
  isLiveStatusRefreshing = false,
  diskUsage,
  armoryRebuildPending,
  checkUpdatesPending,
  onSelectTab,
  onOpenUpdateDialog,
  onArmoryRebuildError,
}: StackOverviewStatusPanelProps) {
  const navigate = useNavigate()
  const queryClient = useQueryClient()
  const { data: launcherBuild } = useLauncherBuildStatus(false)
  const { isRebuildRunning, enqueueRebuild } = useArmoryJobContext()
  const [dockerSetupMessage, setDockerSetupMessage] = useState<string | null>(null)
  const [dockerSetupSuccess, setDockerSetupSuccess] = useState<boolean | null>(null)
  const [dockerSetupSteps, setDockerSetupSteps] = useState<RemotePrerequisiteCheckDto[] | null>(null)

  const provisionVpcDocker = useMutation({
    mutationFn: () => stackApi.provisionVpcDocker(stack.stackId),
    onMutate: () => {
      setDockerSetupSuccess(null)
      setDockerSetupMessage(null)
      setDockerSetupSteps(null)
    },
    onSuccess: (res) => {
      setDockerSetupSuccess(res.data.success)
      setDockerSetupMessage(res.data.message)
      setDockerSetupSteps(res.data.steps ?? null)
      if (res.data.success) {
        queryClient.invalidateQueries({ queryKey: stackKeys.detail(stack.stackId) })
      }
    },
    onError: (err) => {
      setDockerSetupSuccess(false)
      setDockerSetupMessage(
        apiErrorMessage(
          err,
          'Remote Docker setup can take a few minutes. This runs systemctl and apt commands on your VPC over SSH.',
        ),
      )
      setDockerSetupSteps(null)
    },
  })

  const emailNeedsSetup =
    stack.configuration.includeArmory !== false &&
    stack.configuration.armoryAccounts?.useEmailConfirmation &&
    !stack.configuration.armoryAccounts.emailConfigured

  const isExpress = stack.configuration.serverType === ServerType.Express
  const launcherBuilding = !isExpress && launcherBuild?.isBuilding
  const launcherNotBuilt = !isExpress && launcherBuild && !launcherBuild.downloadAvailable && !launcherBuilding

  const hasUpdates = stack.updateStatus?.hasUpdates === true
  const showArmoryRebuild = (armoryRebuildPending || isRebuildRunning) ?? false

  const dockerNotRunning =
    stack.dockerEngineAvailable === false &&
    !stack.needsExternalReconnect

  const vpcProbeReason = stack.dockerEngineUnavailableReason ?? dockerSetupMessage
  const vpcProbeSlow =
    isExternalStack &&
    !stack.needsExternalReconnect &&
    (isVpcProbeSlow(vpcProbeReason) || isStaleVpcProbeCache(vpcProbeReason))

  const vpcSshUnreachable =
    isExternalStack &&
    !stack.needsExternalReconnect &&
    !vpcProbeSlow &&
    isSshConnectivityError(vpcProbeReason)

  const dockerDaemonDown =
    dockerNotRunning && !vpcSshUnreachable && !isLiveStatusRefreshing

  const hasSetupSteps = useHasActiveSetupSteps(stack)
  const showUpdates = hasUpdates && !hasSetupSteps

  if (
    !diskUsage?.isWarning &&
    !(isExternalStack && stack.needsExternalReconnect) &&
    !dockerNotRunning &&
    !launcherBuilding &&
    !launcherNotBuilt &&
    !emailNeedsSetup &&
    !showUpdates &&
    !showArmoryRebuild &&
    !hasSetupSteps
  ) {
    return null
  }

  const onArmoryRebuild = async () => {
    try {
      await enqueueRebuild()
    } catch (err) {
      onArmoryRebuildError?.(err instanceof Error ? err.message : 'Failed to start armory rebuild.')
    }
  }

  return (
    <div className="mb-8 overflow-hidden rounded-lg border border-gray-200 bg-white shadow-sm">
      <div className="border-b border-gray-200 bg-gray-50 px-4 py-3">
        <h2 className="text-sm font-semibold text-gray-900">Stack status</h2>
        <p className="mt-0.5 text-xs text-gray-600">
          Setup items and warnings that need your attention. Expand a row for details.
        </p>
      </div>

      <div>
        {isLiveStatusRefreshing && (
          <StackStatusItemRow
            id="live-status-refresh"
            level="loading"
            title={isExternalStack ? 'Re-establishing connection' : 'Refreshing live status'}
            summary={
              isExternalStack
                ? 'Reconnecting to the VPC over SSH and refreshing container status…'
                : 'Querying the local Docker engine for container status…'
            }
            forceCollapsed
          />
        )}

        {diskUsage?.isWarning && (
          <StackStatusItemRow
            id="docker-disk"
            level="warning"
            title="Docker disk space is running low"
            summary={`${diskUsage.usedPercent.toFixed(1)}% full (${formatBytes(diskUsage.usedBytes)} of ${formatBytes(diskUsage.totalBytes)} used).`}
            details={
              <p>
                Review unused images and reclaim disk space from the Docker tab before builds or uploads fail
                due to lack of space.
              </p>
            }
            action={panelAction('Open Docker tab', () => onSelectTab('docker'))}
          />
        )}

        {isExternalStack && stack.needsExternalReconnect && (
          <StackStatusItemRow
            id="external-reconnect"
            level="warning"
            title="External stack needs reconnect"
            summary="SSH credentials are required to reach the remote Docker engine."
            details={
              <p>
                Open the VPC overview tab to enter or refresh SSH credentials so the manager can control
                containers on the remote host.
              </p>
            }
            action={panelAction('Open VPC overview', () => onSelectTab('vpc-ssh'))}
          />
        )}

        {vpcProbeSlow && !isLiveStatusRefreshing && (
          <StackStatusItemRow
            id="vpc-probe-slow"
            level="loading"
            title="Re-establishing connection"
            summary="Reconnecting to the VPC. Container status below may be briefly out of date."
            details={
              <p className="text-sm">
                The manager is retrying the SSH connection to your remote Docker engine. On small VPS
                instances this can take a moment when the host is under load - status will update automatically
                once the connection is restored.
              </p>
            }
            forceCollapsed
          />
        )}

        {vpcSshUnreachable && (
          <StackStatusItemRow
            id="vpc-ssh-unreachable"
            level="error"
            title="Cannot reach VPC over SSH"
            summary={
              dockerSetupMessage ??
              stack.dockerEngineUnavailableReason ??
              'The manager cannot connect to the remote host on the SSH port.'
            }
            defaultExpanded
            details={
              <div className="space-y-2">
                <p>
                  This is a network or connectivity problem - not a Docker daemon issue. After a cloud
                  instance reboot or stop/start, the public IP often changes unless you use an Elastic IP.
                </p>
                <p>
                  Open <strong>VPC overview → SSH</strong>, update the remote host to the
                  current IP (private key can stay blank if unchanged), and use <strong>Save &amp; reconnect</strong>.
                  Also confirm port 22 is allowed from this manager in your cloud security group.
                </p>
                {dockerSetupMessage && dockerSetupSteps && dockerSetupSteps.length > 0 && (
                  <RemoteSetupStepsList steps={dockerSetupSteps} />
                )}
              </div>
            }
            action={panelAction('Update VPC connection', () => onSelectTab('vpc-ssh'))}
          />
        )}

        {dockerDaemonDown && (
          <StackStatusItemRow
            id="docker-engine"
            level="error"
            title={isExternalStack ? 'Remote Docker daemon is not running' : 'Docker is not running'}
            summary={
              dockerSetupMessage ??
              stack.dockerEngineUnavailableReason ??
              (isExternalStack
                ? 'SSH to the VPC works, but the Docker daemon on the remote host is not responding.'
                : 'The Docker engine on this host is not reachable.')
            }
            defaultExpanded={isExternalStack}
            details={
              isExternalStack ? (
                <div className="space-y-3">
                  <p>
                    The manager can reach the VPC over SSH, but the Docker daemon is stopped or not
                    responding. MySQL tunnels, container controls, and stack start will fail until Docker
                    is running on the remote host.
                  </p>
                  <p>
                    Use <strong>Start Docker on VPC</strong> to run{' '}
                    <span className="font-mono text-xs">systemctl start docker</span> (and install Docker
                    if needed) over SSH - no manual login required.
                  </p>
                  {dockerSetupMessage && (
                    <div
                      role="status"
                      className={`rounded-md border px-3 py-2 text-xs ${
                        dockerSetupSuccess
                          ? 'border-green-200 bg-green-50 text-green-900'
                          : 'border-red-200 bg-red-50 text-red-950'
                      }`}
                    >
                      <p className="font-medium">{dockerSetupMessage}</p>
                      {dockerSetupSteps && dockerSetupSteps.length > 0 && (
                        <RemoteSetupStepsList steps={dockerSetupSteps} />
                      )}
                    </div>
                  )}
                  {!dockerSetupMessage && (
                    <p className="font-mono text-xs text-red-900/90">
                      sudo systemctl enable docker
                      <br />
                      sudo systemctl start docker
                    </p>
                  )}
                  <p>After Docker is running, use Start stack on this overview or start containers from the Docker tab.</p>
                </div>
              ) : (
                <p>
                  Start the Docker service on the manager host, then retry stack operations from this
                  overview or the Docker tab.
                </p>
              )
            }
            action={
              isExternalStack ? (
                <button
                  type="button"
                  onClick={() => provisionVpcDocker.mutate()}
                  disabled={provisionVpcDocker.isPending}
                  className="inline-flex items-center gap-1.5 rounded-md bg-red-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-red-700 disabled:cursor-not-allowed disabled:opacity-50"
                  title="Start or install Docker on the remote VPC over SSH"
                >
                  {provisionVpcDocker.isPending ? (
                    <>
                      <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />
                      Starting…
                    </>
                  ) : dockerSetupSuccess ? (
                    <>
                      <CheckCircle2 className="h-4 w-4" aria-hidden="true" />
                      Docker started
                    </>
                  ) : (
                    'Start Docker on VPC'
                  )}
                </button>
              ) : (
                panelAction('Open Docker tab', () => onSelectTab('docker'))
              )
            }
          />
        )}

        {launcherBuilding && (
          <StackStatusItemRow
            id="launcher-building"
            level="loading"
            title="Launcher build in progress"
            summary={launcherBuild?.message || 'Packaging the launcher distribution…'}
            forceCollapsed
          />
        )}

        {launcherNotBuilt && (
          <StackStatusItemRow
            id="launcher-not-built"
            level="warning"
            title="Launcher not built"
            summary="Build the launcher on the Launcher page before players can download the client."
            details={
              <p>
                The manager has no compiled launcher yet. Configure branding on the Launcher page, then run a
                build so stacks can serve the installer to players.
              </p>
            }
            action={panelAction('Open Launcher page', () => navigate('/launcher'))}
          />
        )}

        <StackSetupOverview stack={stack} onSelectTab={onSelectTab} />

        {emailNeedsSetup && (
          <StackStatusItemRow
            id="armory-email"
            level="warning"
            title="Complete email setup"
            summary="Email confirmation is enabled but SMTP is not configured - armory registration stays disabled."
            details={
              <p>
                Configure SMTP on the Armory Email tab so confirmation messages can be delivered to new
                accounts.
              </p>
            }
            action={panelAction('Configure email', () => onSelectTab('armory-email'))}
          />
        )}

        {showArmoryRebuild && (
          <StackStatusItemRow
            id="armory-rebuild"
            level={isRebuildRunning ? 'loading' : 'warning'}
            title={isRebuildRunning ? 'Rebuilding armory image' : 'Armory rebuild required'}
            summary={
              isRebuildRunning
                ? 'This runs in the background - you can navigate away while it completes.'
                : 'Saved styling or layout changes are not live until you rebuild the armory image.'
            }
            forceCollapsed={isRebuildRunning}
            action={
              isRebuildRunning ? undefined : (
                <button
                  type="button"
                  onClick={onArmoryRebuild}
                  className="inline-flex items-center gap-1.5 rounded-md bg-amber-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-amber-700"
                >
                  <Wand2 className="h-4 w-4" />
                  Rebuild
                </button>
              )
            }
          />
        )}

        {showUpdates && (
          <StackStatusItemRow
            id="stack-updates"
            level="warning"
            title="Updates available"
            summary="New core or module versions are available for this stack."
            defaultExpanded
            details={
              <div className="space-y-3">
                {stack.updateStatus?.latestCoreBuildStatus && (
                  <CiBuildStatusBadge status={stack.updateStatus.latestCoreBuildStatus} showDetails={false} />
                )}

                {stack.updateStatus?.isCoreOutdated && (
                  <div>
                    <div className="font-medium text-amber-900">AzerothCore Server</div>
                    <div className="mt-1 font-mono text-xs text-amber-800">
                      {formatSha(stack.updateStatus.currentCoreSha)} → {formatSha(stack.updateStatus.latestCoreSha)}
                    </div>
                  </div>
                )}

                {stack.updateStatus?.outdatedModules.map((module) => (
                  <div key={module.moduleId}>
                    <div className="font-medium text-amber-900">{module.moduleName}</div>
                    <div className="mt-1 font-mono text-xs text-amber-800">
                      {formatSha(module.currentCommitSha)} → {formatSha(module.latestCommitSha)}
                    </div>
                  </div>
                ))}

                {stack.updateStatus?.lastCheckedAt && (
                  <div className="flex items-center justify-between border-t border-amber-200 pt-2 text-xs text-amber-700">
                    <span>Last checked: {formatRelativeTime(stack.updateStatus.lastCheckedAt)}</span>
                    {checkUpdatesPending && (
                      <span className="inline-flex items-center gap-1">
                        <span className="inline-block h-2 w-2 animate-pulse rounded-full bg-blue-500" />
                        Checking…
                      </span>
                    )}
                  </div>
                )}
              </div>
            }
            action={panelAction(
              'Update stack',
              onOpenUpdateDialog,
              stack.status === StackStatus.Building,
            )}
          />
        )}
      </div>
    </div>
  )
}
