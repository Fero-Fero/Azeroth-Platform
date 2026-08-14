import { useState, type ReactNode } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import {
  BotMessageSquare,
  CheckCircle2,
  Copy,
  Eye,
  EyeOff,
  Loader2,
  ShieldAlert,
} from 'lucide-react'
import IndividualProgressionPlayerbotsSetupHint from '@/components/modules/IndividualProgressionPlayerbotsSetupHint'
import StackStatusItemRow from '@/components/stacks/StackStatusItemRow'
import { stackKeys } from '@/hooks/useStacks'
import { charactersApi, stackApi } from '@/services/api'
import { INDIVIDUAL_PROGRESSION_MODULE_ID } from '@/types/individual-progression.types'
import type { StackDetailsDto } from '@/types/stack.types'

const AH_BOT_GUID_KEY = 'AC_AUCTION_HOUSE_BOT_GUIDS'
interface ModuleSetupStatusRowsProps {
  stack: StackDetailsDto
  onSelectTab: (tab: 'addons') => void
}
function actionButton(
  label: string,
  onClick: () => void,
  options: {
    disabled?: boolean
    pending?: boolean
    icon?: ReactNode
    tone?: 'red' | 'amber' | 'blue'
  } = {},
) {
  const toneClass =
    options.tone === 'red'
      ? 'bg-red-600 hover:bg-red-700'
      : options.tone === 'blue'
        ? 'bg-blue-600 hover:bg-blue-700'
        : 'bg-amber-600 hover:bg-amber-700'

  return (
    <button
      type="button"
      onClick={onClick}
      disabled={options.disabled || options.pending}
      className={`inline-flex items-center gap-1.5 rounded-md px-3 py-1.5 text-sm font-medium text-white disabled:cursor-not-allowed disabled:opacity-50 ${toneClass}`}
    >
      {options.pending ? <Loader2 className="h-4 w-4 animate-spin" /> : options.icon}
      {label}
    </button>
  )
}

