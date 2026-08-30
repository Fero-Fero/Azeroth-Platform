import { useMutation, useQueryClient } from '@tanstack/react-query'
import { Loader2, Play } from 'lucide-react'
import { STEP_IDS } from '@/setup/constants'
import type { SetupStep, SetupStepContext } from '@/setup/types'
import { setupActionButton } from '@/setup/ui'
import { useStackJob } from '@/hooks/useStackJob'
import { stackKeys } from '@/hooks/useStacks'
import { stackApi } from '@/services/api'
import { StackStatus } from '@/types/stack.types'

export function canStartStack(stack: SetupStepContext['stack']): boolean {
  return (
    stack.status === StackStatus.Stopped ||
    stack.status === StackStatus.Failed ||
    stack.status === StackStatus.Degraded ||
    stack.status === StackStatus.Running
  )
}

type StartStackOptions = {
  id?: string
  label?: string
  when?: (ctx: SetupStepContext) => boolean
  isComplete?: (ctx: SetupStepContext) => boolean
  onStarted?: (ctx: SetupStepContext) => void
}

function StartStackDetails({
  ctx,
  label,
  onStarted,
}: {
  ctx: SetupStepContext
  label: string
  onStarted?: (ctx: SetupStepContext) => void
}) {
  const queryClient = useQueryClient()
  const { job, isStackBusy, applyStatus } = useStackJob(ctx.stack.stackId)
  const isBuilding = ctx.stack.status === StackStatus.Building
  const isTransitioning =
    ctx.stack.status === StackStatus.Starting || ctx.stack.status === StackStatus.Initializing
  const phaseStarting = ctx.status.progress.getPlayerbotsPhase() === 'starting'

  const startMutation = useMutation({
    mutationFn: async () => {
      const canStart =
        ctx.stack.status === StackStatus.Stopped ||
        ctx.stack.status === StackStatus.Failed ||
        ctx.stack.status === StackStatus.Degraded
      const canRestart =
        ctx.stack.status === StackStatus.Running || ctx.stack.status === StackStatus.Degraded
      if (canStart) return stackApi.start(ctx.stack.stackId)
      if (canRestart) return stackApi.restart(ctx.stack.stackId)
      throw new Error('The stack cannot be started right now. Wait for the current operation to finish.')
    },
    onSuccess: (res) => {
      applyStatus(res.data)
      onStarted?.(ctx)
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(ctx.stack.stackId) })
      queryClient.invalidateQueries({ queryKey: stackKeys.lists() })
    },
  })

  const isStarting = startMutation.isPending || isStackBusy || isTransitioning || phaseStarting
  const buttonLabel = isStarting
    ? 'Starting'
    : ctx.stack.status === StackStatus.Running
      ? 'Restart stack'
      : label

  return (
    <div className="space-y-2 text-sm">
      <p>
        Start the entire stack so the worldserver loads with the current configuration. After it is up, continue
        with the remaining setup steps.
      </p>
      {isBuilding && <p>Wait for the current build to finish before starting the stack.</p>}
      {startMutation.isError && (
        <p className="text-red-700">
          {(startMutation.error as Error)?.message ?? 'Failed to start the stack.'}
        </p>
      )}
      {isStackBusy && job?.message && <p>{job.message}</p>}
      {setupActionButton(buttonLabel, () => startMutation.mutate(), {
        disabled: !canStartStack(ctx.stack) || isStarting || isBuilding,
        pending: isStarting,
        icon: isStarting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Play className="h-4 w-4" />,
        tone: 'green',
      })}
    </div>
  )
}

export function startStackStep(options: StartStackOptions = {}): SetupStep {
  const label = options.label ?? 'Start stack'
  return {
    id: options.id ?? STEP_IDS.startStack,
    level: 'warning',
    title: label,
    applies: (ctx) => options.when?.(ctx) ?? canStartStack(ctx.stack),
    isComplete: (ctx) => options.isComplete?.(ctx) ?? ctx.stack.status === StackStatus.Running,
    summary: () => 'Start the stack to continue setup.',
    defaultExpanded: true,
    Component: (ctx) => <StartStackDetails ctx={ctx} label={label} onStarted={options.onStarted} />,
    Action: (ctx) => {
      const queryClient = useQueryClient()
      const { isStackBusy, applyStatus } = useStackJob(ctx.stack.stackId)
      const isBuilding = ctx.stack.status === StackStatus.Building
      const isTransitioning =
        ctx.stack.status === StackStatus.Starting || ctx.stack.status === StackStatus.Initializing
      const phaseStarting = ctx.status.progress.getPlayerbotsPhase() === 'starting'
      const startMutation = useMutation({
        mutationFn: async () => {
          const canStart =
            ctx.stack.status === StackStatus.Stopped ||
            ctx.stack.status === StackStatus.Failed ||
            ctx.stack.status === StackStatus.Degraded
          const canRestart =
            ctx.stack.status === StackStatus.Running || ctx.stack.status === StackStatus.Degraded
          if (canStart) return stackApi.start(ctx.stack.stackId)
          if (canRestart) return stackApi.restart(ctx.stack.stackId)
          throw new Error('The stack cannot be started right now.')
        },
        onSuccess: (res) => {
          applyStatus(res.data)
          options.onStarted?.(ctx)
          queryClient.invalidateQueries({ queryKey: stackKeys.detail(ctx.stack.stackId) })
          queryClient.invalidateQueries({ queryKey: stackKeys.lists() })
        },
      })
      const isStarting = startMutation.isPending || isStackBusy || isTransitioning || phaseStarting
      return setupActionButton(
        isStarting ? 'Starting' : ctx.stack.status === StackStatus.Running ? 'Restart stack' : label,
        () => startMutation.mutate(),
        {
          disabled: !canStartStack(ctx.stack) || isStarting || isBuilding,
          pending: isStarting,
          icon: isStarting ? <Loader2 className="h-4 w-4 animate-spin" /> : <Play className="h-4 w-4" />,
          tone: 'green',
        },
      )
    },
  }
}
