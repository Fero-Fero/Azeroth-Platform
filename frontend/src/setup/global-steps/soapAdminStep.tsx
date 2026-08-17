import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Copy, Eye, EyeOff, ShieldAlert } from 'lucide-react'
import { STEP_IDS } from '@/setup/constants'
import { isDatabaseRunning } from '@/setup/stackServices'
import type { SetupStep, SetupStepContext } from '@/setup/types'
import { setupActionButton } from '@/setup/ui'
import { stackKeys } from '@/hooks/useStacks'
import { stackApi } from '@/services/api'

const SOAP_CREDENTIALS_FLAG = 'soap-credentials-visible'
const SOAP_PASSWORD_FLAG = 'soap-password'

function soapUsername(stackId: string) {
  return `acmgr_${stackId.substring(0, 8)}`
}

function SoapDetails(ctx: SetupStepContext) {
  const [passwordVisible, setPasswordVisible] = useState(false)
  const [copiedField, setCopiedField] = useState<'username' | 'password' | null>(null)
  const revealedPassword = ctx.status.progress.getSessionFlag(SOAP_PASSWORD_FLAG)

  const copyToClipboard = async (text: string, field: 'username' | 'password') => {
    await navigator.clipboard.writeText(text)
    setCopiedField(field)
    setTimeout(() => setCopiedField(null), 2000)
  }

  const username = soapUsername(ctx.stack.stackId)

  if (revealedPassword) {
    return (
      <div className="space-y-3">
        <p className="text-sm text-green-800">
          A backup is also written to{' '}
          <code className="rounded bg-green-100 px-1 text-xs">soap-credentials.txt</code> in the stack data
          directory. Restart the stack for the account to become active.
        </p>
        <div className="space-y-2">
          <div className="flex items-center gap-2 rounded-md border border-green-200 bg-white px-3 py-2">
            <span className="w-20 shrink-0 text-xs text-gray-500">Username</span>
            <code className="flex-1 font-mono text-sm text-gray-900">{username}</code>
            <button
              type="button"
              onClick={() => copyToClipboard(username, 'username')}
              className="text-gray-400 transition-colors hover:text-gray-600"
              title="Copy username"
            >
              <Copy className="h-4 w-4" />
            </button>
            {copiedField === 'username' && <span className="text-xs text-green-600">Copied!</span>}
          </div>
          <div className="flex items-center gap-2 rounded-md border border-green-200 bg-white px-3 py-2">
            <span className="w-20 shrink-0 text-xs text-gray-500">Password</span>
            <code className="flex-1 break-all font-mono text-sm text-gray-900">
              {passwordVisible ? revealedPassword : '•'.repeat(32)}
            </code>
            <button
              type="button"
              onClick={() => setPasswordVisible((value) => !value)}
              className="text-gray-400 transition-colors hover:text-gray-600"
              title={passwordVisible ? 'Hide' : 'Show'}
            >
              {passwordVisible ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
            </button>
            <button
              type="button"
              onClick={() => copyToClipboard(revealedPassword, 'password')}
              className="text-gray-400 transition-colors hover:text-gray-600"
              title="Copy password"
            >
              <Copy className="h-4 w-4" />
            </button>
            {copiedField === 'password' && <span className="text-xs text-green-600">Copied!</span>}
          </div>
        </div>
      </div>
    )
  }

  return (
    <p className="text-sm text-red-800">
      The account will be created with a unique, auto-generated password. The database container must be{' '}
      <strong>running</strong> to initialize (full stack start is not required).
    </p>
  )
}

function SoapAction(ctx: SetupStepContext) {
  const queryClient = useQueryClient()
  const revealedPassword = ctx.status.progress.getSessionFlag(SOAP_PASSWORD_FLAG)
  const canInitialize = isDatabaseRunning(ctx.stack)

  const initSoapMutation = useMutation({
    mutationFn: () => stackApi.initializeAdmin(ctx.stack.stackId),
    onSuccess: (data) => {
      if (data.data.created && data.data.password) {
        ctx.status.progress.setSessionFlag(SOAP_PASSWORD_FLAG, data.data.password)
        ctx.status.progress.setSessionFlag(SOAP_CREDENTIALS_FLAG, '1')
      }
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(ctx.stack.stackId) })
    },
  })

  if (revealedPassword) return null
  if (!canInitialize) {
    return <span className="text-sm italic text-red-500">Start the database first</span>
  }

  return setupActionButton('Initialize SOAP', () => initSoapMutation.mutate(), {
    pending: initSoapMutation.isPending,
    icon: <ShieldAlert className="h-4 w-4" />,
    tone: 'red',
  })
}

export function soapAdminStep(): SetupStep {
  return {
    id: STEP_IDS.soapAdmin,
    level: 'error',
    title: 'SOAP admin account required',
    defaultExpanded: true,
    applies: (ctx) =>
      !ctx.status.soapInitialized || ctx.status.progress.getSessionFlag(SOAP_CREDENTIALS_FLAG) === '1',
    isComplete: (ctx) =>
      ctx.status.soapInitialized && ctx.status.progress.getSessionFlag(SOAP_CREDENTIALS_FLAG) !== '1',
    showWhenComplete: (ctx) => ctx.status.progress.getSessionFlag(SOAP_CREDENTIALS_FLAG) === '1',
    summary: (ctx) =>
      ctx.status.progress.getSessionFlag(SOAP_PASSWORD_FLAG)
        ? 'Save the credentials below — the password will not be shown again here.'
        : 'The manager needs a SOAP admin account to send commands to your server.',
    Component: (ctx) => <SoapDetails {...ctx} />,
    Action: (ctx) => <SoapAction {...ctx} />,
  }
}
