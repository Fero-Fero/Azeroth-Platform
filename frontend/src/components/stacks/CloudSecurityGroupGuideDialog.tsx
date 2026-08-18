import { useEffect, useId, useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Check, Copy, ExternalLink, X, AlertTriangle } from 'lucide-react'
import { CloudSecurityGroupRulesCard } from '@/components/stacks/VpcSecurityRolesCard'
import { DEFAULT_ARMORY_PORT, DEFAULT_CLIENT_PORT } from '@/lib/stack-network-defaults'
import { resolvePublicAdminSourceCidr } from '@/lib/public-ip'
import { systemApi } from '@/services/api'
import type { VpcSecurityProfileDto } from '@/types/stack.types'

type CloudProvider = 'aws' | 'digitalocean' | 'vultr' | 'gcp' | 'azure' | 'hetzner'

interface CloudSecurityGroupGuideDialogProps {
  open: boolean
  onClose: () => void
  host?: string
  sshPort?: number
  profile?: VpcSecurityProfileDto | null
  /** When false, hides the acknowledgment checkbox (stack overview reference mode). */
  requireAcknowledgment?: boolean
  acknowledged?: boolean
  onAcknowledgedChange?: (value: boolean) => void
  /** Prefills the provider tab when the dialog opens. */
  initialProvider?: CloudProvider
}

const AWS_CONSOLE_INSTANCES = 'https://console.aws.amazon.com/ec2/home#Instances:'
const DIGITALOCEAN_FIREWALLS = 'https://cloud.digitalocean.com/networking/firewalls'
const VULTR_FIREWALLS = 'https://my.vultr.com/firewall/'
const GCP_FIREWALLS = 'https://console.cloud.google.com/networking/firewalls'
const HETZNER_FIREWALLS = 'https://console.hetzner.cloud/'

function CopyableCidr({ value, label }: { value: string; label: string }) {
  const [copied, setCopied] = useState(false)

  const handleCopy = async () => {
    try {
      await navigator.clipboard.writeText(value)
      setCopied(true)
      window.setTimeout(() => setCopied(false), 2000)
    } catch {
      setCopied(false)
    }
  }

  return (
    <span className="mt-1 inline-flex flex-wrap items-center gap-2">
      <code className="rounded bg-white px-2 py-1 font-mono text-xs text-gray-900 ring-1 ring-blue-200">
        {value}
      </code>
      <button
        type="button"
        onClick={() => void handleCopy()}
        className="inline-flex items-center gap-1 rounded-md border border-blue-300 bg-white px-2 py-1 text-[11px] font-medium text-blue-800 hover:bg-blue-100"
      >
        {copied ? (
          <>
            <Check className="h-3 w-3" aria-hidden="true" />
            Copied
          </>
        ) : (
          <>
            <Copy className="h-3 w-3" aria-hidden="true" />
            Copy {label}
          </>
        )}
      </button>
    </span>
  )
}

function AdminIpHint({ suggestedAdminSourceCidr }: { suggestedAdminSourceCidr?: string }) {
  if (!suggestedAdminSourceCidr) {
    return (
      <span className="mt-1 block text-xs text-blue-800/90">
        Could not detect your public IP automatically. Use your cloud console&apos;s{' '}
        <span className="font-medium">My IP</span> button, or search &quot;what is my ip&quot; and paste the
        result with <span className="font-mono">/32</span> (for example{' '}
        <span className="font-mono">203.0.113.10/32</span>).
      </span>
    )
  }

  return (
    <span className="mt-1 block text-xs text-blue-900">
      Your public IP (paste as SSH source):
      <CopyableCidr value={suggestedAdminSourceCidr} label="IP" />
      <span className="mt-1 block text-[11px] text-blue-800/80">
        This is looked up from your browser, not from the platform container - use this value, not Docker
        addresses like <span className="font-mono">172.18.0.1</span>.
      </span>
    </span>
  )
}

