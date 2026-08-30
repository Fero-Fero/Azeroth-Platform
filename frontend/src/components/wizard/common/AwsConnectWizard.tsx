import { useMemo, useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Check, Copy, Download, ExternalLink, Loader2 } from 'lucide-react'
import { cloudApi } from '@/services/api'
import type {
  CloudAuthStartResultDto,
  CloudProvider,
  CloudProviderConnectionDto,
} from '@/types/stack.types'
import { cn } from '@/lib/utils'

function extractErrorMessage(error: unknown, fallback: string): string {
  if (error && typeof error === 'object' && 'response' in error) {
    const data = (error as { response?: { data?: unknown } }).response?.data
    if (typeof data === 'string' && data.trim().length > 0) {
      return data
    }
  }

  return fallback
}

interface AwsConnectWizardProps {
  provider: CloudProvider
  start: CloudAuthStartResultDto
  label: string
  reconnectConnectionId?: string
  disabled?: boolean
  onLinked?: (connection: CloudProviderConnectionDto) => void
  onCancel: () => void
}

export function AwsConnectWizard({
  provider,
  start,
  label,
  reconnectConnectionId,
  disabled = false,
  onLinked,
  onCancel,
}: AwsConnectWizardProps) {
  const queryClient = useQueryClient()
  const templates = start.awsTemplates ?? []
  const [tier, setTier] = useState(templates[templates.length - 1]?.policyTier ?? 'Full')
  const [roleArn, setRoleArn] = useState('')
  const [copied, setCopied] = useState<'external' | 'yaml' | null>(null)

  const selected = useMemo(
    () => templates.find((template) => template.policyTier === tier) ?? templates[0],
    [templates, tier]
  )

  const completeMutation = useMutation({
    mutationFn: async () =>
      (
        await cloudApi.completeCloudAuth(provider, {
          roleArn: roleArn.trim(),
          externalId: start.externalId,
          label,
          reconnectConnectionId,
        })
      ).data,
    onSuccess: async (connection) => {
      await queryClient.invalidateQueries({ queryKey: ['cloud-connections'] })
      await queryClient.invalidateQueries({ queryKey: ['cloud-audit-logs'] })
      onLinked?.(connection)
    },
  })

  const copy = async (value: string, which: 'external' | 'yaml') => {
    try {
      await navigator.clipboard.writeText(value)
      setCopied(which)
      window.setTimeout(() => setCopied(null), 2000)
    } catch {
      setCopied(null)
    }
  }

  return (
    <div className="space-y-3 rounded-md border border-amber-200 bg-amber-50/70 p-3">
      <div>
        <p className="text-xs font-semibold text-amber-950">Connect AWS account</p>
        <p className="mt-0.5 text-[11px] text-amber-900">
          This is a cross-account IAM role, not OAuth. Deploy the template in AWS, then paste the{' '}
          <span className="font-medium">Role ARN</span> output.
        </p>
      </div>

      <div>
        <p className="text-[11px] font-medium text-amber-950">External ID</p>
        <div className="mt-1 flex flex-wrap items-center gap-2">
          <code className="rounded bg-white px-2 py-1 font-mono text-xs text-gray-900 ring-1 ring-amber-200">
            {start.externalId}
          </code>
          <button
            type="button"
            disabled={disabled || !start.externalId}
            onClick={() => void copy(start.externalId ?? '', 'external')}
            className="inline-flex items-center gap-1 rounded-md border border-amber-300 bg-white px-2 py-1 text-[11px] font-medium text-amber-900 hover:bg-amber-100"
          >
            {copied === 'external' ? <Check className="h-3 w-3" aria-hidden="true" /> : <Copy className="h-3 w-3" aria-hidden="true" />}
            Copy
          </button>
        </div>
      </div>

      <div className="flex flex-wrap gap-2">
        {templates.map((template) => (
          <button
            key={template.policyTier}
            type="button"
            disabled={disabled}
            onClick={() => setTier(template.policyTier)}
            className={cn(
              'rounded-md border px-2.5 py-1 text-[11px] font-medium',
              template.policyTier === selected?.policyTier
                ? 'border-amber-500 bg-white text-amber-950'
                : 'border-amber-200 bg-amber-100/60 text-amber-900 hover:bg-amber-100'
            )}
          >
            {template.label}
          </button>
        ))}
      </div>
      {selected?.description ? <p className="text-[11px] text-amber-900">{selected.description}</p> : null}

      <ol className="list-decimal space-y-1 pl-4 text-[11px] text-amber-950">
        <li>Copy the CloudFormation YAML below.</li>
        <li>
          Open the{' '}
          {start.cloudFormationConsoleUrl ? (
            <a
              href={start.cloudFormationConsoleUrl}
              target="_blank"
              rel="noopener noreferrer"
              className="inline-flex items-center gap-0.5 font-medium text-amber-800 underline"
            >
              CloudFormation console
              <ExternalLink className="h-3 w-3" aria-hidden="true" />
            </a>
          ) : (
            'CloudFormation console'
          )}
          , create a stack, and paste the template.
        </li>
        <li>Copy the <span className="font-medium">RoleArn</span> output and paste it here.</li>
      </ol>

      <textarea
        readOnly
        rows={8}
        value={selected?.cloudFormationYaml ?? ''}
        className="block w-full rounded-md border border-amber-200 bg-white px-2 py-1.5 font-mono text-[10px] text-gray-800"
      />
      <div className="flex flex-wrap gap-2">
        <button
          type="button"
          disabled={disabled || !selected?.cloudFormationYaml}
          onClick={() => void copy(selected?.cloudFormationYaml ?? '', 'yaml')}
          className="inline-flex items-center gap-1 rounded-md border border-amber-300 bg-white px-2 py-1 text-[11px] font-medium text-amber-900 hover:bg-amber-100"
        >
          {copied === 'yaml' ? <Check className="h-3 w-3" aria-hidden="true" /> : <Copy className="h-3 w-3" aria-hidden="true" />}
          Copy template
        </button>
        <button
          type="button"
          disabled={disabled || !selected?.cloudFormationYaml}
          onClick={() => {
            const yaml = selected?.cloudFormationYaml ?? ''
            const blob = new Blob([yaml], { type: 'text/yaml' })
            const url = URL.createObjectURL(blob)
            const link = document.createElement('a')
            link.href = url
            link.download = `azeroth-platform-aws-${selected?.policyTier ?? 'Full'}.yaml`
            link.click()
            URL.revokeObjectURL(url)
          }}
          className="inline-flex items-center gap-1 rounded-md border border-amber-300 bg-white px-2 py-1 text-[11px] font-medium text-amber-900 hover:bg-amber-100"
        >
          <Download className="h-3 w-3" aria-hidden="true" />
          Download YAML
        </button>
      </div>

      <div>
        <label htmlFor="aws-role-arn" className="block text-xs font-medium text-amber-950">
          Role ARN
        </label>
        <input
          id="aws-role-arn"
          type="text"
          value={roleArn}
          disabled={disabled || completeMutation.isPending}
          placeholder="arn:aws:iam::123456789012:role/AzerothPlatformAccess"
          onChange={(event) => setRoleArn(event.target.value)}
          className="mt-1 block w-full rounded-md border border-amber-200 bg-white px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-amber-500"
        />
      </div>

      {completeMutation.isError ? (
        <p className="text-xs text-red-700">
          {extractErrorMessage(completeMutation.error, 'Could not assume the IAM role.')}
        </p>
      ) : null}

      <div className="flex flex-wrap gap-2">
        <button
          type="button"
          disabled={disabled || completeMutation.isPending || roleArn.trim().length === 0}
          onClick={() => void completeMutation.mutate()}
          className="inline-flex items-center gap-2 rounded-md bg-amber-800 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-900 disabled:opacity-60"
        >
          {completeMutation.isPending ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> : null}
          Verify role and connect
        </button>
        <button
          type="button"
          disabled={disabled || completeMutation.isPending}
          onClick={onCancel}
          className="rounded-md border border-amber-300 bg-white px-3 py-1.5 text-xs font-medium text-amber-900 hover:bg-amber-100"
        >
          Cancel
        </button>
      </div>
    </div>
  )
}
