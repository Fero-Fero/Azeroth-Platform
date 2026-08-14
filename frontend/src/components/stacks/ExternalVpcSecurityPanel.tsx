import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import {
  AlertTriangle,
  CheckCircle2,
  HelpCircle,
  Loader2,
  RefreshCw,
  Shield,
  XCircle,
} from 'lucide-react'
import { CloudSecurityGroupGuideDialog } from '@/components/stacks/CloudSecurityGroupGuideDialog'
import { VpcSecurityProfileCard, VpcSecurityRolesCard } from '@/components/stacks/VpcSecurityRolesCard'
import { stackApi } from '@/services/api'
import { apiErrorMessage } from '@/lib/utils'
import type {
  RemotePrerequisiteCheckDto,
  StackDetailsDto,
  VpcSecurityCheckDto,
  VpcSecurityCheckStatus,
} from '@/types/stack.types'
import { DeploymentTarget } from '@/types/stack.types'

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
