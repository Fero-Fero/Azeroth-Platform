import { useEffect } from 'react'
import { useSearchParams } from 'react-router-dom'
import { CLOUD_OAUTH_MESSAGE_TYPE, type CloudOAuthMessage } from '@/lib/cloud-auth'

export default function CloudOAuthCallbackPage() {
  const [params] = useSearchParams()
  const status = params.get('status') === 'success' ? 'success' : 'error'
  const provider = params.get('provider') ?? undefined
  const connectionId = params.get('connectionId') ?? undefined
  const message = params.get('message') ?? undefined

  useEffect(() => {
    const payload: CloudOAuthMessage = {
      type: CLOUD_OAUTH_MESSAGE_TYPE,
      status,
      provider,
      connectionId,
      message,
    }

    if (window.opener && !window.opener.closed) {
      window.opener.postMessage(payload, window.location.origin)
      window.close()
    }
  }, [connectionId, message, provider, status])

  return (
    <div className="flex min-h-screen items-center justify-center bg-gray-50 px-6">
      <div className="max-w-md rounded-lg border border-gray-200 bg-white p-6 text-center shadow-sm">
        <h1 className="text-lg font-semibold text-gray-900">
          {status === 'success' ? 'Cloud account linked' : 'Cloud sign-in did not finish'}
        </h1>
        <p className="mt-2 text-sm text-gray-600">
          {status === 'success'
            ? 'You can close this window and return to Azeroth Platform.'
            : message || 'Start sign-in again from Cloud settings or the Create Stack wizard.'}
        </p>
      </div>
    </div>
  )
}
