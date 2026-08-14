import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { Loader2, RefreshCw, Shield } from 'lucide-react'
import { CloudSecurityGroupGuideDialog } from '@/components/stacks/CloudSecurityGroupGuideDialog'
import { VpcSecurityProfileCard, VpcSecurityRolesCard } from '@/components/stacks/VpcSecurityRolesCard'
import { stackApi } from '@/services/api'
import { apiErrorMessage } from '@/lib/utils'
import type { RemotePrerequisiteCheckDto, StackDetailsDto } from '@/types/stack.types'
import { DeploymentTarget } from '@/types/stack.types'

export default function ExternalVpcSecurityPanel({ stack }: { stack: StackDetailsDto }) {
  const isExternal = stack.configuration?.deployment?.target === DeploymentTarget.External
  const [syncMessage, setSyncMessage] = useState<string | null>(null)
  const [syncSuccess, setSyncSuccess] = useState<boolean | null>(null)
  const [syncSteps, setSyncSteps] = useState<RemotePrerequisiteCheckDto[] | null>(null)
  const [sgGuideOpen, setSgGuideOpen] = useState(false)

  const deployment = stack.configuration?.deployment
  const remoteHost = deployment?.externalHost?.trim()

  const { data: profile, isLoading } = useQuery({
    queryKey: ['vpc-security-profile', stack.stackId],
    queryFn: async () => (await stackApi.vpcSecurityProfile(stack.stackId)).data,
    enabled: isExternal,
  })

  const syncFirewall = useMutation({
    mutationFn: () => stackApi.syncVpcFirewall(stack.stackId),
    onSuccess: (res) => {
      setSyncSuccess(res.data.success)
      setSyncMessage(res.data.message)
      setSyncSteps(res.data.steps ?? null)
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
        <button
          type="button"
          onClick={() => syncFirewall.mutate()}
          disabled={syncFirewall.isPending}
          className="inline-flex items-center gap-2 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-60"
          title="Configure ufw on the remote VPS over SSH (optional)"
        >
          {syncFirewall.isPending ? (
            <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
          ) : (
            <RefreshCw className="h-3.5 w-3.5" aria-hidden="true" />
          )}
          Sync VPC firewall (ufw)
        </button>
        <button
          type="button"
          onClick={() => setSgGuideOpen(true)}
          className="inline-flex items-center gap-2 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50"
        >
          Cloud SG guide
        </button>
      </div>

      <div className="mt-4 space-y-4">
        <VpcSecurityRolesCard compact />
        {isLoading && <p className="text-xs text-gray-500">Loading rule profile…</p>}
        {profile && <VpcSecurityProfileCard profile={profile} />}
        {syncMessage && (
          <div
            className={`rounded-md border px-3 py-2 text-xs ${
              syncSuccess
                ? 'border-green-200 bg-green-50 text-green-900'
                : 'border-amber-200 bg-amber-50 text-amber-950'
            }`}
          >
            <p>{syncMessage}</p>
            {syncSteps && syncSteps.length > 0 && (
              <ul className="mt-2 space-y-1">
                {syncSteps.map((step) => (
                  <li key={step.name} className={step.passed ? 'text-green-900' : 'text-amber-950'}>
                    {step.passed ? '✓' : '✗'} {step.name}
                    {step.message ? ` — ${step.message}` : ''}
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
