import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import {
  AlertTriangle,
  CheckCircle2,
  ChevronDown,
  ChevronUp,
  HelpCircle,
  Loader2,
  RefreshCw,
  Shield,
  XCircle,
} from 'lucide-react'
import { CloudSecurityGroupGuideDialog } from '@/components/stacks/CloudSecurityGroupGuideDialog'
import { VpcSecurityProfileCard, VpcSecurityRolesCard } from '@/components/stacks/VpcSecurityRolesCard'
import { resolvePublicAdminSourceCidr } from '@/lib/public-ip'
import { cloudApi, stackApi, systemApi } from '@/services/api'
import { apiErrorMessage } from '@/lib/utils'
import type {
  RemotePrerequisiteCheckDto,
  StackDetailsDto,
  VpcSecurityCheckDto,
  VpcSecurityCheckStatus,
} from '@/types/stack.types'
import { CloudProvider, DeploymentTarget } from '@/types/stack.types'

function StatusIcon({ status }: { status: VpcSecurityCheckStatus }) {
  switch (status) {
    case 'ok':
      return <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-green-600" aria-hidden="true" />
    case 'warning':
      return <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-600" aria-hidden="true" />
    case 'error':
      return <XCircle className="mt-0.5 h-4 w-4 shrink-0 text-red-600" aria-hidden="true" />
    default:
      return <HelpCircle className="mt-0.5 h-4 w-4 shrink-0 text-gray-400" aria-hidden="true" />
  }
}

function categoryLabel(category: string) {
  switch (category) {
    case 'host-firewall':
      return 'Host firewall (ufw)'
    case 'docker-bind':
      return 'Docker bind policy'
    case 'cloud-sg':
      return 'Cloud security group'
    default:
      return category
  }
}

function VpcSecurityStatusCard({
  checks,
  ufwActive,
  ufwInstalled,
  ufwStatusSummary,
}: {
  checks: VpcSecurityCheckDto[]
  ufwActive: boolean
  ufwInstalled: boolean
  ufwStatusSummary?: string
}) {
  if (checks.length === 0) {
    return null
  }

  const grouped = checks.reduce<Record<string, VpcSecurityCheckDto[]>>((acc, check) => {
    const key = check.category || 'other'
    acc[key] ??= []
    acc[key].push(check)
    return acc
  }, {})

  return (
    <div className="space-y-3 rounded-md border border-gray-200 bg-white p-3">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-xs font-semibold text-gray-800">Live security status</p>
        <p className="text-[11px] text-gray-500">
          ufw:{' '}
          {!ufwInstalled ? (
            <span className="text-amber-700">not installed</span>
          ) : ufwActive ? (
            <span className="text-green-700">active</span>
          ) : (
            <span className="text-amber-700">inactive</span>
          )}
        </p>
      </div>
      {ufwStatusSummary && (
        <p className="text-[11px] text-gray-500">{ufwStatusSummary}</p>
      )}
      {Object.entries(grouped).map(([category, items]) => (
        <div key={category}>
          <p className="mb-1.5 text-xs font-medium text-gray-700">{categoryLabel(category)}</p>
          <ul className="space-y-1.5">
            {items.map((check) => (
              <li
                key={`${check.category}-${check.name}-${check.port ?? 'na'}`}
                className="flex items-start gap-2 rounded border border-gray-100 bg-gray-50 px-2 py-1.5 text-xs"
              >
                <StatusIcon status={check.status} />
                <span className="min-w-0 flex-1 text-gray-800">
                  <span className="font-medium">{check.name}</span>
                  {check.port != null && (
                    <span className="ml-1 font-mono text-gray-600">:{check.port}</span>
                  )}
                  {check.message ? ` — ${check.message}` : ''}
                </span>
              </li>
            ))}
          </ul>
        </div>
      ))}
    </div>
  )
}

