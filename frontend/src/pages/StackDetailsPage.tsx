import { useParams, useNavigate, useSearchParams } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { stackApi, buildApi } from '@/services/api'
import { StackStatus } from '@/types/stack.types'
import type { StackServiceDto, StackServiceAction } from '@/types/stack.types'
import { useState, useMemo, useEffect, useRef } from 'react'
import { stackKeys } from '@/hooks/useStacks'
import { accountKeys } from '@/hooks/useAccounts'
import { characterKeys } from '@/hooks/useCharacters'
import { useArmoryJobContext, ArmoryJobProvider } from '@/contexts/ArmoryJobContext'
import { useStackJob } from '@/hooks/useStackJob'
import EditStackConfigModal from '@/components/EditStackConfigModal'
import UpdateStackDialog from '@/components/UpdateStackDialog'
import RebuildStackDialog from '@/components/RebuildStackDialog'
import type { ConfigMigrationMode } from '@/components/config/ConfigMigrationModeChoice'
import AccountsTab from '@/components/accounts/AccountsTab'
import CharactersTab from '@/components/characters/CharactersTab'
import RealmsTab from '@/components/realms/RealmsTab'
import PatchesTab from '@/components/patches/PatchesTab'
import AddonsManager from '@/components/addons/AddonsManager'
import LuaScriptsTab from '@/components/lua/LuaScriptsTab'
import ServerConfigTab from '@/components/config/ServerConfigTab'
import EnvironmentVariablesTab from '@/components/env/EnvironmentVariablesTab'
import ArmoryDataManager from '@/components/armory/ArmoryDataManager'
import ArmoryStylingTab from '@/components/armory/ArmoryStylingTab'
import ArmoryLayoutTab from '@/components/armory/ArmoryLayoutTab'
import ArmoryEmailTab from '@/components/armory/ArmoryEmailTab'
import ArmoryRebuildBanner from '@/components/armory/ArmoryRebuildBanner'
import ClientTab from '@/components/client/ClientTab'
import StackModulesTab from '@/components/modules/StackModulesTab'
import { useClientBaseInfo } from '@/hooks/useClient'
import { useArmoryAssetsInfo } from '@/hooks/useArmoryAssets'
import { useRealms } from '@/hooks/useRealms'
import RevisionsTab from '@/components/revisions/RevisionsTab'
import LauncherProfileTab from '@/components/launcher/LauncherProfileTab'
import RealmlistOverrideField from '@/components/launcher/RealmlistOverrideField'
import ArmoryNetworkField from '@/components/launcher/ArmoryNetworkField'
import StackNewsTab from '@/components/launcher/StackNewsTab'
import DockerTab from '@/components/docker/DockerTab'
import ExternalReconnectPanel from '@/components/stacks/ExternalReconnectPanel'
import InitialBuildRequiredPanel from '@/components/stacks/InitialBuildRequiredPanel'
import { formatBytes } from '@/components/docker/DockerDiskUsage'
import { useDockerDiskUsage } from '@/hooks/useStackDocker'
import ModuleSetupWarnings from '@/components/modules/ModuleSetupWarnings'
import { resolveArmoryBrowseUrl } from '@/lib/armory-network'
import { CiBuildStatusBadge } from '@/components/CiBuildStatusBadge'
import { useLauncherProfile } from '@/hooks/useLauncher'
import { Eye, EyeOff, Copy, Play, Square, RotateCw, Hammer, Loader2 } from 'lucide-react'

// Helper to format commit SHAs safely
const formatSha = (sha?: string | null): string => {
  if (!sha) return 'Not yet built'
  return sha.substring(0, 7)
}

// Helper to format relative time
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

type StackTabId =
  | 'overview'
  | 'accounts'
  | 'characters'
  | 'realms'
  | 'addons'
  | 'client'
  | 'armory'
  | 'armory-styling'
  | 'armory-layout'
  | 'armory-email'
  | 'launcher'
  | 'modules'
  | 'patches'
  | 'lua'
  | 'news'
  | 'config'
  | 'env'
  | 'revisions'
  | 'logs'
  | 'docker'

// Stack tabs grouped into nested categories. Single-tab groups (Overview, Launcher, News) act as
// standalone tabs; multi-tab groups render a secondary row of sub-tabs when active.
const TAB_GROUPS: { id: string; label: string; tabs: { id: StackTabId; label: string }[] }[] = [
  { id: 'overview', label: 'Overview', tabs: [{ id: 'overview', label: 'Overview' }] },
  {
    id: 'client',
    label: 'Client',
    tabs: [
      { id: 'client', label: 'Client Files' },
      { id: 'addons', label: 'Addons' },
    ],
  },
  {
    id: 'server',
    label: 'Server',
    tabs: [
      { id: 'accounts', label: 'Accounts' },
      { id: 'characters', label: 'Characters' },
      { id: 'realms', label: 'Realms' },
      { id: 'modules', label: 'Modules' },
      { id: 'patches', label: 'Patches' },
      { id: 'lua', label: 'Lua Scripts' },
      { id: 'config', label: 'Server Config' },
    ],
  },
  { id: 'launcher', label: 'Launcher', tabs: [{ id: 'launcher', label: 'Launcher' }] },
  {
    id: 'armory',
    label: 'Armory',
    tabs: [
      { id: 'armory', label: 'Data / Assets' },
      { id: 'armory-styling', label: 'Styling' },
      { id: 'armory-layout', label: 'Layout' },
      { id: 'armory-email', label: 'Email' },
    ],
  },
  { id: 'news', label: 'News', tabs: [{ id: 'news', label: 'News' }] },
  {
    id: 'advanced',
    label: 'Advanced',
    tabs: [
      { id: 'env', label: 'Environment Variables' },
      { id: 'revisions', label: 'Revisions' },
      { id: 'docker', label: 'Docker' },
      { id: 'logs', label: 'Logs' },
    ],
  },
]

const STACK_TAB_IDS = new Set<StackTabId>(TAB_GROUPS.flatMap((group) => group.tabs.map((tab) => tab.id)))

function resolveStackTab(value: string | null): StackTabId {
  return value && STACK_TAB_IDS.has(value as StackTabId) ? (value as StackTabId) : 'overview'
}

export default function StackDetailsPage() {
  const { stackId } = useParams<{ stackId: string }>()
  if (!stackId) {
    return null
  }
  return (
    <ArmoryJobProvider stackId={stackId}>
      <StackDetailsPageContent />
    </ArmoryJobProvider>
  )
}

