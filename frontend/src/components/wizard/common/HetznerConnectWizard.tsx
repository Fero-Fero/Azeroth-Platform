import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { ExternalLink, Loader2 } from 'lucide-react'
import { cloudApi } from '@/services/api'
import type {
  CloudAuthStartResultDto,
  CloudProvider,
  CloudProviderConnectionDto,
} from '@/types/stack.types'

function extractErrorMessage(error: unknown, fallback: string): string {
  if (error && typeof error === 'object' && 'response' in error) {
    const data = (error as { response?: { data?: unknown } }).response?.data
    if (typeof data === 'string' && data.trim().length > 0) {
      return data
    }
  }

  return fallback
}

const HETZNER_TOKEN_DOCS = 'https://docs.hetzner.com/cloud/api/getting-started/generating-api-token/'
const HETZNER_CONSOLE = 'https://console.hetzner.cloud/'

interface HetznerConnectWizardProps {
  provider: CloudProvider
  start: CloudAuthStartResultDto
  label: string
  reconnectConnectionId?: string
  disabled?: boolean
  onLinked?: (connection: CloudProviderConnectionDto) => void
  onCancel: () => void
}

export function HetznerConnectWizard({
  provider,
  start,
  label,
  reconnectConnectionId,
  disabled = false,
  onLinked,
  onCancel,
}: HetznerConnectWizardProps) {
  const queryClient = useQueryClient()
  const [accessToken, setAccessToken] = useState('')

  const completeMutation = useMutation({
    mutationFn: async () =>
      (
        await cloudApi.completeCloudAuth(provider, {
          accessToken: accessToken.trim(),
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

  return (
    <div className="space-y-3 rounded-md border border-amber-200 bg-amber-50/70 p-3">
      <div>
        <p className="text-xs font-semibold text-amber-950">Connect Hetzner project</p>
        <p className="mt-0.5 text-[11px] text-amber-900">
          {start.message
            || 'Create a Read & Write project token in Hetzner Console, then paste it here. This is not OAuth.'}
        </p>
      </div>

      <ol className="list-decimal space-y-1 pl-4 text-[11px] text-amber-950">
        <li>
          Open the{' '}
          <a
            href={HETZNER_CONSOLE}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-0.5 font-medium text-amber-800 underline"
          >
            Hetzner Cloud Console
            <ExternalLink className="h-3 w-3" aria-hidden="true" />
          </a>
          {' '}and select the project you want to connect.
        </li>
        <li>
          Go to <span className="font-medium">Security → API tokens → Generate API token</span>. Choose{' '}
          <span className="font-medium">Read & Write</span> (Read-only cannot manage Cloud Firewalls).
        </li>
        <li>
          Copy the token once (Hetzner shows it only at creation) and paste it below. See the{' '}
          <a
            href={HETZNER_TOKEN_DOCS}
            target="_blank"
            rel="noopener noreferrer"
            className="inline-flex items-center gap-0.5 font-medium text-amber-800 underline"
          >
            token generation guide
            <ExternalLink className="h-3 w-3" aria-hidden="true" />
          </a>
          .
        </li>
      </ol>

      <div>
        <label htmlFor="hetzner-access-token" className="block text-xs font-medium text-amber-950">
          Project API token
        </label>
        <input
          id="hetzner-access-token"
          type="password"
          autoComplete="off"
          value={accessToken}
          disabled={disabled || completeMutation.isPending}
          placeholder="hcloud_…"
          onChange={(event) => setAccessToken(event.target.value)}
          className="mt-1 block w-full rounded-md border border-amber-200 bg-white px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-amber-500"
        />
      </div>

      {completeMutation.isError ? (
        <p className="text-xs text-red-700">
          {extractErrorMessage(completeMutation.error, 'Connection failed - check token.')}
        </p>
      ) : null}

      <div className="flex flex-wrap gap-2">
        <button
          type="button"
          disabled={disabled || completeMutation.isPending || accessToken.trim().length === 0}
          onClick={() => void completeMutation.mutate()}
          className="inline-flex items-center gap-2 rounded-md bg-amber-800 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-900 disabled:opacity-60"
        >
          {completeMutation.isPending ? <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" /> : null}
          Verify token and connect
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
