import { CheckCircle2, Loader2, XCircle } from 'lucide-react'
import type { RemoteConnectionTestResultDto } from '@/types/stack.types'
import { cn } from '@/lib/utils'

function sshCheckPassed(result: RemoteConnectionTestResultDto | null): boolean {
  return result?.prerequisites?.some((check) => check.name === 'SSH' && check.passed) ?? false
}

interface VpcConnectionTestFooterProps {
  method: 'manual' | 'cloud'
  connectionFieldsReady: boolean
  credentialsReady: boolean
  testing: boolean
  disabled?: boolean
  onTestConnection: () => void
  testResult: RemoteConnectionTestResultDto | null
}

export function VpcConnectionTestFooter({
  method,
  connectionFieldsReady,
  credentialsReady,
  testing,
  disabled = false,
  onTestConnection,
  testResult,
}: VpcConnectionTestFooterProps) {
  const canTest = credentialsReady && !disabled && !testing
  const sshPassed = sshCheckPassed(testResult)

  const hint = (() => {
    if (credentialsReady) {
      return null
    }
    if (!connectionFieldsReady) {
      return method === 'cloud'
        ? 'Select or launch a server above first.'
        : 'Enter host and SSH user above first.'
    }
    return 'Add an SSH key above to test the connection.'
  })()

  return (
    <div className="mt-4 border-t border-gray-200 pt-3">
      <div className="flex flex-wrap items-center gap-2">
        <button
          type="button"
          onClick={() => void onTestConnection()}
          disabled={!canTest}
          className="inline-flex items-center gap-2 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:opacity-60"
        >
          {testing ? (
            <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
          ) : null}
          Test connection
        </button>
        {hint ? <span className="text-[11px] text-gray-500">{hint}</span> : null}
      </div>

      {testing ? (
        <p className="mt-2 inline-flex items-center gap-1.5 text-xs text-gray-600">
          <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
          Checking SSH connection…
        </p>
      ) : null}

      {testResult && !testing ? (
        <div
          className={cn(
            'mt-2 rounded-md border px-3 py-2 text-xs',
            sshPassed ? 'border-green-200 bg-green-50 text-green-900' : 'border-red-200 bg-red-50 text-red-900'
          )}
        >
          <p className="flex items-start gap-1.5 font-medium">
            {sshPassed ? (
              <CheckCircle2 className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
            ) : (
              <XCircle className="mt-0.5 h-3.5 w-3.5 shrink-0" aria-hidden="true" />
            )}
            {sshPassed ? 'SSH connection successful' : 'SSH connection failed'}
          </p>
          {testResult.message ? <p className="mt-1 text-[11px] opacity-90">{testResult.message}</p> : null}
          {testResult.prerequisites
            ?.filter((check) => check.name === 'SSH')
            .map((check) => (
              <p key={check.name} className="mt-1 text-[11px] opacity-90">
                {check.message}
              </p>
            ))}
        </div>
      ) : null}
    </div>
  )
}