function ProviderSteps({
  provider,
  host,
  sshPort,
  suggestedAdminSourceCidr,
}: {
  provider: CloudProvider
  host?: string
  sshPort: number
  suggestedAdminSourceCidr?: string
}) {
  if (provider === 'aws') {
    return (
      <ol className="list-decimal space-y-2 pl-5 text-sm text-gray-700">
        <li>
          Open the{' '}
          <a
            href={AWS_CONSOLE_INSTANCES}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-1 font-medium text-blue-700 hover:underline"
          >
            AWS EC2 Instances console
            <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
          </a>
          .
        </li>
        <li>
          Select your instance
          {host?.trim() ? (
            <>
              {' '}
              (look for public IP or DNS <span className="font-mono text-gray-900">{host.trim()}</span>)
            </>
          ) : (
            ' (match the public IP or DNS from the wizard)'
          )}
          .
        </li>
        <li>
          Open the <span className="font-medium">Security</span> tab and click the linked{' '}
          <span className="font-medium">Security group</span> (for example{' '}
          <span className="font-mono text-xs">launch-wizard-…</span>).
        </li>
        <li>
          Choose <span className="font-medium">Edit inbound rules</span> →{' '}
          <span className="font-medium">Add rule</span> for each row in the table below.
        </li>
        <li>
          For SSH (port {sshPort}), set <span className="font-medium">Source</span> to your admin IP - do not
          use <span className="font-mono">0.0.0.0/0</span> for SSH.
          <AdminIpHint suggestedAdminSourceCidr={suggestedAdminSourceCidr} />
        </li>
        <li>
          For game ports (3724, 8085, and web ports when shown), use{' '}
          <span className="font-mono">0.0.0.0/0</span> unless you intentionally restrict players to a CIDR.
        </li>
        <li>
          Save rules. See the highlighted <span className="font-medium">Do not add inbound rules for</span>{' '}
          section below (MySQL 3306 and SOAP 7878 must stay closed).
        </li>
      </ol>
    )
  }

  if (provider === 'digitalocean') {
    return (
      <ol className="list-decimal space-y-2 pl-5 text-sm text-gray-700">
        <li>
          Open{' '}
          <a
            href={DIGITALOCEAN_FIREWALLS}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-1 font-medium text-blue-700 hover:underline"
          >
            Networking → Firewalls
            <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
          </a>{' '}
          in DigitalOcean Cloud.
        </li>
        <li>
          Create a firewall (or edit the one named{' '}
          <span className="font-mono text-xs">azeroth-platform-…</span>) and assign it to your Droplet
          {host?.trim() ? (
            <>
              {' '}
              (public IP <span className="font-mono text-gray-900">{host.trim()}</span>)
            </>
          ) : (
            ' (match the public IP from the wizard)'
          )}
          .
        </li>
        <li>
          Add inbound TCP rules for each row in the table below. Outbound can stay allow-all.
        </li>
        <li>
          For SSH (port {sshPort}), set sources to your admin IP - do not use{' '}
          <span className="font-mono">0.0.0.0/0</span> for SSH.
          <AdminIpHint suggestedAdminSourceCidr={suggestedAdminSourceCidr} />
        </li>
        <li>
          For game ports (3724, 8085, and web ports when shown), use{' '}
          <span className="font-mono">0.0.0.0/0</span> unless you intentionally restrict players.
        </li>
        <li>
          See the highlighted <span className="font-medium">Do not add inbound rules for</span> section below
          (MySQL 3306 and SOAP 7878 must stay closed).
        </li>
      </ol>
    )
  }

  if (provider === 'vultr') {
    return (
      <ol className="list-decimal space-y-2 pl-5 text-sm text-gray-700">
        <li>
          Open{' '}
          <a
            href={VULTR_FIREWALLS}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-1 font-medium text-blue-700 hover:underline"
          >
            Products → Firewall
            <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
          </a>{' '}
          in the Vultr customer portal.
        </li>
        <li>
          Create a firewall group (or edit the one named{' '}
          <span className="font-mono text-xs">azeroth-platform-…</span>) and link it to your instance
          {host?.trim() ? (
            <>
              {' '}
              (public IP <span className="font-mono text-gray-900">{host.trim()}</span>)
            </>
          ) : (
            ' (match the public IP from the wizard)'
          )}
          .
        </li>
        <li>
          Add inbound IPv4 TCP rules for each row in the table below.
        </li>
        <li>
          For SSH (port {sshPort}), set the source to your admin IP - do not use{' '}
          <span className="font-mono">0.0.0.0/0</span> for SSH.
          <AdminIpHint suggestedAdminSourceCidr={suggestedAdminSourceCidr} />
        </li>
        <li>
          For game ports (3724, 8085, and web ports when shown), use{' '}
          <span className="font-mono">0.0.0.0/0</span> unless you intentionally restrict players.
        </li>
        <li>
          See the highlighted <span className="font-medium">Do not add inbound rules for</span> section below
          (MySQL 3306 and SOAP 7878 must stay closed).
        </li>
      </ol>
    )
  }

  if (provider === 'gcp') {
    return (
      <ol className="list-decimal space-y-2 pl-5 text-sm text-gray-700">
        <li>
          Open{' '}
          <a
            href={GCP_FIREWALLS}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-1 font-medium text-blue-800 hover:underline"
          >
            VPC network → Firewall
            <ExternalLink className="h-3 w-3" aria-hidden="true" />
          </a>{' '}
          in Google Cloud Console.
        </li>
        <li>
          Confirm the VM has network tag <span className="font-mono">azeroth-platform</span> (set automatically
          at launch).
        </li>
        <li>
          Create ingress rules targeting that tag. Restrict SSH (port {sshPort}) to your admin IP; open
          player/web ports to the internet as needed.
          <AdminIpHint suggestedAdminSourceCidr={suggestedAdminSourceCidr} />
        </li>
        <li>
          See the highlighted <span className="font-medium">Do not add inbound rules for</span> section below
          (MySQL 3306 and SOAP 7878 must stay closed).
        </li>
      </ol>
    )
  }

  if (provider === 'hetzner') {
    return (
      <ol className="list-decimal space-y-2 pl-5 text-sm text-gray-700">
        <li>
          Open{' '}
          <a
            href={HETZNER_FIREWALLS}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-1 font-medium text-blue-700 hover:underline"
          >
            Hetzner Cloud Console → Firewalls
            <ExternalLink className="h-3.5 w-3.5" aria-hidden="true" />
          </a>
          .
        </li>
        <li>
          Create a firewall (or edit the one named{' '}
          <span className="font-mono text-xs">azeroth-platform-…</span>) and apply it to your server
          {host?.trim() ? (
            <>
              {' '}
              (public IP <span className="font-mono text-gray-900">{host.trim()}</span>)
            </>
          ) : (
            ' (match the public IP from the wizard)'
          )}
          . Launch and pick apply this automatically.
        </li>
        <li>
          Add inbound TCP rules for each row in the table below.
        </li>
        <li>
          For SSH (port {sshPort}), set sources to your admin IP - do not use{' '}
          <span className="font-mono">0.0.0.0/0</span> for SSH.
          <AdminIpHint suggestedAdminSourceCidr={suggestedAdminSourceCidr} />
        </li>
        <li>
          For game ports (3724, 8085, and web ports when shown), use{' '}
          <span className="font-mono">0.0.0.0/0</span> unless you intentionally restrict players.
        </li>
        <li>
          See the highlighted <span className="font-medium">Do not add inbound rules for</span> section below
          (MySQL 3306 and SOAP 7878 must stay closed).
        </li>
      </ol>
    )
  }

  return (
    <ol className="list-decimal space-y-2 pl-5 text-sm text-gray-700">
      <li>
        Open your VM in Azure Portal → <span className="font-medium">Networking</span>. Pick/bootstrap
        applies an NSG on the NIC automatically (rules named <span className="font-mono">azp-tcp-{'{port}'}</span>
        ). Use this checklist only if you need to inspect or repair those rules.
      </li>
      <li>
        Confirm inbound port rules matching the table below (SSH on port {sshPort} from your IP only).
        <AdminIpHint suggestedAdminSourceCidr={suggestedAdminSourceCidr} />
      </li>
      <li>
        See the highlighted <span className="font-medium">Do not add inbound rules for</span> section below
        (MySQL 3306 and SOAP 7878 must stay closed).
      </li>
    </ol>
  )
}

