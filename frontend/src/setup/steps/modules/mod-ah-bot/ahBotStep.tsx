import { useState } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { BotMessageSquare, CheckCircle2 } from 'lucide-react'
import { AH_BOT_GUID_KEY, MODULE_IDS, STEP_IDS } from '@/setup/constants'
import { hasModule } from '@/setup/stackServices'
import type { SetupStep, SetupStepContext } from '@/setup/types'
import { setupActionButton } from '@/setup/ui'
import { stackKeys } from '@/hooks/useStacks'
import { charactersApi, stackApi } from '@/services/api'

function ahBotGuids(ctx: SetupStepContext): string | undefined {
  return ctx.stack.configuration.advanced?.serviceEnvVars?.worldserver?.[AH_BOT_GUID_KEY]
}

function AhBotDetails(ctx: SetupStepContext) {
  const queryClient = useQueryClient()
  const [done, setDone] = useState(false)

  const createAhBotMutation = useMutation({
    mutationFn: async () => {
      const result = await charactersApi.createAhBotAccount(ctx.stack.stackId)
      const { allianceGuid, hordeGuid } = result.data
      const guids = [allianceGuid, hordeGuid].sort((a, b) => a - b).join(',')
      await stackApi.applyModuleConfig(ctx.stack.stackId, { [AH_BOT_GUID_KEY]: guids })
      return result.data
    },
    onSuccess: () => {
      setDone(true)
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(ctx.stack.stackId) })
    },
  })

  return (
    <div className="space-y-2 text-sm text-amber-900">
      <p>
        The AH Bot module is installed but no bot characters have been created yet. Click the button to inject
        a dedicated <strong>AHBOT</strong> account with Alliance and Horde characters directly into the
        database.
      </p>
      {createAhBotMutation.isError && (
        <p className="text-red-700">Failed to create characters - make sure the database container is running.</p>
      )}
      {done && (
        <p className="inline-flex items-center gap-1.5 font-medium text-green-700">
          <CheckCircle2 className="h-4 w-4" />
          Done - restart stack
        </p>
      )}
    </div>
  )
}

function AhBotAction(ctx: SetupStepContext) {
  const queryClient = useQueryClient()
  const [done, setDone] = useState(false)

  const createAhBotMutation = useMutation({
    mutationFn: async () => {
      const result = await charactersApi.createAhBotAccount(ctx.stack.stackId)
      const { allianceGuid, hordeGuid } = result.data
      const guids = [allianceGuid, hordeGuid].sort((a, b) => a - b).join(',')
      await stackApi.applyModuleConfig(ctx.stack.stackId, { [AH_BOT_GUID_KEY]: guids })
      return result.data
    },
    onSuccess: () => {
      setDone(true)
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(ctx.stack.stackId) })
    },
  })

  if (done || ahBotGuids(ctx)) {
    return (
      <span className="inline-flex items-center gap-1.5 text-sm font-medium text-green-700">
        <CheckCircle2 className="h-4 w-4" />
        Done - restart stack
      </span>
    )
  }

  return setupActionButton('Create AH Bot', () => createAhBotMutation.mutate(), {
    pending: createAhBotMutation.isPending,
    icon: <BotMessageSquare className="h-4 w-4" />,
  })
}

export function ahBotStep(): SetupStep {
  return {
    id: STEP_IDS.ahBot,
    moduleId: MODULE_IDS.ahBot,
    level: 'warning',
    title: 'Auction House Bot - setup required',
    applies: (ctx) => hasModule(ctx.stack, MODULE_IDS.ahBot) && !ahBotGuids(ctx),
    isComplete: (ctx) => !!ahBotGuids(ctx),
    summary: () => 'Create dedicated AHBOT characters in the database, then restart the stack.',
    Component: (ctx) => <AhBotDetails {...ctx} />,
    Action: (ctx) => <AhBotAction {...ctx} />,
  }
}