function StackDetailsPageContent() {
  const { stackId } = useParams<{ stackId: string }>()
  const navigate = useNavigate()
  const [searchParams, setSearchParams] = useSearchParams()
  const queryClient = useQueryClient()
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false)
  const [showEditModal, setShowEditModal] = useState(false)
  const [showUpdateDialog, setShowUpdateDialog] = useState(false)
  const [showRebuildDialog, setShowRebuildDialog] = useState(false)
  const [recentLifecycleAction, setRecentLifecycleAction] = useState<number | null>(null)
  const [activeTab, setActiveTab] = useState<StackTabId>(() => resolveStackTab(searchParams.get('tab')))
  const [soapCredsVisible, setSoapCredsVisible] = useState(false)
  const [soapCopied, setSoapCopied] = useState<'username' | 'password' | null>(null)
  const [dbCredsVisible, setDbCredsVisible] = useState(false)
  const [dbCopied, setDbCopied] = useState<'host' | 'port' | 'user' | 'password' | null>(null)
  const [armoryRebuildError, setArmoryRebuildError] = useState<string | null>(null)
  const { data: launcherProfile } = useLauncherProfile(stackId ?? '', !!stackId)
  const { data: realms } = useRealms(stackId ?? '')

  useEffect(() => {
    const nextTab = resolveStackTab(searchParams.get('tab'))
    setActiveTab((current) => (current === nextTab ? current : nextTab))
  }, [searchParams])

  // Fetch stack details with auto-refresh every 5 seconds
  // Poll when: Running, Starting, Building, Degraded, Initializing, or within 30 seconds of a lifecycle action
  const { data: stack, isLoading, error } = useQuery({
    queryKey: stackKeys.detail(stackId!),
    queryFn: () => stackApi.get(stackId!).then(res => res.data),
    enabled: !!stackId,
    refetchInterval: (query) => {
      const status = query.state.data?.status
      const shouldPollForStatus = 
        status === StackStatus.Running || 
        status === StackStatus.Starting ||
        status === StackStatus.Building ||
        status === StackStatus.Degraded ||
        status === StackStatus.Initializing
      
      // Also poll for 30 seconds after any lifecycle action
      const shouldPollForRecent = recentLifecycleAction && 
        (Date.now() - recentLifecycleAction < 30000)
      
      return shouldPollForStatus || shouldPollForRecent ? 5000 : false
    },
  })

  const { data: diskUsage } = useDockerDiskUsage()
  const { data: armoryNetwork } = useQuery({
    queryKey: ['armory-network', stackId],
    queryFn: () => stackApi.armoryNetwork(stackId!).then((res) => res.data),
    enabled: !!stackId,
  })

  // Calculate stack uptime based on earliest running container
  const stackUptime = useMemo(() => {
    if (!stack || stack.containers.length === 0) return null
    
    const runningContainers = stack.containers.filter(c => 
      c.status.toLowerCase().includes('running') || c.status.toLowerCase().includes('up')
    )
    
    if (runningContainers.length === 0) return null
    
    const earliestStart = runningContainers.reduce((earliest, container) => {
      const startTime = new Date(container.startedAt).getTime()
      return startTime < earliest ? startTime : earliest
    }, Infinity)
    
    const uptimeMs = Date.now() - earliestStart
    const hours = Math.floor(uptimeMs / (1000 * 60 * 60))
    const minutes = Math.floor((uptimeMs % (1000 * 60 * 60)) / (1000 * 60))
    
    if (hours > 0) {
      return `${hours}h ${minutes}m`
    }
    return `${minutes}m`
  }, [stack])

  // Tracks the detached stack lifecycle job (start/stop/restart/start-database). Reattaches after a page
  // refresh or navigating away via GET + SignalR, so the UI reflects an in-flight operation and its
  // result regardless of which browser tab (or the list page) triggered it.
  const { job: stackJob, isStackBusy, applyStatus: applyStackStatus } = useStackJob(stackId ?? null)

  // Lifecycle mutations enqueue detached background jobs; seed the returned status into the job hook so
  // the UI reflects the operation instantly (and the polling/SignalR reattach engages).
  const startMutation = useMutation({
    mutationFn: () => stackApi.start(stackId!),
    onSuccess: (res) => {
      applyStackStatus(res.data)
      setRecentLifecycleAction(Date.now())
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(stackId!) })
      queryClient.invalidateQueries({ queryKey: stackKeys.lists() })
    },
  })

  const stopMutation = useMutation({
    mutationFn: () => stackApi.stop(stackId!),
    onSuccess: (res) => {
      applyStackStatus(res.data)
      setRecentLifecycleAction(Date.now())
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(stackId!) })
      queryClient.invalidateQueries({ queryKey: stackKeys.lists() })
    },
  })

  const restartMutation = useMutation({
    mutationFn: () => stackApi.restart(stackId!),
    onSuccess: (res) => {
      applyStackStatus(res.data)
      setRecentLifecycleAction(Date.now())
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(stackId!) })
      queryClient.invalidateQueries({ queryKey: stackKeys.lists() })
    },
  })

  // Brings only the database up (world/auth stopped) so migrations/maintenance run uninterrupted.
  const dbMaintenanceMutation = useMutation({
    mutationFn: () => stackApi.startDatabase(stackId!),
    onSuccess: (res) => {
      applyStackStatus(res.data)
      setRecentLifecycleAction(Date.now())
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(stackId!) })
      queryClient.invalidateQueries({ queryKey: stackKeys.lists() })
    },
  })

  // When a lifecycle job finishes, refresh the stack so container/status reflect the result.
  const lastHandledStackJobRef = useRef<string | null>(null)
  useEffect(() => {
    if (stackJob && !stackJob.isRunning && stackJob.jobId !== lastHandledStackJobRef.current) {
      lastHandledStackJobRef.current = stackJob.jobId
      setRecentLifecycleAction(Date.now())
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(stackId!) })
      queryClient.invalidateQueries({ queryKey: stackKeys.lists() })

      // Actions that bring the database up make account/character data available again — reload it
      // so anyone viewing the Accounts or Characters tab sees fresh data once the DB is ready.
      if (
        stackJob.action === 'Start' ||
        stackJob.action === 'StartDatabase' ||
        stackJob.action === 'Restart'
      ) {
        queryClient.invalidateQueries({ queryKey: accountKeys.list(stackId!) })
        queryClient.invalidateQueries({ queryKey: characterKeys.list(stackId!) })
      }
    }
  }, [stackJob, queryClient, stackId])

  // Tracks the detached armory background job (start/stop/restart/rebuild). Reattaches after a page
  // refresh via GET + SignalR, so the UI reflects an in-flight operation and its result regardless of
  // whether this browser tab triggered it.
  const { job: armoryJob, isArmoryBusy, applyStatus: applyArmoryStatus } = useArmoryJobContext()

  // When an armory job finishes, refresh the stack so armoryRunning / container state reflect the result.
  const lastHandledArmoryJobRef = useRef<string | null>(null)
  useEffect(() => {
    if (armoryJob && !armoryJob.isRunning && armoryJob.jobId !== lastHandledArmoryJobRef.current) {
      lastHandledArmoryJobRef.current = armoryJob.jobId
      setRecentLifecycleAction(Date.now())
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(stackId!) })
      queryClient.invalidateQueries({ queryKey: stackKeys.lists() })
    }
  }, [armoryJob, queryClient, stackId])

  // Starts/stops the per-stack armory container independently of the game servers. The backend runs
  // these as detached background jobs and returns the initial status, which we seed into the job hook.
  const armoryStartMutation = useMutation({
    mutationFn: () => stackApi.startArmory(stackId!),
    onSuccess: (res) => applyArmoryStatus(res.data),
  })

  const armoryStopMutation = useMutation({
    mutationFn: () => stackApi.stopArmory(stackId!),
    onSuccess: (res) => applyArmoryStatus(res.data),
  })

  // Per-service (per-container) lifecycle actions. Tracks which service is mid-action so only that
  // card's buttons show a spinner / disable, while the 5s poll refreshes state afterwards.
  const [pendingService, setPendingService] = useState<string | null>(null)
  const serviceActionMutation = useMutation({
    mutationFn: ({ service, action }: { service: string; action: StackServiceAction }) =>
      stackApi.serviceAction(stackId!, service, action),
    onMutate: ({ service }) => setPendingService(service),
    onSuccess: (res, { service }) => {
      // Armory actions return a background-job status; seed it so the UI reflects the running job.
      if (service === 'frontend-armory') {
        applyArmoryStatus(res.data)
      }
    },
    onSettled: () => {
      setPendingService(null)
      setRecentLifecycleAction(Date.now())
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(stackId!) })
      queryClient.invalidateQueries({ queryKey: stackKeys.lists() })
    },
  })

  const deleteMutation = useMutation({
    mutationFn: () => stackApi.delete(stackId!),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: stackKeys.lists() })
      navigate('/stacks')
    },
  })

  const rebuildMutation = useMutation({
    mutationFn: (configMigrationMode: ConfigMigrationMode) =>
      buildApi.start(stackId!, undefined, configMigrationMode),
    onSuccess: () => {
      setShowRebuildDialog(false)
      queryClient.invalidateQueries({ queryKey: stackKeys.lists() })
      navigate(`/stacks/${stackId}/build`)
    },
  })

  const retryInitialBuildMutation = useMutation({
    mutationFn: () => buildApi.start(stackId!),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(stackId!) })
      queryClient.invalidateQueries({ queryKey: stackKeys.lists() })
      navigate(`/stacks/${stackId}/build`)
    },
  })

  const checkUpdatesMutation = useMutation({
    mutationFn: () => stackApi.checkUpdates(stackId!),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(stackId!) })
      queryClient.invalidateQueries({ queryKey: stackKeys.lists() })
    },
  })

  const updateStackMutation = useMutation({
    mutationFn: async (configMigrationMode: ConfigMigrationMode) => {
      // Stop stack if running
      if (stack?.status === StackStatus.Running) {
        await stackApi.stop(stackId!)
        // Small delay to ensure stop is processed
        await new Promise(resolve => setTimeout(resolve, 500))
      }
      // Now trigger update
      return stackApi.update(stackId!, configMigrationMode)
    },
    onSuccess: () => {
      setShowUpdateDialog(false)
      queryClient.invalidateQueries({ queryKey: stackKeys.lists() })
      // Redirect to build page to show progress
      navigate(`/stacks/${stackId}/build`)
    },
    onError: (error) => {
      console.error('Update failed:', error)
      // User will see error in the dialog
    },
  })

  const soapCredentialsQuery = useQuery({
    queryKey: [...stackKeys.detail(stackId!), 'soap-credentials'],
    queryFn: () => stackApi.getSoapCredentials(stackId!),
    enabled: !!stackId && !!stack?.isAdminAccountInitialized,
    select: (res) => res.data,
  })

  // Root password is no longer part of the detail payload; fetch it on demand from the audited
  // reveal endpoint only when the operator chooses to view it.
  const [dbCredsRequested, setDbCredsRequested] = useState(false)
  const dbCredentialsQuery = useQuery({
    queryKey: [...stackKeys.detail(stackId!), 'database-credentials'],
    queryFn: () => stackApi.getDatabaseCredentials(stackId!),
    enabled: !!stackId && dbCredsRequested,
    select: (res) => res.data,
  })

  // Per-stack client + armory data presence, used to prompt on the Overview when either is missing
  // (each stack now keeps its own base client and armory dataset, so both are uploaded per stack).
  const clientBaseInfoQuery = useClientBaseInfo(stackId!)
  const armoryAssetsInfoQuery = useArmoryAssetsInfo(stackId!)
  const clientDataMissing = clientBaseInfoQuery.data ? !clientBaseInfoQuery.data.exists : false
  const armoryDataMissing = armoryAssetsInfoQuery.data ? !armoryAssetsInfoQuery.data.dataUploaded : false
  const isArmoryTab =
    activeTab === 'armory' || activeTab === 'armory-styling' || activeTab === 'armory-layout' || activeTab === 'armory-email'

  if (isLoading) {
    return (
      <div className="flex items-center justify-center min-h-96">
        <div className="text-lg text-gray-600">Loading stack details...</div>
      </div>
    )
  }

  if (error || !stack) {
    return (
      <div className="max-w-2xl mx-auto mt-12">
        <div className="bg-red-50 border border-red-200 rounded-lg p-6">
          <h2 className="text-xl font-semibold text-red-800 mb-2">Stack Not Found</h2>
          <p className="text-red-600 mb-4">
            The stack you're looking for doesn't exist or has been deleted.
          </p>
          <button
            onClick={() => navigate('/stacks')}
            className="px-4 py-2 bg-red-600 text-white rounded hover:bg-red-700 transition"
          >
            Back to Stacks
          </button>
        </div>
      </div>
    )
  }

  const hasCompletedBuild =
    stack.hasCompletedBuild === true ||
    (stack.hasCompletedBuild === undefined && !!stack.updateStatus?.currentCoreSha)

  if (!hasCompletedBuild) {
    return (
      <InitialBuildRequiredPanel
        stack={stack}
        stackId={stackId!}
        onRetryBuild={() => retryInitialBuildMutation.mutate()}
        isRetrying={retryInitialBuildMutation.isPending}
        onDelete={() => deleteMutation.mutate()}
        isDeleting={deleteMutation.isPending}
      />
    )
  }

  const getStatusColor = (status: StackStatus) => {
    switch (status) {
      case StackStatus.Running:
        return 'bg-green-100 text-green-800 border-green-200'
      case StackStatus.Initializing:
        return 'bg-blue-100 text-blue-800 border-blue-200'
      case StackStatus.Starting:
        return 'bg-yellow-100 text-yellow-800 border-yellow-200'
      case StackStatus.Degraded:
        return 'bg-orange-100 text-orange-800 border-orange-200'
      case StackStatus.Stopped:
        return 'bg-gray-100 text-gray-800 border-gray-200'
      case StackStatus.Building:
        return 'bg-blue-100 text-blue-800 border-blue-200'
      case StackStatus.Failed:
        return 'bg-red-100 text-red-800 border-red-200'
      default:
        return 'bg-gray-100 text-gray-800 border-gray-200'
    }
  }

  const getContainerStatusColor = (status: string) => {
    if (status.toLowerCase().includes('running') || status.toLowerCase().includes('up')) {
      return 'text-green-600'
    }
    if (status.toLowerCase().includes('exited')) {
      return 'text-gray-600'
    }
    return 'text-yellow-600'
  }

  const getHealthIcon = (health: string) => {
    if (health === 'healthy') return '✓'
    if (health === 'unhealthy') return '✗'
    return '○'
  }

  const resolveServiceHealth = (svc: StackServiceDto) => {
    const raw = (svc.health ?? '').toLowerCase()
    if (raw === 'healthy' || raw === 'unhealthy') return raw
    if (svc.state === 'running' && (svc.service === 'ac-authserver' || svc.service === 'ac-worldserver')) {
      return 'healthy'
    }
    if (!raw || raw === 'unknown') return svc.state === 'absent' ? 'n/a' : 'unknown'
    return raw
  }

  const formatHealthLabel = (health: string) =>
    health === 'n/a' ? 'Health: n/a' : `Health: ${health.charAt(0).toUpperCase()}${health.slice(1)}`

  // Per-service state presentation + action gating.
  const isServiceRunning = (svc: StackServiceDto) => svc.state === 'running'
  const serviceStateDot = (state: string) => {
    if (state === 'running') return 'bg-green-500'
    if (state === 'restarting') return 'bg-yellow-400'
    if (state === 'dead') return 'bg-red-500'
    if (state === 'absent') return 'bg-gray-300'
    return 'bg-gray-400'
  }
  const serviceStateLabel = (state: string) => {
    if (state === 'absent') return 'Not created'
    return state.charAt(0).toUpperCase() + state.slice(1)
  }

  // From Degraded (DB maintenance) Start brings the world/auth servers back up.
  const canStart = stack.status === StackStatus.Stopped || stack.status === StackStatus.Failed || stack.status === StackStatus.Degraded
  const canStop = stack.status === StackStatus.Running || stack.status === StackStatus.Starting || stack.status === StackStatus.Degraded || stack.status === StackStatus.Initializing
  const canRestart = stack.status === StackStatus.Running || stack.status === StackStatus.Degraded
  // Already in DB maintenance (Degraded) → button is disabled; use Start/Stop/Restart instead.
  const canDbMaintenance = stack.status === StackStatus.Running || stack.status === StackStatus.Stopped
  // A lifecycle job (from this tab, another tab, or the stacks list) makes every lifecycle button wait.
  const isTransitioning = startMutation.isPending || stopMutation.isPending || restartMutation.isPending || dbMaintenanceMutation.isPending || isStackBusy
  const isArmoryPending = armoryStartMutation.isPending || armoryStopMutation.isPending || isArmoryBusy
  // The armory queries the game DB; if the DB isn't up, starting the armory will bring it up first.
  // Allow starting from Stopped too (not just Running/Degraded); only block transitional/build states.
  const canStartArmory = !stack.armoryRunning &&
    (stack.status === StackStatus.Running ||
      stack.status === StackStatus.Degraded ||
      stack.status === StackStatus.Stopped)
  const realmlistHost =
    launcherProfile?.effectiveRealmlistHost || stack.configuration.advanced.realmlistHost
  const armoryUrl =
    armoryNetwork && stack.armoryPort > 0
      ? resolveArmoryBrowseUrl(armoryNetwork.effectiveBindAddress, stack.armoryPort, realmlistHost)
      : null

  const selectTab = (tab: StackTabId) => {
    setActiveTab(tab)
    const next = new URLSearchParams(searchParams)
    if (tab === 'overview') {
      next.delete('tab')
    } else {
      next.set('tab', tab)
    }
    setSearchParams(next, { replace: true })
  }

  return (
    <div className="max-w-6xl mx-auto">
      {/* Header */}
      <div className="mb-8">
        <div className="flex items-center justify-between mb-4">
          <div>
            <button
              onClick={() => navigate('/stacks')}
              className="text-sm text-gray-600 hover:text-gray-800 mb-2 inline-flex items-center gap-1"
            >
              ← Back to Stacks
            </button>
            <h1 className="text-3xl font-bold text-gray-900">{stack.stackName}</h1>
            <div className="flex items-center gap-3 mt-1">
              <p className="text-sm text-gray-500">
                Created {new Date(stack.createdAt).toLocaleDateString()} • {stack.serverType}
              </p>
              {stackUptime && (
                <>
                  <span className="text-gray-300">•</span>
                  <p className="text-sm text-green-600 font-medium">
                    Uptime: {stackUptime}
                  </p>
                </>
              )}
            </div>
          </div>
          <div>
            <span className={`px-4 py-2 rounded-full text-sm font-medium border ${getStatusColor(stack.status)}`}>
              {stack.status}
            </span>
          </div>
        </div>

        {/* Lifecycle Controls */}
        {stack.status === StackStatus.Building && (
          <div className="mb-4 rounded-lg border border-blue-200 bg-blue-50 px-4 py-3 text-sm text-blue-900">
            A worldserver build is in progress.{' '}
            <button
              type="button"
              onClick={() => navigate(`/stacks/${stackId}/build`)}
              className="font-medium underline hover:text-blue-950"
            >
              View build progress
            </button>
          </div>
        )}
        <div className="flex gap-3 flex-wrap">
          <button
            onClick={() => startMutation.mutate()}
            disabled={!canStart || isTransitioning}
            className="px-4 py-2 bg-green-600 text-white rounded hover:bg-green-700 disabled:opacity-50 disabled:cursor-not-allowed transition inline-flex items-center gap-2"
          >
            {isStackBusy && stackJob?.action === 'Start' && <Loader2 className="h-4 w-4 animate-spin" />}
            {isStackBusy && stackJob?.action === 'Start' ? 'Starting...' : 'Start'}
          </button>
          <button
            onClick={() => stopMutation.mutate()}
            disabled={!canStop || isTransitioning}
            className="px-4 py-2 bg-red-600 text-white rounded hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition inline-flex items-center gap-2"
          >
            {isStackBusy && stackJob?.action === 'Stop' && <Loader2 className="h-4 w-4 animate-spin" />}
            {isStackBusy && stackJob?.action === 'Stop' ? 'Stopping...' : 'Stop'}
          </button>
          <button
            onClick={() => restartMutation.mutate()}
            disabled={!canRestart || isTransitioning}
            className="px-4 py-2 bg-blue-600 text-white rounded hover:bg-blue-700 disabled:opacity-50 disabled:cursor-not-allowed transition inline-flex items-center gap-2"
          >
            {isStackBusy && stackJob?.action === 'Restart' && <Loader2 className="h-4 w-4 animate-spin" />}
            {isStackBusy && stackJob?.action === 'Restart' ? 'Restarting...' : 'Restart'}
          </button>
          <button
            onClick={() => dbMaintenanceMutation.mutate()}
            disabled={!canDbMaintenance || isTransitioning}
            className="px-4 py-2 bg-indigo-600 text-white rounded hover:bg-indigo-700 disabled:opacity-50 disabled:cursor-not-allowed transition inline-flex items-center gap-2"
            title="Run only the database (world/auth stopped) so migrations can be performed without interruptions"
          >
            {isStackBusy && stackJob?.action === 'StartDatabase' && <Loader2 className="h-4 w-4 animate-spin" />}
            {isStackBusy && stackJob?.action === 'StartDatabase' ? 'Starting DB...' : 'DB Maintenance'}
          </button>

          {isArmoryBusy ? (
            <button
              disabled
              className="px-4 py-2 bg-teal-600 text-white rounded opacity-80 cursor-not-allowed inline-flex items-center gap-2"
              title={armoryJob?.message ?? 'Armory operation in progress'}
            >
              <Loader2 className="h-4 w-4 animate-spin" />
              {armoryJob?.message ?? 'Working…'}
            </button>
          ) : stack.armoryRunning ? (
            <button
              onClick={() => armoryStopMutation.mutate()}
              disabled={isArmoryPending}
              className="px-4 py-2 bg-amber-600 text-white rounded hover:bg-amber-700 disabled:opacity-50 disabled:cursor-not-allowed transition"
              title="Stop the armory web app for this stack"
            >
              {armoryStopMutation.isPending ? 'Stopping...' : 'Stop Armory'}
            </button>
          ) : (
            <button
              onClick={() => armoryStartMutation.mutate()}
              disabled={!canStartArmory || isArmoryPending}
              className="px-4 py-2 bg-teal-600 text-white rounded hover:bg-teal-700 disabled:opacity-50 disabled:cursor-not-allowed transition"
              title="Build (if needed) and start the armory web app for this stack (starts the database first if it isn't running)"
            >
              {armoryStartMutation.isPending ? 'Starting Armory...' : 'Start Armory'}
            </button>
          )}
          {stack.armoryRunning && armoryUrl && (
            <a
              href={armoryUrl}
              target="_blank"
              rel="noreferrer"
              className="px-4 py-2 border border-teal-600 text-teal-700 rounded hover:bg-teal-50 transition"
            >
              Open Armory ↗
            </a>
          )}

          <div className="flex-1"></div>
          <button
            onClick={() => checkUpdatesMutation.mutate()}
            disabled={checkUpdatesMutation.isPending}
            className="px-4 py-2 border border-blue-300 text-blue-700 rounded hover:bg-blue-50 disabled:opacity-50 disabled:cursor-not-allowed transition"
            title="Check for updates to this stack"
          >
            {checkUpdatesMutation.isPending ? 'Checking...' : 'Check for Updates'}
          </button>
          <button
            onClick={() => setShowEditModal(true)}
            disabled={stack.status === StackStatus.Building}
            className="px-4 py-2 border border-blue-300 text-blue-700 rounded hover:bg-blue-50 disabled:opacity-50 disabled:cursor-not-allowed transition"
          >
            Edit Configuration
          </button>
          <button
            onClick={() => setShowRebuildDialog(true)}
            disabled={stack.status === StackStatus.Building}
            className="px-4 py-2 border border-amber-300 text-amber-700 rounded hover:bg-amber-50 disabled:opacity-50 disabled:cursor-not-allowed transition"
          >
            {rebuildMutation.isPending ? 'Starting Rebuild...' : 'Rebuild'}
          </button>
          <button
            onClick={() => setShowDeleteConfirm(true)}
            disabled={deleteMutation.isPending}
            className="px-4 py-2 border border-red-300 text-red-700 rounded hover:bg-red-50 disabled:opacity-50 disabled:cursor-not-allowed transition"
          >
            Delete
          </button>
        </div>
        {isStackBusy && stackJob && (
          <p className="mt-2 text-sm text-gray-600 inline-flex items-center gap-2">
            <Loader2 className="h-4 w-4 animate-spin" />
            {stackJob.message}
          </p>
        )}
        {stackJob && !stackJob.isRunning && stackJob.phase === 'Failed' && (
          <p className="mt-2 text-sm text-red-600">
            {stackJob.message} {stackJob.error ?? ''}
          </p>
        )}
        {armoryJob && !armoryJob.isRunning && armoryJob.phase === 'Failed' && (
          <p className="mt-2 text-sm text-red-600">
            Armory {armoryJob.action.toLowerCase()} failed: {armoryJob.error ?? 'unknown error'}
          </p>
        )}
      </div>

      {/* Tabs Navigation (grouped) */}
      {(() => {
        const activeGroup =
          TAB_GROUPS.find((group) => group.tabs.some((tab) => tab.id === activeTab)) ?? TAB_GROUPS[0]
        return (
          <div className="mb-6">
            <div className="border-b border-gray-200">
              <nav className="flex flex-wrap gap-6">
                {TAB_GROUPS.map((group) => {
                  const isActive = group.id === activeGroup.id
                  return (
                    <button
                      key={group.id}
                      onClick={() => selectTab(group.tabs[0].id)}
                      className={`pb-3 px-1 border-b-2 font-medium text-sm transition-colors ${
                        isActive
                          ? 'border-blue-600 text-blue-600'
                          : 'border-transparent text-gray-500 hover:text-gray-700 hover:border-gray-300'
                      }`}
                    >
                      {group.label}
                    </button>
                  )
                })}
              </nav>
            </div>

            {/* Secondary sub-tab row for multi-tab groups */}
            {activeGroup.tabs.length > 1 && (
              <nav className="mt-3 flex flex-wrap gap-2">
                {activeGroup.tabs.map((tab) => {
                  const isActive = tab.id === activeTab
                  return (
                    <button
                      key={tab.id}
                      onClick={() => selectTab(tab.id)}
                      className={`rounded-full px-3 py-1.5 text-sm font-medium transition-colors ${
                        isActive
                          ? 'bg-blue-600 text-white'
                          : 'bg-gray-100 text-gray-600 hover:bg-gray-200 hover:text-gray-800'
                      }`}
                    >
                      {tab.label}
                    </button>
                  )
                })}
              </nav>
            )}
          </div>
        )
      })()}

      {isArmoryTab && (
        <div className="mb-6 space-y-3">
          {armoryRebuildError && (
            <div className="rounded-md bg-red-50 p-3 text-sm text-red-700">{armoryRebuildError}</div>
          )}
          <ArmoryRebuildBanner
            rebuildPending={armoryAssetsInfoQuery.data?.staticRebuildPending}
            onRebuildError={setArmoryRebuildError}
          />
        </div>
      )}

      {/* Tab Content */}
      {activeTab === 'accounts' && (
        <AccountsTab stackId={stackId!} />
      )}

      {activeTab === 'characters' && (
        <CharactersTab stackId={stackId!} />
      )}

      {activeTab === 'realms' && (
        <div className="mb-8">
          <h2 className="text-xl font-semibold mb-4">Realms</h2>
          <RealmsTab stackId={stackId!} />
        </div>
      )}

      {activeTab === 'patches' && (
        <PatchesTab stackId={stackId!} />
      )}

      {activeTab === 'addons' && (
        <div className="mb-8">
          <AddonsManager stackId={stackId!} />
        </div>
      )}

      {activeTab === 'lua' && (
        <div className="mb-8">
          <h2 className="text-xl font-semibold mb-4">Lua Scripts</h2>
          <LuaScriptsTab stackId={stackId!} />
        </div>
      )}

      {activeTab === 'config' && (
        <div className="mb-8">
          <h2 className="text-xl font-semibold mb-4">Server Configuration</h2>
          <ServerConfigTab stackId={stackId!} />
        </div>
      )}

      {activeTab === 'env' && (
        <EnvironmentVariablesTab stack={stack} />
      )}

      {activeTab === 'client' && (
        <div className="mb-8">
          <h2 className="text-xl font-semibold mb-4">Client Files</h2>
          <ClientTab stackId={stackId!} />
        </div>
      )}

      {activeTab === 'armory' && (
        <div className="mb-8">
          <div className="mb-4">
            <h2 className="text-xl font-semibold">Armory Data / Assets</h2>
            <p className="mt-1 text-sm text-gray-600">
              Upload this stack&rsquo;s armory model-viewer dataset and static web assets. Each stack keeps
              its own armory data, so it is uploaded per stack.
            </p>
          </div>
          <ArmoryDataManager stackId={stackId!} />
        </div>
      )}

      {activeTab === 'armory-styling' && (
        <div className="mb-8">
          <div className="mb-4">
            <h2 className="text-xl font-semibold">Armory Styling</h2>
            <p className="mt-1 text-sm text-gray-600">
              Choose this stack&rsquo;s armory theme template and optional advanced colors or wallpaper.
            </p>
          </div>
          <ArmoryStylingTab stackId={stackId!} />
        </div>
      )}

      {activeTab === 'armory-layout' && (
        <div className="mb-8">
          <ArmoryLayoutTab
            stackId={stackId!}
            siteName={stack.stackName}
            moduleIds={stack.configuration.moduleIds}
            realmCount={realms?.length ?? 1}
          />
        </div>
      )}

      {activeTab === 'armory-email' && (
        <div className="mb-8">
          <ArmoryEmailTab stack={stack} />
        </div>
      )}

      {activeTab === 'modules' && (
        <div className="mb-8">
          <StackModulesTab stack={stack} />
        </div>
      )}

      {activeTab === 'revisions' && (
        <div className="mb-8">
          <h2 className="text-xl font-semibold mb-4">Revisions</h2>
          <RevisionsTab stackId={stackId!} />
        </div>
      )}

      {activeTab === 'launcher' && (
        <div className="mb-8">
          <h2 className="text-xl font-semibold mb-4">Launcher Profile</h2>
          <LauncherProfileTab stackId={stackId!} />
        </div>
      )}

      {activeTab === 'news' && (
        <div className="mb-8">
          <StackNewsTab stackId={stackId!} />
        </div>
      )}

      {activeTab === 'docker' && (
        <div className="mb-8">
          <DockerTab stackId={stackId!} />
        </div>
      )}

      {activeTab === 'logs' && (
        <div className="mb-8">
          <h2 className="text-xl font-semibold mb-4">Container Logs</h2>
          {stack.containers.length === 0 ? (
            <div className="bg-gray-50 border border-gray-200 rounded-lg p-6 text-center">
              <p className="text-gray-600">No containers running. Start the stack to see containers.</p>
            </div>
          ) : (
            <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
              {stack.containers.map((container) => (
                <div 
                  key={container.name} 
                  onClick={() => navigate(`/stacks/${stackId}/containers/${encodeURIComponent(container.name)}/logs`)}
                  className="bg-white border border-gray-200 rounded-lg p-4 shadow-sm cursor-pointer hover:border-blue-500 hover:shadow-md transition-all"
                >
                  <div className="flex items-start justify-between mb-2">
                    <h3 className="font-medium text-gray-900 text-sm truncate" title={container.name}>
                      {container.name.split('-').pop() || container.name}
                    </h3>
                    <span className="text-lg ml-2" title={formatHealthLabel(container.health || 'unknown')}>
                      {getHealthIcon(container.health)}
                    </span>
                  </div>
                  <div className="space-y-1 text-sm">
                    <div className="flex items-center gap-2">
                      <span className="text-gray-500">Status:</span>
                      <span className={`font-medium ${getContainerStatusColor(container.status)}`}>
                        {container.status}
                      </span>
                    </div>
                    <div className="flex items-center gap-2">
                      <span className="text-gray-500">Started:</span>
                      <span className="text-gray-700">
                        {new Date(container.startedAt).toLocaleTimeString()}
                      </span>
                    </div>
                  </div>
                  <div className="mt-3 text-xs text-blue-600 font-medium">
                    Click to view logs →
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      )}

      {activeTab === 'overview' && (
        <>
      {diskUsage?.isWarning && (
        <div className="mb-8 rounded-lg border border-amber-300 bg-amber-50 p-4">
          <h2 className="font-semibold text-amber-950">Docker disk space is running low</h2>
          <p className="mt-1 text-sm text-amber-900">
            The Docker engine is {diskUsage.usedPercent.toFixed(1)}% full ({formatBytes(diskUsage.usedBytes)} of{' '}
            {formatBytes(diskUsage.totalBytes)} used). Use the <button type="button" onClick={() => selectTab('docker')} className="font-medium underline">Docker tab</button> to review unused images and reclaim disk space.
          </p>
        </div>
      )}

      <div className="mb-8">
        <ExternalReconnectPanel stack={stack} />
      </div>

      {/* Module setup warnings */}
      <ModuleSetupWarnings stack={stack} />

      {stack.configuration.armoryAccounts?.useEmailConfirmation &&
        !stack.configuration.armoryAccounts.emailConfigured && (
        <div className="mb-8 rounded-lg border border-amber-300 bg-amber-50 p-4">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <div>
              <h2 className="font-semibold text-amber-950">Complete email setup</h2>
              <p className="mt-1 text-sm text-amber-900">
                Email confirmation is enabled for this stack, but SMTP is not configured yet. Armory
                registration stays disabled until email delivery is set up.
              </p>
            </div>
            <button
              type="button"
              onClick={() => selectTab('armory-email')}
              className="shrink-0 rounded-md bg-amber-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-amber-700"
            >
              Configure email
            </button>
          </div>
        </div>
      )}

      {/* Missing per-stack data prompt (base client / armory dataset) */}
      {(clientDataMissing || armoryDataMissing) && (
        <div className="mb-8">
          <div className="bg-amber-50 border border-amber-200 rounded-lg p-6">
            <h2 className="text-xl font-semibold text-amber-900 mb-2">Data needs uploading</h2>
            <p className="text-sm text-amber-800 mb-4">
              This stack is missing data that must be uploaded per stack. Upload it so the launcher and
              armory work correctly.
            </p>
            <div className="space-y-3 text-sm">
              {clientDataMissing && (
                <div className="flex items-start justify-between gap-3">
                  <div className="flex items-start gap-2">
                    <span className="text-amber-600 mt-0.5">●</span>
                    <div>
                      <div className="font-medium text-amber-900">Base client not uploaded</div>
                      <div className="text-amber-700 text-xs mt-1">
                        The client container has no base WoW client to serve to the launcher.
                      </div>
                    </div>
                  </div>
                  <button
                    onClick={() => selectTab('client')}
                    className="shrink-0 px-3 py-1.5 text-sm bg-amber-600 text-white rounded hover:bg-amber-700 transition"
                  >
                    Upload client
                  </button>
                </div>
              )}
              {armoryDataMissing && (
                <div className="flex items-start justify-between gap-3">
                  <div className="flex items-start gap-2">
                    <span className="text-amber-600 mt-0.5">●</span>
                    <div>
                      <div className="font-medium text-amber-900">Armory data not uploaded</div>
                      <div className="text-amber-700 text-xs mt-1">
                        The 3D model-viewer dataset is missing, so the armory viewer is disabled.
                      </div>
                    </div>
                  </div>
                  <button
                    onClick={() => selectTab('armory')}
                    className="shrink-0 px-3 py-1.5 text-sm bg-amber-600 text-white rounded hover:bg-amber-700 transition"
                  >
                    Upload armory data
                  </button>
                </div>
              )}
            </div>
          </div>
        </div>
      )}

      {/* Updates Available Section */}
      {stack.updateStatus?.hasUpdates && (
        <div className="mb-8">
          <div className="bg-amber-50 border border-amber-200 rounded-lg p-6">
            <div className="flex items-start justify-between mb-4">
              <div className="flex-1">
                <h2 className="text-xl font-semibold text-amber-900 mb-2">Updates Available</h2>
                <p className="text-sm text-amber-800 mb-3">
                  New versions are available for this stack. Update to get the latest features and bug fixes.
                </p>
                {/* CI Build Status Badge */}
                {stack.updateStatus.latestCoreBuildStatus && (
                  <div className="mb-3">
                    <CiBuildStatusBadge 
                      status={stack.updateStatus.latestCoreBuildStatus} 
                      showDetails={false}
                    />
                  </div>
                )}
              </div>
              <button
                onClick={() => setShowUpdateDialog(true)}
                disabled={stack.status === StackStatus.Building}
                className="px-3 py-1.5 text-sm bg-amber-600 text-white rounded hover:bg-amber-700 disabled:opacity-50 transition ml-4"
                title={stack.status === StackStatus.Building ? 'Wait for build to finish' : 'Update stack'}
              >
                Update Stack
              </button>
            </div>

            <div className="space-y-3 text-sm">
              {stack.updateStatus.isCoreOutdated && (
                <div className="flex items-start gap-2">
                  <span className="text-amber-600 mt-0.5">●</span>
                  <div className="flex-1">
                    <div className="font-medium text-amber-900">AzerothCore Server</div>
                    <div className="text-amber-700 text-xs font-mono mt-1">
                      {formatSha(stack.updateStatus.currentCoreSha)} → {formatSha(stack.updateStatus.latestCoreSha)}
                    </div>
                  </div>
                </div>
              )}

              {stack.updateStatus.outdatedModules.map((module) => (
                <div key={module.moduleId} className="flex items-start gap-2">
                  <span className="text-amber-600 mt-0.5">●</span>
                  <div className="flex-1">
                    <div className="font-medium text-amber-900">{module.moduleName}</div>
                    <div className="text-amber-700 text-xs font-mono mt-1">
                      {formatSha(module.currentCommitSha)} → {formatSha(module.latestCommitSha)}
                    </div>
                  </div>
                </div>
              ))}

              {stack.updateStatus.lastCheckedAt && (
                <div className="text-xs text-amber-700 pt-2 border-t border-amber-200 flex items-center justify-between">
                  <span>Last checked: {formatRelativeTime(stack.updateStatus.lastCheckedAt)}</span>
                  {checkUpdatesMutation.isPending && (
                    <span className="flex items-center gap-1">
                      <span className="inline-block w-2 h-2 bg-blue-500 rounded-full animate-pulse"></span>
                      Checking...
                    </span>
                  )}
                </div>
              )}
            </div>
          </div>
        </div>
      )}

      {/* Containers / Services Section */}
      <div className="mb-8">
        <h2 className="text-xl font-semibold mb-4">Containers</h2>
        <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-4">
          {stack.services.map((svc) => {
            const running = isServiceRunning(svc)
            const isArmoryService = svc.service === 'frontend-armory'
            // The armory runs as a detached background job, so its busy state comes from the job hook
            // (not just this request's mutation) and survives page refreshes.
            const busy =
              (serviceActionMutation.isPending && pendingService === svc.service) ||
              (isArmoryService && isArmoryBusy)
            const actionsDisabled =
              pendingService !== null ||
              stack.status === StackStatus.Building ||
              (isArmoryService && isArmoryBusy)
            const runAction = (action: StackServiceAction) =>
              serviceActionMutation.mutate({ service: svc.service, action })
            const actionBtn =
              'inline-flex items-center gap-1 rounded px-2.5 py-1 text-xs font-medium disabled:opacity-40 disabled:cursor-not-allowed transition'
            return (
              <div key={svc.service} className="bg-white border border-gray-200 rounded-lg p-4 shadow-sm flex flex-col">
                <div className="flex items-start justify-between mb-1">
                  <div className="min-w-0">
                    <h3 className="font-medium text-gray-900 text-sm truncate" title={svc.containerName || svc.service}>
                      {svc.displayName}
                    </h3>
                    <span className="text-[11px] font-mono text-gray-400">{svc.service}</span>
                  </div>
                    <span className="text-lg ml-2" title={formatHealthLabel(resolveServiceHealth(svc))}>
                      {getHealthIcon(resolveServiceHealth(svc))}
                  </span>
                </div>

                <div className="mb-3 flex items-center gap-2 text-sm">
                  <span className={`inline-block h-2 w-2 rounded-full ${serviceStateDot(svc.state)}`} />
                  <span className="font-medium text-gray-700">{serviceStateLabel(svc.state)}</span>
                  {running && svc.startedAt && (
                    <span className="text-xs text-gray-400">· since {new Date(svc.startedAt).toLocaleTimeString()}</span>
                  )}
                </div>

                <div className="mt-auto flex flex-wrap gap-2">
                  {busy && (
                    <span className="inline-flex items-center gap-1 text-xs text-gray-500">
                      <Loader2 className="h-3.5 w-3.5 animate-spin" />{' '}
                      {isArmoryService && armoryJob?.isRunning ? armoryJob.message : 'Working…'}
                    </span>
                  )}
                  {!busy && running && (
                    <>
                      <button
                        onClick={() => runAction('stop')}
                        disabled={actionsDisabled}
                        className={`${actionBtn} bg-red-50 text-red-700 hover:bg-red-100`}
                        title="Stop this container"
                      >
                        <Square className="h-3.5 w-3.5" /> Stop
                      </button>
                      <button
                        onClick={() => runAction('restart')}
                        disabled={actionsDisabled}
                        className={`${actionBtn} bg-blue-50 text-blue-700 hover:bg-blue-100`}
                        title="Restart this container"
                      >
                        <RotateCw className="h-3.5 w-3.5" /> Restart
                      </button>
                    </>
                  )}
                  {!busy && !running && (
                    <button
                      onClick={() => runAction('start')}
                      disabled={actionsDisabled}
                      className={`${actionBtn} bg-green-50 text-green-700 hover:bg-green-100`}
                      title="Start this container"
                    >
                      <Play className="h-3.5 w-3.5" /> Start
                    </button>
                  )}
                  {/* Database, auth, and world servers keep stateful/runtime data; recreating them is
                      needless downtime and risk, so Rebuild & Restart is intentionally omitted. */}
                  {!busy
                    && svc.service !== 'ac-database'
                    && svc.service !== 'ac-authserver'
                    && svc.service !== 'ac-worldserver' && (
                    <button
                      onClick={() => runAction('recreate')}
                      disabled={actionsDisabled}
                      className={`${actionBtn} bg-amber-50 text-amber-700 hover:bg-amber-100`}
                      title="Recreate the container from its current image and the latest generated config"
                    >
                      <Hammer className="h-3.5 w-3.5" /> Rebuild &amp; Restart
                    </button>
                  )}
                </div>

                {svc.containerName && (
                  <button
                    onClick={() => navigate(`/stacks/${stackId}/containers/${encodeURIComponent(svc.containerName)}/logs`)}
                    className="mt-3 self-start text-xs font-medium text-blue-600 hover:text-blue-800"
                  >
                    View logs →
                  </button>
                )}
              </div>
            )
          })}
        </div>
      </div>

      {/* Configuration Section */}
      <div className="mb-8">
        <h2 className="text-xl font-semibold mb-4">Configuration</h2>
        <div className="bg-white border border-gray-200 rounded-lg p-6 space-y-6">
          {/* Realmlist host override (propagates to the launcher client on save) */}
          {stackId && <RealmlistOverrideField stackId={stackId} />}

          {/* Armory + client web access (host ports + publish bind interface) */}
          {stackId && <ArmoryNetworkField stackId={stackId} />}

          {/* Ports */}
          <div>
            <h3 className="font-medium text-gray-900 mb-2">Server Ports</h3>
            <div className="grid grid-cols-2 md:grid-cols-4 gap-4 text-sm">
              <div>
                <span className="text-gray-500">Auth Server:</span>
                <span className="ml-2 font-mono text-gray-900">{stack.configuration.ports.authServer}</span>
              </div>
              <div>
                <span className="text-gray-500">World Server:</span>
                <span className="ml-2 font-mono text-gray-900">{stack.configuration.ports.worldServer}</span>
              </div>
              <div>
                <span className="text-gray-500">SOAP:</span>
                <span className="ml-2 font-mono text-gray-900">{stack.configuration.ports.soapPort}</span>
              </div>
              <div>
                <span className="text-gray-500">Database:</span>
                <span className="ml-2 font-mono text-gray-900">{stack.configuration.database.port}</span>
              </div>
            </div>
          </div>

          {/* Advanced */}
          <div>
            <h3 className="font-medium text-gray-900 mb-2">Advanced Settings</h3>
            <div className="grid grid-cols-2 gap-4 text-sm">
              <div>
                <span className="text-gray-500">Max Players:</span>
                <span className="ml-2 text-gray-900">{stack.configuration.advanced.maxPlayers}</span>
              </div>
              <div>
                <span className="text-gray-500">Realm Name:</span>
                <span className="ml-2 text-gray-900">{stack.configuration.advanced.realmName}</span>
              </div>
            </div>
          </div>

          {/* SOAP Credentials Recovery */}
          {stack.isAdminAccountInitialized && (
            <div>
              <h3 className="font-medium text-gray-900 mb-2">SOAP Admin Credentials</h3>
              <div className="space-y-2 text-sm">
                {soapCredentialsQuery.data ? (
                  <>
                    <div className="flex items-center gap-2 bg-gray-50 border border-gray-200 rounded-md px-3 py-2">
                      <span className="text-gray-500 w-20 shrink-0">Username</span>
                      <code className="flex-1 font-mono text-gray-900">{soapCredentialsQuery.data.username}</code>
                      <button
                        onClick={async () => {
                          await navigator.clipboard.writeText(soapCredentialsQuery.data!.username)
                          setSoapCopied('username')
                          setTimeout(() => setSoapCopied(null), 2000)
                        }}
                        className="text-gray-400 hover:text-gray-600 transition-colors"
                        title="Copy username"
                      >
                        <Copy className="h-4 w-4" />
                      </button>
                      {soapCopied === 'username' && <span className="text-xs text-green-600">Copied!</span>}
                    </div>
                    <div className="flex items-center gap-2 bg-gray-50 border border-gray-200 rounded-md px-3 py-2">
                      <span className="text-gray-500 w-20 shrink-0">Password</span>
                      <code className="flex-1 font-mono text-gray-900 break-all">
                        {soapCredsVisible ? soapCredentialsQuery.data.password : '•'.repeat(32)}
                      </code>
                      <button
                        onClick={() => setSoapCredsVisible(v => !v)}
                        className="text-gray-400 hover:text-gray-600 transition-colors"
                        title={soapCredsVisible ? 'Hide password' : 'Reveal password'}
                      >
                        {soapCredsVisible ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
                      </button>
                      <button
                        onClick={async () => {
                          await navigator.clipboard.writeText(soapCredentialsQuery.data!.password)
                          setSoapCopied('password')
                          setTimeout(() => setSoapCopied(null), 2000)
                        }}
                        className="text-gray-400 hover:text-gray-600 transition-colors"
                        title="Copy password"
                      >
                        <Copy className="h-4 w-4" />
                      </button>
                      {soapCopied === 'password' && <span className="text-xs text-green-600">Copied!</span>}
                    </div>
                  </>
                ) : (
                  <p className="text-gray-500 text-sm italic">Loading credentials…</p>
                )}
              </div>
            </div>
          )}

          {/* Database Credentials */}
          <div>
            <h3 className="font-medium text-gray-900 mb-2">Database Credentials</h3>
            <div className="space-y-2 text-sm">
              {[
                { label: 'Host', value: 'localhost', key: 'host' as const },
                { label: 'Port', value: String(stack.configuration.database.port), key: 'port' as const },
                { label: 'User', value: 'root', key: 'user' as const },
              ].map(({ label, value, key }) => (
                <div key={key} className="flex items-center gap-2 bg-gray-50 border border-gray-200 rounded-md px-3 py-2">
                  <span className="text-gray-500 w-20 shrink-0">{label}</span>
                  <code className="flex-1 font-mono text-gray-900">{value}</code>
                  <button
                    onClick={async () => {
                      await navigator.clipboard.writeText(value)
                      setDbCopied(key)
                      setTimeout(() => setDbCopied(null), 2000)
                    }}
                    className="text-gray-400 hover:text-gray-600 transition-colors"
                    title={`Copy ${label.toLowerCase()}`}
                  >
                    <Copy className="h-4 w-4" />
                  </button>
                  {dbCopied === key && <span className="text-xs text-green-600">Copied!</span>}
                </div>
              ))}
              <div className="flex items-center gap-2 bg-gray-50 border border-gray-200 rounded-md px-3 py-2">
                <span className="text-gray-500 w-20 shrink-0">Password</span>
                <code className="flex-1 font-mono text-gray-900 break-all">
                  {dbCredsVisible
                    ? (dbCredentialsQuery.data?.password ?? (dbCredentialsQuery.isFetching ? 'Loading…' : ''))
                    : '•'.repeat(32)}
                </code>
                <button
                  onClick={() => {
                    setDbCredsRequested(true)
                    setDbCredsVisible(v => !v)
                  }}
                  className="text-gray-400 hover:text-gray-600 transition-colors"
                  title={dbCredsVisible ? 'Hide password' : 'Reveal password'}
                >
                  {dbCredsVisible ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
                </button>
                <button
                  onClick={async () => {
                    setDbCredsRequested(true)
                    const pw = dbCredentialsQuery.data?.password
                      ?? (await stackApi.getDatabaseCredentials(stackId!)).data.password
                    await navigator.clipboard.writeText(pw)
                    setDbCopied('password')
                    setTimeout(() => setDbCopied(null), 2000)
                  }}
                  className="text-gray-400 hover:text-gray-600 transition-colors"
                  title="Copy password"
                >
                  <Copy className="h-4 w-4" />
                </button>
                {dbCopied === 'password' && <span className="text-xs text-green-600">Copied!</span>}
              </div>
            </div>
          </div>

          {/* Modules */}
          <div>
            <h3 className="font-medium text-gray-900 mb-2">Modules</h3>
            {stack.configuration.moduleIds.length === 0 ? (
              <p className="text-sm text-gray-500">No modules installed</p>
            ) : (
              <div className="flex flex-wrap gap-2">
                {stack.configuration.moduleIds.map((moduleId) => (
                  <span key={moduleId} className="px-3 py-1 bg-blue-50 text-blue-700 rounded-full text-sm">
                    {moduleId}
                  </span>
                ))}
              </div>
            )}
          </div>

          {/* Per-service Env Vars */}
          {stack.configuration.advanced.serviceEnvVars &&
            Object.values(stack.configuration.advanced.serviceEnvVars).some(
              (bucket) => bucket && Object.keys(bucket).length > 0,
            ) && (
              <div>
                <h3 className="font-medium text-gray-900 mb-2">Environment Variables</h3>
                <div className="space-y-3">
                  {Object.entries(stack.configuration.advanced.serviceEnvVars)
                    .filter(([, bucket]) => bucket && Object.keys(bucket).length > 0)
                    .map(([service, bucket]) => (
                      <div key={service}>
                        <div className="mb-1 text-xs font-semibold uppercase text-gray-500">{service}</div>
                        <div className="space-y-1 rounded bg-gray-50 p-3 font-mono text-sm">
                          {Object.entries(bucket).map(([key, value]) => (
                            <div key={key}>
                              <span className="text-gray-600">{key}</span>=
                              <span className="text-gray-900">{value}</span>
                            </div>
                          ))}
                        </div>
                      </div>
                    ))}
                </div>
              </div>
            )}
        </div>
      </div>
        </>
      )}

      {/* Delete Confirmation Modal */}
      {showDeleteConfirm && (
        <div className="fixed inset-0 bg-black bg-opacity-50 flex items-center justify-center z-50">
          <div className="bg-white rounded-lg p-6 max-w-md mx-4">
            <h3 className="text-xl font-semibold mb-4">Delete Stack?</h3>
            <p className="text-gray-600 mb-6">
              Are you sure you want to delete <strong>{stack.stackName}</strong>? 
              This will remove all containers, images, and build files. This action cannot be undone.
            </p>
            <div className="flex gap-3 justify-end">
              <button
                onClick={() => setShowDeleteConfirm(false)}
                className="px-4 py-2 border border-gray-300 rounded hover:bg-gray-50 transition"
              >
                Cancel
              </button>
              <button
                onClick={() => {
                  deleteMutation.mutate()
                  setShowDeleteConfirm(false)
                }}
                disabled={deleteMutation.isPending}
                className="px-4 py-2 bg-red-600 text-white rounded hover:bg-red-700 disabled:opacity-50 transition"
              >
                {deleteMutation.isPending ? 'Deleting...' : 'Delete Stack'}
              </button>
            </div>
          </div>
        </div>
      )}

      {/* Edit Configuration Modal */}
      {showEditModal && (
        <EditStackConfigModal
          stack={stack}
          onClose={() => setShowEditModal(false)}
        />
      )}

      {/* Update Stack Dialog */}
      {showUpdateDialog && stack.updateStatus && (
        <UpdateStackDialog
          stackName={stack.stackName}
          updateStatus={stack.updateStatus}
          onConfirm={(mode) => updateStackMutation.mutate(mode)}
          onCancel={() => setShowUpdateDialog(false)}
          isUpdating={updateStackMutation.isPending}
        />
      )}

      {/* Rebuild Stack Dialog */}
      {showRebuildDialog && (
        <RebuildStackDialog
          stackName={stack.stackName}
          onConfirm={(mode) => rebuildMutation.mutate(mode)}
          onCancel={() => setShowRebuildDialog(false)}
          isRebuilding={rebuildMutation.isPending}
        />
      )}
    </div>
  )
}