export function CloudSecurityGroupGuideDialog({
  open,
  onClose,
  host,
  sshPort = 22,
  profile,
  requireAcknowledgment = true,
  acknowledged = false,
  onAcknowledgedChange,
  initialProvider = 'aws',
}: CloudSecurityGroupGuideDialogProps) {
  const titleId = useId()
  const [provider, setProvider] = useState<CloudProvider>(initialProvider)
  const [localAcknowledged, setLocalAcknowledged] = useState(acknowledged)

  const { data: networkInfo } = useQuery({
    queryKey: ['system-network', 'admin-source'],
    queryFn: async () => {
      const res = await systemApi.network()
      const suggestedAdminSourceCidr = await resolvePublicAdminSourceCidr(
        res.data.suggestedAdminSourceCidr
      )

      return {
        ...res.data,
        suggestedAdminSourceCidr,
      }
    },
    enabled: open,
    staleTime: 60_000,
  })

  useEffect(() => {
    if (open) {
      setLocalAcknowledged(acknowledged)
      setProvider(initialProvider)
    }
  }, [acknowledged, initialProvider, open])

  if (!open) {
    return null
  }

  const handleDone = () => {
    if (requireAcknowledgment) {
      onAcknowledgedChange?.(localAcknowledged)
    }
    onClose()
  }

  return (
    <div
      className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
      role="presentation"
      onMouseDown={(event) => {
        if (event.target === event.currentTarget) {
          onClose()
        }
      }}
    >
      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby={titleId}
        className="flex max-h-[90vh] w-full max-w-2xl flex-col overflow-hidden rounded-lg bg-white shadow-xl"
      >
        <div className="flex items-start justify-between border-b border-gray-200 px-5 py-4">
          <div>
            <h2 id={titleId} className="text-lg font-semibold text-gray-900">
              Configure cloud security group
            </h2>
            <p className="mt-1 text-sm text-gray-600">
              Host <span className="font-medium">ufw</span> (from Verify VPC / Repair) protects the Linux instance.
              Your cloud provider&apos;s security group or Cloud Firewall is a separate layer in front of the VPC.
              AWS, DigitalOcean, Vultr, Google Cloud, Azure, and Hetzner can apply this automatically at launch; use this
              guide when you configure rules by hand.
            </p>
          </div>
          <button
            type="button"
            onClick={onClose}
            className="rounded-md p-1 text-gray-500 hover:bg-gray-100 hover:text-gray-700"
            aria-label="Close"
          >
            <X className="h-5 w-5" aria-hidden="true" />
          </button>
        </div>

        <div className="flex-1 overflow-y-auto px-5 py-4 space-y-4">
          <div>
            <p className="mb-2 text-xs font-medium text-gray-700">Cloud provider</p>
            <div className="flex flex-wrap gap-2">
              {(
                [
                  { id: 'aws' as const, label: 'AWS EC2' },
                  { id: 'digitalocean' as const, label: 'DigitalOcean' },
                  { id: 'vultr' as const, label: 'Vultr' },
                  { id: 'gcp' as const, label: 'Google Cloud' },
                  { id: 'azure' as const, label: 'Azure' },
                  { id: 'hetzner' as const, label: 'Hetzner' },
                ] as const
              ).map((option) => (
                <button
                  key={option.id}
                  type="button"
                  onClick={() => setProvider(option.id)}
                  className={`rounded-md border px-3 py-1.5 text-xs font-medium ${
                    provider === option.id
                      ? 'border-blue-500 bg-blue-50 text-blue-800'
                      : 'border-gray-300 text-gray-700 hover:bg-gray-50'
                  }`}
                >
                  {option.label}
                </button>
              ))}
            </div>
          </div>

          <div className="rounded-md border border-blue-100 bg-blue-50 p-3">
            <p className="text-xs font-medium text-blue-900">Step-by-step</p>
            <div className="mt-2">
              <ProviderSteps
                provider={provider}
                host={host}
                sshPort={sshPort}
                suggestedAdminSourceCidr={networkInfo?.suggestedAdminSourceCidr}
              />
            </div>
          </div>

          {profile ? (
            <div>
              <p className="mb-2 text-xs font-medium text-gray-800">Inbound rules to add</p>
              <CloudSecurityGroupRulesCard
                profile={profile}
                suggestedSshSource={networkInfo?.suggestedAdminSourceCidr}
              />
            </div>
          ) : (
            <div className="rounded-md border border-amber-300 bg-amber-50 p-3 text-xs text-amber-950">
              <p className="flex items-center gap-2 text-sm font-semibold text-amber-950">
                <AlertTriangle className="h-4 w-4 shrink-0" aria-hidden="true" />
                Do not add inbound rules for MySQL or SOAP
              </p>
              <p className="mt-1">
                Enter a remote host above to load the inbound allow list. Default web ports are{' '}
                <span className="font-mono">{DEFAULT_ARMORY_PORT}</span> (armory) and{' '}
                <span className="font-mono">{DEFAULT_CLIENT_PORT}</span> (launcher/client). Never open{' '}
                <span className="font-mono">3306</span> (MySQL) or <span className="font-mono">7878</span>{' '}
                (SOAP) at the cloud edge.
              </p>
            </div>
          )}

          <p className="text-xs text-gray-500">
            If you run multiple stacks on the same VPC, later stacks may use higher ports - update cloud
            rules from the stack VPC overview tab when that happens.
          </p>
        </div>

        <div className="border-t border-gray-200 px-5 py-4">
          {requireAcknowledgment && (
            <label className="flex items-start gap-2 text-sm text-gray-800">
              <input
                type="checkbox"
                checked={localAcknowledged}
                onChange={(event) => setLocalAcknowledged(event.target.checked)}
                className="mt-0.5"
              />
              <span>
                I have configured my cloud security group (or firewall rules) to match the allow/deny
                guidance above, including <span className="font-medium">not</span> opening MySQL (3306) or
                SOAP (7878).
              </span>
            </label>
          )}
          <div className={`flex flex-wrap justify-end gap-3 ${requireAcknowledgment ? 'mt-4' : ''}`}>
            <button
              type="button"
              onClick={onClose}
              className="rounded-md border border-gray-300 px-4 py-2 text-sm text-gray-700 hover:bg-gray-50"
            >
              {requireAcknowledgment ? 'Cancel' : 'Close'}
            </button>
            {requireAcknowledgment && (
              <button
                type="button"
                onClick={handleDone}
                disabled={!localAcknowledged}
                className="rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-60"
              >
                Done
              </button>
            )}
          </div>
        </div>
      </div>
    </div>
  )
}