export default function ExternalVpcSecurityPanel({ stack }: { stack: StackDetailsDto }) {
  const isExternal = stack.configuration?.deployment?.target === DeploymentTarget.External
  const [syncMessage, setSyncMessage] = useState<string | null>(null)
  const [syncSuccess, setSyncSuccess] = useState<boolean | null>(null)
  const [syncSteps, setSyncSteps] = useState<RemotePrerequisiteCheckDto[] | null>(null)
  const [sgGuideOpen, setSgGuideOpen] = useState(false)
  const [cloudSgOpen, setCloudSgOpen] = useState(false)
  const [connectionId, setConnectionId] = useState('')
  const [adminSourceCidr, setAdminSourceCidr] = useState('')
  const [instanceId, setInstanceId] = useState('')
  const [awsRegion, setAwsRegion] = useState('')
  const [cloudSgMessage, setCloudSgMessage] = useState<string | null>(null)
  const [cloudSgSuccess, setCloudSgSuccess] = useState<boolean | null>(null)

  const deployment = stack.configuration?.deployment
  const remoteHost = deployment?.externalHost?.trim()

  const { data: profile, isLoading: profileLoading } = useQuery({
    queryKey: ['vpc-security-profile', stack.stackId],
    queryFn: async () => (await stackApi.vpcSecurityProfile(stack.stackId)).data,
    enabled: isExternal,
  })

  const {
    data: firewallStatus,
    isLoading: statusLoading,
    isFetching: statusFetching,
    refetch: refetchStatus,
  } = useQuery({
    queryKey: ['vpc-firewall-status', stack.stackId],
    queryFn: async () => (await stackApi.vpcFirewallStatus(stack.stackId)).data,
    enabled: isExternal,
    refetchInterval: 120_000,
  })

  const { data: awsConnections = [] } = useQuery({
    queryKey: ['cloud-connections', 'aws'],
    queryFn: async () => {
      const res = await cloudApi.listConnections()
      return res.data.filter((connection) => connection.provider === CloudProvider.Aws)
    },
    enabled: isExternal,
  })

  const { data: networkInfo } = useQuery({
    queryKey: ['system-network', 'admin-source'],
    queryFn: async () => {
      const res = await systemApi.network()
      const suggestedAdminSourceCidr = await resolvePublicAdminSourceCidr(
        res.data.suggestedAdminSourceCidr,
      )
      return { suggestedAdminSourceCidr }
    },
    enabled: isExternal && cloudSgOpen,
    staleTime: 60_000,
  })

  const syncFirewall = useMutation({
    mutationFn: () => stackApi.syncVpcFirewall(stack.stackId),
    onMutate: () => {
      setSyncSuccess(null)
      setSyncMessage(null)
      setSyncSteps(null)
    },
    onSuccess: (res) => {
      setSyncSuccess(res.data.success)
      setSyncMessage(res.data.message)
      setSyncSteps(res.data.steps ?? null)
      void refetchStatus()
    },
    onError: (err) => {
      setSyncSuccess(false)
      setSyncMessage(
        apiErrorMessage(
          err,
          'Remote firewall sync can take a few minutes. This only changes firewall rules on your remote VPS, not on this PC.',
        ),
      )
      setSyncSteps(null)
    },
  })

  const syncCloudSg = useMutation({
    mutationFn: () =>
      stackApi.syncCloudSecurityGroup(stack.stackId, {
        connectionId,
        adminSourceCidr: adminSourceCidr.trim(),
        instanceId: instanceId.trim() || undefined,
        region: awsRegion.trim() || undefined,
      }),
    onMutate: () => {
      setCloudSgSuccess(null)
      setCloudSgMessage(null)
    },
    onSuccess: (res) => {
      setCloudSgSuccess(res.data.success)
      setCloudSgMessage(res.data.message)
    },
    onError: (err) => {
      setCloudSgSuccess(false)
      setCloudSgMessage(apiErrorMessage(err, 'Failed to apply AWS security group rules.'))
    },
  })

  if (!isExternal) {
    return null
  }

  return (
    <section className="rounded-lg border border-gray-200 bg-white p-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="flex items-start gap-3">
          <Shield className="mt-0.5 h-5 w-5 shrink-0 text-gray-500" aria-hidden="true" />
          <div>
            <h3 className="font-medium text-gray-900">VPC security</h3>
            <p className="mt-1 text-sm text-gray-600">
              Firewall roles for this external stack. Player and web ports must be reachable on the remote
              VPS{remoteHost ? ` (${remoteHost})` : ''}; MySQL and SOAP stay on the VPC interface via Docker
              bind policy.
            </p>
            <p className="mt-2 text-xs text-gray-500">
              <strong>Sync VPC firewall (ufw)</strong> is optional. It SSHs into your remote Linux server and
              configures <em>that machine&apos;s</em> host firewall — it does <strong>not</strong> change
              Windows Firewall or any rules on the PC running this manager. If you only use your cloud
              provider&apos;s security group, open the <strong>Cloud SG guide</strong> and skip sync.
            </p>
          </div>
        </div>
        <div className="flex flex-wrap gap-2">
          <button
            type="button"
            onClick={() => void refetchStatus()}
            disabled={statusFetching}
            className="inline-flex items-center gap-2 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-60"
            title="Re-check ufw and Docker bind policy on the remote host"
          >
            {statusFetching ? (
              <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
            ) : (
              <RefreshCw className="h-3.5 w-3.5" aria-hidden="true" />
            )}
            Check status
          </button>
          <button
            type="button"
            onClick={() => syncFirewall.mutate()}
            disabled={syncFirewall.isPending}
            className={`inline-flex items-center gap-2 rounded-md border px-3 py-1.5 text-xs font-medium disabled:opacity-60 ${
              syncSuccess === true
                ? 'border-green-300 bg-green-50 text-green-800 hover:bg-green-100'
                : 'border-gray-300 bg-white text-gray-700 hover:bg-gray-50'
            }`}
            title="Configure ufw on the remote VPS over SSH (optional)"
          >
            {syncFirewall.isPending ? (
              <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
            ) : syncSuccess === true ? (
              <CheckCircle2 className="h-3.5 w-3.5 text-green-600" aria-hidden="true" />
            ) : (
              <RefreshCw className="h-3.5 w-3.5" aria-hidden="true" />
            )}
            {syncSuccess === true && !syncFirewall.isPending ? 'VPC firewall synced' : 'Sync VPC firewall (ufw)'}
          </button>
          <button
            type="button"
            onClick={() => setSgGuideOpen(true)}
            className="inline-flex items-center gap-2 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50"
          >
            Cloud SG guide
          </button>
        </div>
      </div>

      <div className="mt-4 space-y-4">
        {statusLoading && <p className="text-xs text-gray-500">Checking firewall status…</p>}
        {firewallStatus && (
          <>
            <div
              role="status"
              className={`rounded-md border px-3 py-2 text-xs ${
                firewallStatus.overallHealthy
                  ? 'border-green-200 bg-green-50 text-green-900'
                  : 'border-amber-200 bg-amber-50 text-amber-950'
              }`}
            >
              <p className="flex items-start gap-2 font-medium">
                {firewallStatus.overallHealthy ? (
                  <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-green-600" aria-hidden="true" />
                ) : (
                  <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-700" aria-hidden="true" />
                )}
                <span>{firewallStatus.message}</span>
              </p>
            </div>
            <VpcSecurityStatusCard
              checks={firewallStatus.checks}
              ufwActive={firewallStatus.ufwActive}
              ufwInstalled={firewallStatus.ufwInstalled}
              ufwStatusSummary={firewallStatus.ufwStatusSummary}
            />
          </>
        )}
        <VpcSecurityRolesCard compact />
        {profileLoading && <p className="text-xs text-gray-500">Loading rule profile…</p>}
        {profile && <VpcSecurityProfileCard profile={profile} />}
        <div className="rounded-md border border-blue-200 bg-blue-50/60">
          <button
            type="button"
            onClick={() => setCloudSgOpen((open) => !open)}
            className="flex w-full items-start justify-between gap-3 px-3 py-2.5 text-left"
          >
            <div>
              <p className="text-xs font-semibold text-blue-950">Apply AWS security group rules (optional)</p>
              <p className="mt-0.5 text-[11px] text-blue-900/90">
                Uses a linked AWS account to add ingress rules from this stack&apos;s profile. SSH is restricted
                to your admin CIDR; game and web ports use the public template.
              </p>
            </div>
            {cloudSgOpen ? (
              <ChevronUp className="mt-0.5 h-4 w-4 shrink-0 text-blue-800" aria-hidden="true" />
            ) : (
              <ChevronDown className="mt-0.5 h-4 w-4 shrink-0 text-blue-800" aria-hidden="true" />
            )}
          </button>
          {cloudSgOpen && (
            <div className="space-y-3 border-t border-blue-200 px-3 py-3">
              {awsConnections.length === 0 ? (
                <p className="text-xs text-blue-950">
                  Link an AWS account under <span className="font-medium">Admin → Cloud</span> to use automation.
                </p>
              ) : (
                <>
                  <label className="block text-xs text-blue-950">
                    <span className="mb-1 block font-medium">Linked AWS account</span>
                    <select
                      value={connectionId}
                      onChange={(event) => setConnectionId(event.target.value)}
                      className="w-full rounded-md border border-blue-200 bg-white px-2 py-1.5 text-xs text-gray-900"
                    >
                      <option value="">Select connection…</option>
                      {awsConnections.map((connection) => (
                        <option key={connection.id} value={connection.id}>
                          {connection.label}
                          {connection.defaultRegion ? ` (${connection.defaultRegion})` : ''}
                        </option>
                      ))}
                    </select>
                  </label>
                  <label className="block text-xs text-blue-950">
                    <span className="mb-1 block font-medium">Admin SSH source CIDR</span>
                    <div className="flex flex-wrap gap-2">
                      <input
                        type="text"
                        value={adminSourceCidr}
                        onChange={(event) => setAdminSourceCidr(event.target.value)}
                        placeholder="203.0.113.10/32"
                        className="min-w-[12rem] flex-1 rounded-md border border-blue-200 bg-white px-2 py-1.5 font-mono text-xs text-gray-900"
                      />
                      {networkInfo?.suggestedAdminSourceCidr && (
                        <button
                          type="button"
                          onClick={() => setAdminSourceCidr(networkInfo.suggestedAdminSourceCidr ?? '')}
                          className="rounded-md border border-blue-300 bg-white px-2 py-1.5 text-[11px] font-medium text-blue-800 hover:bg-blue-100"
                        >
                          Use my IP
                        </button>
                      )}
                    </div>
                  </label>
                  <div className="grid gap-3 sm:grid-cols-2">
                    <label className="block text-xs text-blue-950">
                      <span className="mb-1 block font-medium">EC2 instance id (optional)</span>
                      <input
                        type="text"
                        value={instanceId}
                        onChange={(event) => setInstanceId(event.target.value)}
                        placeholder="i-0abc123…"
                        className="w-full rounded-md border border-blue-200 bg-white px-2 py-1.5 font-mono text-xs text-gray-900"
                      />
                    </label>
                    <label className="block text-xs text-blue-950">
                      <span className="mb-1 block font-medium">AWS region (optional)</span>
                      <input
                        type="text"
                        value={awsRegion}
                        onChange={(event) => setAwsRegion(event.target.value)}
                        placeholder="us-east-1"
                        className="w-full rounded-md border border-blue-200 bg-white px-2 py-1.5 font-mono text-xs text-gray-900"
                      />
                    </label>
                  </div>
                  <p className="text-[11px] text-blue-900/90">
                    When instance id is omitted, the platform finds the running EC2 instance whose public IP or DNS
                    matches this stack&apos;s host ({remoteHost || 'not set'}). Duplicate rules are skipped.
                  </p>
                  <button
                    type="button"
                    onClick={() => syncCloudSg.mutate()}
                    disabled={
                      syncCloudSg.isPending
                      || !connectionId
                      || !adminSourceCidr.trim()
                    }
                    className="inline-flex items-center gap-2 rounded-md border border-blue-400 bg-blue-600 px-3 py-1.5 text-xs font-medium text-white hover:bg-blue-700 disabled:opacity-60"
                  >
                    {syncCloudSg.isPending ? (
                      <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
                    ) : (
                      <Shield className="h-3.5 w-3.5" aria-hidden="true" />
                    )}
                    Apply AWS security group rules
                  </button>
                </>
              )}
              {cloudSgMessage && (
                <div
                  role="status"
                  className={`rounded-md border px-3 py-2 text-xs ${
                    cloudSgSuccess
                      ? 'border-green-200 bg-green-50 text-green-900'
                      : 'border-amber-200 bg-amber-50 text-amber-950'
                  }`}
                >
                  {cloudSgMessage}
                </div>
              )}
            </div>
          )}
        </div>
        {syncMessage && (
          <div
            role="status"
            className={`rounded-md border px-3 py-2 text-xs ${
              syncSuccess
                ? 'border-green-200 bg-green-50 text-green-900'
                : 'border-amber-200 bg-amber-50 text-amber-950'
            }`}
          >
            <p className="flex items-start gap-2 font-medium">
              {syncSuccess ? (
                <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-green-600" aria-hidden="true" />
              ) : (
                <XCircle className="mt-0.5 h-4 w-4 shrink-0 text-amber-700" aria-hidden="true" />
              )}
              <span>{syncMessage}</span>
            </p>
            {syncSteps && syncSteps.length > 0 && (
              <ul className="mt-2 space-y-1 pl-6">
                {syncSteps.map((step) => (
                  <li key={step.name} className={`flex items-start gap-1.5 ${step.passed ? 'text-green-900' : 'text-amber-950'}`}>
                    {step.passed ? (
                      <CheckCircle2 className="mt-0.5 h-3.5 w-3.5 shrink-0 text-green-600" aria-hidden="true" />
                    ) : (
                      <XCircle className="mt-0.5 h-3.5 w-3.5 shrink-0 text-amber-700" aria-hidden="true" />
                    )}
                    <span>
                      {step.name}
                      {step.message ? ` — ${step.message}` : ''}
                    </span>
                  </li>
                ))}
              </ul>
            )}
          </div>
        )}
      </div>

      <CloudSecurityGroupGuideDialog
        open={sgGuideOpen}
        onClose={() => setSgGuideOpen(false)}
        host={deployment?.externalHost}
        sshPort={deployment?.externalSshPort ?? 22}
        profile={profile}
        requireAcknowledgment={false}
      />
    </section>
  )
}