export default function ModuleSetupStatusRows({ stack, onSelectTab }: ModuleSetupStatusRowsProps) {
  const queryClient = useQueryClient()

  const [ahBotDone, setAhBotDone] = useState(false)
  const hasAhBot = stack.configuration.moduleIds?.includes('mod-ah-bot')
  const ahBotGuids = stack.configuration.advanced?.customEnvVars?.[AH_BOT_GUID_KEY]
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

  return (
    <>
      {showIpSetupHint && (
        <StackStatusItemRow
          id="ip-playerbots"
          level="warning"
          title="Individual Progression — playerbots setup"
          summary="Disable playerbots before the first launch, prepare server-wide progression on Patches, then re-enable playerbots."
          defaultExpanded
          details={
            <IndividualProgressionPlayerbotsSetupHint
              stackId={stack.stackId}
              stackStatus={stack.status}
              patchesHref={`/stacks/${stack.stackId}?tab=patches`}
              className="border-0 bg-transparent p-0 shadow-none"
            />
          }
        />
      )}

      {hasDungeonSim && (
        <StackStatusItemRow
          id="dungeon-sim"
          level="warning"
          title="Playerbot Dungeon Sim — setup notes"
          summary="Configure mod-dungeon-clear, apply SQL, and install the client addon."
          details={
            <ul className="list-disc space-y-1 pl-5 text-sm">
              <li>
                Autonomous bot runs need{' '}
                <code className="rounded bg-white px-1 text-xs">DungeonClear.AllowAutonomousBotRuns = 1</code>{' '}
                in mod-dungeon-clear config.
              </li>
              <li>
                Apply the module SQL (
                <code className="rounded bg-white px-1 text-xs">playerbot_dungeon_progression</code>) to the
                characters database after install.
              </li>
              <li>
                Install the <strong>Dungeon Clear</strong> client addon from the Addons tab for in-game control.
              </li>
            </ul>
          }
          action={actionButton('Open addons', () => onSelectTab('addons'), { tone: 'amber' })}
        />
      )}

      {soapNeedsSetup && (
        <StackStatusItemRow
          id="soap-admin"
          level={soapRevealedPassword ? 'success' : 'error'}
          title={soapRevealedPassword ? 'SOAP admin account created' : 'SOAP admin account required'}
          summary={
            soapRevealedPassword
              ? 'Save the credentials below — the password will not be shown again here.'
              : 'The manager needs a SOAP admin account to send commands to your server.'
          }
          defaultExpanded={!!soapRevealedPassword}
          details={
            soapRevealedPassword ? (
              <div className="space-y-3">
                <p className="text-sm text-green-800">
                  A backup is also written to{' '}
                  <code className="rounded bg-green-100 px-1 text-xs">soap-credentials.txt</code> in the stack data
                  directory. Restart the stack for the account to become active.
                </p>
                <div className="space-y-2">
                  <div className="flex items-center gap-2 rounded-md border border-green-200 bg-white px-3 py-2">
                    <span className="w-20 shrink-0 text-xs text-gray-500">Username</span>
                    <code className="flex-1 font-mono text-sm text-gray-900">{soapUsername}</code>
                    <button
                      type="button"
                      onClick={() => copyToClipboard(soapUsername, 'username')}
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
                      {soapPasswordVisible ? soapRevealedPassword : '•'.repeat(32)}
                    </code>
                    <button
                      type="button"
                      onClick={() => setSoapPasswordVisible((value) => !value)}
                      className="text-gray-400 transition-colors hover:text-gray-600"
                      title={soapPasswordVisible ? 'Hide' : 'Show'}
                    >
                      {soapPasswordVisible ? <EyeOff className="h-4 w-4" /> : <Eye className="h-4 w-4" />}
                    </button>
                    <button
                      type="button"
                      onClick={() => copyToClipboard(soapRevealedPassword, 'password')}
                      className="text-gray-400 transition-colors hover:text-gray-600"
                      title="Copy password"
                    >
                      <Copy className="h-4 w-4" />
                    </button>
                    {copiedField === 'password' && <span className="text-xs text-green-600">Copied!</span>}
                  </div>
                </div>
              </div>
            ) : (
              <p className="text-sm text-red-800">
                The account will be created with a unique, auto-generated password. The database container
                must be <strong>running</strong> to initialize (full stack start is not required).
                {initSoapMutation.isError && (
                  <span className="mt-2 block text-red-700">
                    {(initSoapMutation.error as { response?: { data?: { error?: string } } })?.response?.data
                      ?.error ?? 'Failed to create admin account — make sure the database container is running.'}
                  </span>
                )}
              </p>
            )
          }
          action={
            soapRevealedPassword ? undefined : !canInitializeSoap ? (
              <span className="text-sm italic text-red-500">Start the database first</span>
            ) : (
              actionButton('Initialize SOAP', () => initSoapMutation.mutate(), {
                pending: initSoapMutation.isPending,
                icon: <ShieldAlert className="h-4 w-4" />,
                tone: 'red',
              })
            )
          }
        />
      )}

      {ahBotNeedsSetup && (
        <StackStatusItemRow
          id="ah-bot"
          level={ahBotDone ? 'success' : 'warning'}
          title="Auction House Bot — setup required"
          summary="Create dedicated AHBOT characters in the database, then restart the stack."
          details={
            <div className="space-y-2 text-sm text-amber-900">
              <p>
                The AH Bot module is installed but no bot characters have been created yet. Click the button to
                inject a dedicated <strong>AHBOT</strong> account with Alliance and Horde characters directly into
                the database.
              </p>
              {createAhBotMutation.isError && (
                <p className="text-red-700">
                  Failed to create characters — make sure the database container is running.
                </p>
              )}
            </div>
          }
          action={
            ahBotDone ? (
              <span className="inline-flex items-center gap-1.5 text-sm font-medium text-green-700">
                <CheckCircle2 className="h-4 w-4" />
                Done — restart stack
              </span>
            ) : (
              actionButton('Create AH Bot', () => createAhBotMutation.mutate(), {
                pending: createAhBotMutation.isPending,
                icon: <BotMessageSquare className="h-4 w-4" />,
                tone: 'amber',
              })
            )
          }
        />
      )}
    </>
  )
}
