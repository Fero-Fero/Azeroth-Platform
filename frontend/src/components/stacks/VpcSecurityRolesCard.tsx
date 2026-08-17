import { useQuery } from '@tanstack/react-query'
import { AlertTriangle, Shield } from 'lucide-react'
import { systemApi } from '@/services/api'
import type { VpcSecurityProfileDto, VpcSecurityRuleDto } from '@/types/stack.types'

function RuleTable({
  title,
  rules,
  variant,
  showSource = false,
}: {
  title: string
  rules: VpcSecurityRuleDto[]
  variant: 'allow' | 'deny'
  showSource?: boolean
}) {
  if (rules.length === 0) {
    return null
  }

  if (variant === 'deny') {
    return (
      <div className="rounded-md border border-amber-300 bg-amber-50 p-3">
        <p className="flex items-center gap-2 text-sm font-semibold text-amber-950">
          <AlertTriangle className="h-4 w-4 shrink-0" aria-hidden="true" />
          {title}
        </p>
        <p className="mt-1 text-xs text-amber-950">
          Do not create allow rules for these ports in your cloud firewall. Opening them exposes your
          database or SOAP admin API.
        </p>
        <div className="mt-2 overflow-x-auto rounded border border-amber-200 bg-white">
          <table className="min-w-full divide-y divide-amber-100 text-xs">
            <thead className="bg-amber-50/80">
              <tr>
                <th className="px-2 py-1.5 text-left font-medium text-amber-900">Port</th>
                {showSource && (
                  <th className="px-2 py-1.5 text-left font-medium text-amber-900">Source</th>
                )}
                <th className="px-2 py-1.5 text-left font-medium text-amber-900">Notes</th>
              </tr>
            </thead>
            <tbody className="divide-y divide-amber-100">
              {rules.map((rule) => (
                <tr key={`${rule.roleId}-${rule.port}-${rule.action}`}>
                  <td className="px-2 py-1.5 font-mono text-red-800">{rule.port}/{rule.protocol}</td>
                  {showSource && (
                    <td className="px-2 py-1.5 font-mono text-red-800">{rule.source?.trim() || '—'}</td>
                  )}
                  <td className="px-2 py-1.5 text-red-800">{rule.description}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>
    )
  }

  return (
    <div>
      <p className="mb-1.5 text-xs font-medium text-gray-700">{title}</p>
      <div className="overflow-x-auto rounded border border-gray-200">
        <table className="min-w-full divide-y divide-gray-200 text-xs">
          <thead className="bg-gray-50">
            <tr>
              <th className="px-2 py-1.5 text-left font-medium text-gray-600">Port</th>
              {showSource && (
                <th className="px-2 py-1.5 text-left font-medium text-gray-600">Source</th>
              )}
              <th className="px-2 py-1.5 text-left font-medium text-gray-600">Notes</th>
            </tr>
          </thead>
          <tbody className="divide-y divide-gray-100 bg-white">
            {rules.map((rule) => (
              <tr key={`${rule.roleId}-${rule.port}-${rule.action}`}>
                <td className="px-2 py-1.5 font-mono">{rule.port}/{rule.protocol}</td>
                {showSource && (
                  <td className="px-2 py-1.5 font-mono">{rule.source?.trim() || '—'}</td>
                )}
                <td className="px-2 py-1.5 text-gray-700">
                  {rule.description}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function resolveCloudSshSource(rules: VpcSecurityRuleDto[], suggestedSshSource?: string) {
  if (!suggestedSshSource) {
    return rules
  }

  return rules.map((rule) =>
    rule.roleId === 'admin' ? { ...rule, source: suggestedSshSource } : rule
  )
}

/** Inbound rules to add manually — cloud firewall guide only. */
export function CloudSecurityGroupRulesCard({
  profile,
  suggestedSshSource,
}: {
  profile: VpcSecurityProfileDto
  suggestedSshSource?: string
}) {
  const inboundRules = resolveCloudSshSource(profile.cloudSecurityGroupRules, suggestedSshSource)
  const hostFirewall = 'ufw'
  const hostOs = 'Linux'

  return (
    <div className="space-y-3 rounded-md border border-gray-200 bg-gray-50 p-3">
      <p className="text-xs text-gray-700">
        Add these <span className="font-medium">inbound allow</span> rules in your cloud firewall (security
        group, NSG, or Cloud Firewall). Host <span className="font-medium">{hostFirewall}</span> is applied at launch
        and by Verify VPC / Repair — you do not need to set source CIDRs on the {hostOs} host.
      </p>
      <RuleTable
        title="Inbound allow"
        rules={inboundRules}
        variant="allow"
        showSource
      />
      {profile.deniedPorts.length > 0 && (
        <RuleTable title="Do not add inbound rules for" rules={profile.deniedPorts} variant="deny" />
      )}
    </div>
  )
}

export function VpcSecurityRolesCard({ compact = false }: { compact?: boolean }) {
  const { data: catalog } = useQuery({
    queryKey: ['vpc-security-roles'],
    queryFn: async () => (await systemApi.vpcSecurityRoles()).data,
  })

  if (!catalog?.roles?.length) {
    return null
  }

  return (
    <div className="rounded-md border border-gray-200 bg-white p-3">
      <div className="mb-2 flex items-center gap-2">
        <Shield className="h-4 w-4 text-gray-500" aria-hidden="true" />
        <p className="text-xs font-semibold text-gray-800">VPC security roles</p>
      </div>
      <ul className={`space-y-2 ${compact ? 'text-xs' : 'text-sm'}`}>
        {catalog.roles.map((role) => (
          <li key={role.id} className="rounded border border-gray-100 bg-gray-50 p-2">
            <p className="font-medium text-gray-900">{role.name}</p>
            <p className="mt-0.5 text-xs text-gray-600">{role.description}</p>
            <p className="mt-1 text-[11px] text-gray-500">
              Exposure: <span className="font-medium">{role.exposure}</span>
              {role.dockerHandlesBind && ' · Docker bind policy applies'}
              {role.adminSettingsLocation && (
                <>
                  {' '}
                  · Admin: <span className="text-gray-700">{role.adminSettingsLocation}</span>
                </>
              )}
            </p>
          </li>
        ))}
      </ul>
      <p className="mt-2 text-[11px] text-gray-500">
        Worldserver environment variables do not control host or Docker networking.
      </p>
    </div>
  )
}

export function VpcSecurityProfileCard({
  profile,
}: {
  profile: VpcSecurityProfileDto
}) {
  return (
    <div className="space-y-3 rounded-md border border-gray-200 bg-gray-50 p-3">
      <p className="text-xs text-gray-700">{profile.notes}</p>
      <RuleTable
        title="Host firewall (ufw) — allow"
        rules={profile.hostFirewallRules}
        variant="allow"
      />
      <RuleTable
        title="Cloud security group — allow"
        rules={profile.cloudSecurityGroupRules}
        variant="allow"
        showSource
      />
      <RuleTable title="Do not expose" rules={profile.deniedPorts} variant="deny" />
    </div>
  )
}
