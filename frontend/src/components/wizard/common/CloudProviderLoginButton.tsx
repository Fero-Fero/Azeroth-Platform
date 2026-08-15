import { useMutation, useQueryClient } from '@tanstack/react-query'
import { CheckCircle2, Loader2, LogIn } from 'lucide-react'
import { useEffect, useState } from 'react'
import { cloudApi } from '@/services/api'
import {
  CLOUD_OAUTH_MESSAGE_TYPE,
  authMethodLabel,
  isCloudOAuthMessage,
  openCloudOAuthPopup,
  type CloudOAuthMessage,
} from '@/lib/cloud-auth'
import { AwsConnectWizard } from '@/components/wizard/common/AwsConnectWizard'
import {
  CloudAuthMethod,
  CloudLoginMode,
  type CloudAuthProviderStatusDto,
  type CloudAuthStartResultDto,
  type CloudProvider,
  type CloudProviderConnectionDto,
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

interface CloudProviderLoginButtonProps {
  provider: CloudProvider
  status?: CloudAuthProviderStatusDto
  disabled?: boolean
  reconnectConnectionId?: string
  label?: string
  linkedConnection?: CloudProviderConnectionDto | null
  onLinked?: (connection: CloudProviderConnectionDto) => void
  onDisconnected?: () => void
  onRequiresManualCredentials?: (message?: string) => void
}

export function CloudProviderLoginButton({
  provider,
  status,
  disabled = false,
  reconnectConnectionId,
  label,
  linkedConnection,
  onLinked,
  onDisconnected,
  onRequiresManualCredentials,
}: CloudProviderLoginButtonProps) {
  const queryClient = useQueryClient()
  const [popupError, setPopupError] = useState<string | null>(null)
  const [awsStart, setAwsStart] = useState<CloudAuthStartResultDto | null>(null)

  const startMutation = useMutation({
    mutationFn: async () =>
      (
        await cloudApi.startCloudAuth(provider, {
          reconnectConnectionId,
          label,
          externalId: awsStart?.externalId,
        })
      ).data,
    onSuccess: async (result) => {
      setPopupError(null)
      if (result.requiresManualCredentials) {
        onRequiresManualCredentials?.(result.message)
        return
      }

      if (result.externalId && (result.awsTemplates?.length ?? 0) > 0) {
        setAwsStart(result)
        return
      }

      if (!result.authorizationUrl) {
        setPopupError(result.message || 'Sign-in did not return an authorization URL.')
        return
      }

      const popup = openCloudOAuthPopup(result.authorizationUrl)
      if (!popup) {
        setPopupError('Pop-up was blocked. Allow pop-ups for this site and try again.')
      }
    },
    onError: (error: unknown) => {
      setPopupError(extractErrorMessage(
        error,
        status?.loginMode === CloudLoginMode.AssumedRole
          ? 'Failed to start AWS account connect.'
          : 'Failed to start cloud sign-in.'
      ))
    },
  })

  const disconnectMutation = useMutation({
    mutationFn: async (connectionId: string) => {
      await cloudApi.deleteConnection(connectionId)
    },
    onSuccess: async (_, connectionId) => {
      setPopupError(null)
      queryClient.setQueryData(
        ['cloud-connections'],
        (current: import('@/types/stack.types').CloudProviderConnectionDto[] | undefined) =>
          Array.isArray(current) ? current.filter((connection) => connection.id !== connectionId) : current
      )
      await queryClient.invalidateQueries({ queryKey: ['cloud-connections'] })
      await queryClient.invalidateQueries({ queryKey: ['cloud-audit-logs'] })
      onDisconnected?.()
    },
    onError: (error: unknown) => {
      setPopupError(extractErrorMessage(error, 'Could not disconnect this cloud account.'))
    },
  })

  useEffect(() => {
    const handleMessage = (event: MessageEvent) => {
      if (event.origin !== window.location.origin || !isCloudOAuthMessage(event.data)) {
        return
      }

      const payload: CloudOAuthMessage = event.data
      if (payload.provider && payload.provider !== provider) {
        return
      }

      if (payload.status === 'error') {
        setPopupError(payload.message || 'Cloud sign-in failed.')
        return
      }

      setPopupError(null)
      if (payload.connectionId) {
        void queryClient.invalidateQueries({ queryKey: ['cloud-connections'] })
        void queryClient.invalidateQueries({ queryKey: ['cloud-audit-logs'] })
        void cloudApi.listConnections().then((response) => {
          const match = response.data.find((connection) => connection.id === payload.connectionId)
          if (match) {
            onLinked?.(match)
          }
        })
      }
    }

    window.addEventListener('message', handleMessage)
    return () => window.removeEventListener('message', handleMessage)
  }, [onLinked, provider, queryClient])

  if (linkedConnection && !linkedConnection.needsReauth && !reconnectConnectionId) {
    const connected =
      linkedConnection.authMethod === CloudAuthMethod.AssumedRole ? 'Connected as' : 'Signed in as'
    return (
      <div className="space-y-1.5">
        <div className="flex flex-wrap items-center gap-2">
          <p className="inline-flex items-center gap-1.5 text-xs text-green-800">
            <CheckCircle2 className="h-3.5 w-3.5 shrink-0" aria-hidden="true" />
            {connected} {linkedConnection.accountHint || linkedConnection.label}
            {linkedConnection.authMethod ? ` (${authMethodLabel(linkedConnection.authMethod)})` : ''}
          </p>
          <button
            type="button"
            disabled={disabled || disconnectMutation.isPending}
            onClick={() => void disconnectMutation.mutate(linkedConnection.id)}
            className="text-xs font-medium text-gray-700 underline-offset-2 hover:underline disabled:opacity-60"
          >
            {disconnectMutation.isPending ? 'Disconnecting…' : 'Disconnect'}
          </button>
        </div>
        {popupError ? <p className="text-xs text-red-700">{popupError}</p> : null}
      </div>
    )
  }

  const requiresPlatformConfig =
    status?.loginMode === CloudLoginMode.OAuth
    || status?.loginMode === CloudLoginMode.DeviceCode
  const canStart = Boolean(status?.isImplemented && (!requiresPlatformConfig || status.isConfigured))
  const reason = status?.unavailableReason

  if (awsStart) {
    return (
      <AwsConnectWizard
        provider={provider}
        start={awsStart}
        label={label ?? ''}
        reconnectConnectionId={reconnectConnectionId}
        disabled={disabled}
        onLinked={(connection) => {
          setAwsStart(null)
          onLinked?.(connection)
        }}
        onCancel={() => setAwsStart(null)}
      />
    )
  }

  return (
    <div className="space-y-1.5">
      <button
        type="button"
        disabled={disabled || startMutation.isPending || !canStart}
        title={!canStart ? reason || undefined : undefined}
        onClick={() => void startMutation.mutate()}
        className={cn(
          'inline-flex items-center gap-2 rounded-md bg-blue-600 px-3 py-1.5 text-xs font-semibold text-white hover:bg-blue-700 disabled:opacity-60'
        )}
      >
        {startMutation.isPending ? (
          <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
        ) : (
          <LogIn className="h-3.5 w-3.5" aria-hidden="true" />
        )}
        {reconnectConnectionId ? 'Reconnect' : status?.signInLabel || 'Sign in'}
      </button>
      {!canStart && reason ? <p className="text-[11px] text-gray-600">{reason}</p> : null}
      {popupError ? <p className="text-xs text-red-700">{popupError}</p> : null}
      <span className="sr-only" data-oauth-message-type={CLOUD_OAUTH_MESSAGE_TYPE} />
    </div>
  )
}
