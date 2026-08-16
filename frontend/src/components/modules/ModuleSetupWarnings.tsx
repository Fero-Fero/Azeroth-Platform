import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, BotMessageSquare, CheckCircle2, Loader2, Copy, ShieldAlert, Eye, EyeOff } from 'lucide-react'
import { charactersApi, stackApi } from '@/services/api'
import { stackKeys } from '@/hooks/useStacks'
import type { StackDetailsDto } from '@/types/stack.types'
import { INDIVIDUAL_PROGRESSION_MODULE_ID } from '@/types/individual-progression.types'
import IndividualProgressionPlayerbotsSetupHint from '@/components/modules/IndividualProgressionPlayerbotsSetupHint'

interface ModuleSetupWarningsProps {
  stack: StackDetailsDto
}

const AH_BOT_GUID_KEY = 'AC_AUCTION_HOUSE_BOT_GUIDS'

/**
 * Renders post-setup action warnings for the stack and modules that require additional
 * configuration after the stack is deployed. Each item has its own inline action.
 */
export default function ModuleSetupWarnings({ stack }: ModuleSetupWarningsProps) {
  const queryClient = useQueryClient()

  // ── AH Bot ──────────────────────────────────────────────────────────────────
  const [ahBotDone, setAhBotDone] = useState(false)
  const hasAhBot = stack.configuration.moduleIds?.includes('mod-ah-bot')
  const ahBotGuids = stack.configuration.advanced?.serviceEnvVars?.worldserver?.[AH_BOT_GUID_KEY]
  const ahBotNeedsSetup = hasAhBot && !ahBotGuids

  const hasDungeonSim = stack.configuration.moduleIds?.includes('mod-playerbot-dungeon-sim')
  const hasIndividualProgression = stack.configuration.moduleIds?.includes(INDIVIDUAL_PROGRESSION_MODULE_ID)
  const showIpSetupHint = hasIndividualProgression

  const createAhBotMutation = useMutation({
    mutationFn: async () => {
      const result = await charactersApi.createAhBotAccount(stack.stackId)
      const { allianceGuid, hordeGuid } = result.data
      const guids = [allianceGuid, hordeGuid].sort((a, b) => a - b).join(',')
      await stackApi.applyModuleConfig(stack.stackId, { [AH_BOT_GUID_KEY]: guids })
      return result.data
    },
    onSuccess: () => {
      setAhBotDone(true)
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(stack.stackId) })
    },
  })

  // ── SOAP Admin ──────────────────────────────────────────────────────────────
  const [soapRevealedPassword, setSoapRevealedPassword] = useState<string | null>(null)
  const [soapPasswordVisible, setSoapPasswordVisible] = useState(false)
  const [copiedField, setCopiedField] = useState<'username' | 'password' | null>(null)
  const isDatabaseRunning = stack.services.some(
    (svc) =>
      (svc.service === 'ac-database' || svc.service.includes('database')) && svc.state === 'running',
  )
  const soapNeedsSetup = !stack.isAdminAccountInitialized
  const canInitializeSoap = isDatabaseRunning

  const initSoapMutation = useMutation({
    mutationFn: () => stackApi.initializeAdmin(stack.stackId),
    onSuccess: (data) => {
      if (data.data.created && data.data.password) {
        setSoapRevealedPassword(data.data.password)
      }
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(stack.stackId) })
    },
  })

  const copyToClipboard = async (text: string, field: 'username' | 'password') => {
    await navigator.clipboard.writeText(text)
    setCopiedField(field)
    setTimeout(() => setCopiedField(null), 2000)
  }

  const soapUsername = `acmgr_${stack.stackId.substring(0, 8)}`

  if (!soapNeedsSetup && !ahBotNeedsSetup && !hasDungeonSim && !showIpSetupHint) return null

  return (
    <div className="mb-8 space-y-3">

      {showIpSetupHint && (
        <IndividualProgressionPlayerbotsSetupHint
          stackId={stack.stackId}
          stackStatus={stack.status}
          patchesHref={`/stacks/${stack.stackId}?tab=patches`}
        />
      )}

      {hasDungeonSim && (
        <div className="rounded-lg border border-amber-200 bg-amber-50 px-5 py-4">
          <div className="flex items-start gap-3">
            <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0 text-amber-600" />
            <div>
              <p className="text-sm font-semibold text-amber-900">Playerbot Dungeon Sim — setup notes</p>
              <ul className="mt-1 list-disc space-y-1 pl-5 text-sm text-amber-800">
                <li>
                  Autonomous bot runs need <code className="rounded bg-amber-100 px-1 text-xs">DungeonClear.AllowAutonomousBotRuns = 1</code>{' '}
                  in mod-dungeon-clear config. Upstream jrad7/mod-dungeon-clear may require a fork that exports{' '}
                  <code className="rounded bg-amber-100 px-1 text-xs">StartAutonomousClear</code>.
                </li>
                <li>Apply the module SQL (<code className="rounded bg-amber-100 px-1 text-xs">playerbot_dungeon_progression</code>) to the characters database after install.</li>
                <li>Install the <strong>Dungeon Clear</strong> client addon from the Addons tab for in-game control.</li>
              </ul>
            </div>
          </div>
        </div>
      )}

      {/* SOAP Admin warning */}
      {soapNeedsSetup && (
        <div className="rounded-lg border border-red-200 bg-red-50 px-5 py-4">
          {soapRevealedPassword ? (
            // ── Credentials reveal panel ──
            <div>
              <div className="flex items-start gap-3 mb-4">
                <CheckCircle2 className="mt-0.5 h-5 w-5 shrink-0 text-green-600" />
                <div>
                  <p className="text-sm font-semibold text-green-900">SOAP admin account created</p>
                  <p className="mt-0.5 text-sm text-green-800">
                    Save these credentials — the password will <strong>not</strong> be shown again here.
                    A backup is also written to <code className="text-xs bg-green-100 px-1 rounded">soap-credentials.txt</code> in the stack data directory.
                  </p>
                </div>
              </div>
              <div className="space-y-2 mb-4">
                <div className="flex items-center gap-2 bg-white border border-green-200 rounded-md px-3 py-2">
                  <span className="text-xs text-gray-500 w-20 shrink-0">Username</span>
                  <code className="flex-1 text-sm font-mono text-gray-900">{soapUsername}</code>
                  <button
                    onClick={() => copyToClipboard(soapUsername, 'username')}
                    className="text-gray-400 hover:text-gray-600 transition-colors"
                    title="Copy username"
                  >
                    <Copy className="h-4 w-4" />
                  </button>
                  {copiedField === 'username' && <span className="text-xs text-green-600">Copied!</span>}
                </div>
                <div className="flex items-center gap-2 bg-white border border-green-200 rounded-md px-3 py-2">
                  <span className="text-xs text-gray-500 w-20 shrink-0">Password</span>
                  <code className="flex-1 text-sm font-mono text-gray-900 break-all">
                    {soapPasswordVisible ? soapRevealedPassword : '•'.repeat(32)}
                  </code>
                  <button
                    onClick={() => setSoapPasswordVisible(v => !v)}
                    className="text-gray-400 hover:text-gray-600 transition-colors"
                    title={soapPasswordVisible ? 'Hide' : 'Show'}
                  >
                    {soapPasswordVisible ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
                  </button>
                  <button
                    onClick={() => copyToClipboard(soapRevealedPassword, 'password')}
                    className="text-gray-400 hover:text-gray-600 transition-colors"
                    title="Copy password"
                  >
                    <Copy className="h-4 w-4" />
                  </button>
                  {copiedField === 'password' && <span className="text-xs text-green-600">Copied!</span>}
                </div>
              </div>
              <p className="text-xs text-green-700">
                Restart the stack for the SOAP admin account to become active.
              </p>
            </div>
          ) : (
            // ── Warning + action ──
            <div className="flex items-start justify-between gap-4">
              <div className="flex items-start gap-3">
                <ShieldAlert className="mt-0.5 h-5 w-5 shrink-0 text-red-600" />
                <div>
                  <p className="text-sm font-semibold text-red-900">SOAP admin account required</p>
                  <p className="mt-0.5 text-sm text-red-800">
                    The manager needs a SOAP admin account to send commands to your server (account creation, bans, GM levels, etc.).
                    The account will be created with a unique, auto-generated password. 
                    The database container must be <strong>running</strong> to initialize.
                  </p>
                </div>
              </div>
              <div className="shrink-0">
                {!canInitializeSoap ? (
                  <span className="text-sm text-red-500 italic">Start the database first</span>
                ) : (
                  <button
                    onClick={() => initSoapMutation.mutate()}
                    disabled={initSoapMutation.isPending}
                    className="flex items-center gap-2 rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-700 disabled:cursor-not-allowed disabled:opacity-50 transition-colors"
                  >
                    {initSoapMutation.isPending
                      ? <Loader2 className="h-4 w-4 animate-spin" />
                      : <ShieldAlert className="h-4 w-4" />
                    }
                    Initialize SOAP Admin
                  </button>
                )}
              </div>
            </div>
          )}
          {initSoapMutation.isError && (
            <p className="mt-2 text-sm text-red-700 pl-8">
              {(initSoapMutation.error as any)?.response?.data?.error ?? 'Failed to create admin account — make sure the database container is running.'}
            </p>
          )}
        </div>
      )}

      {/* AH Bot warning */}
      {ahBotNeedsSetup && (
        <div className="rounded-lg border border-amber-200 bg-amber-50 px-5 py-4">
          <div className="flex items-start justify-between gap-4">
            <div className="flex items-start gap-3">
              <AlertTriangle className="mt-0.5 h-5 w-5 shrink-0 text-amber-600" />
              <div>
                <p className="text-sm font-semibold text-amber-900">Auction House Bot — setup required</p>
                <p className="mt-0.5 text-sm text-amber-800">
                  The AH Bot module is installed but no bot characters have been created yet.
                  Click the button to inject a dedicated <strong>AHBOT</strong> account with Alliance and Horde characters
                  directly into the database. The stack needs to be restarted afterwards.
                </p>
              </div>
            </div>

            <div className="shrink-0">
              {ahBotDone ? (
                <span className="flex items-center gap-1.5 text-sm font-medium text-green-700">
                  <CheckCircle2 className="h-4 w-4" />
                  Done — restart stack
                </span>
              ) : (
                <button
                  onClick={() => createAhBotMutation.mutate()}
                  disabled={createAhBotMutation.isPending}
                  className="flex items-center gap-2 rounded-md bg-amber-600 px-4 py-2 text-sm font-medium text-white hover:bg-amber-700 disabled:cursor-not-allowed disabled:opacity-50 transition-colors"
                >
                  {createAhBotMutation.isPending
                    ? <Loader2 className="h-4 w-4 animate-spin" />
                    : <BotMessageSquare className="h-4 w-4" />
                  }
                  Create AH Bot Characters
                </button>
              )}
            </div>
          </div>

          {createAhBotMutation.isError && (
            <p className="mt-2 text-sm text-red-700 pl-8">
              Failed to create characters — make sure the database container is running.
            </p>
          )}
        </div>
      )}
    </div>
  )
}
