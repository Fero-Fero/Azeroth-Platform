import StackStatusItemRow from '@/components/stacks/StackStatusItemRow'
import { collectAllSteps } from '@/setup/collectAllSteps'
import { countSetupProgress, resolveVisibleSteps } from '@/setup/resolveVisibleSteps'
import SetupProgressBar from '@/setup/SetupProgressBar'
import type { SetupStepContext, SetupTabId } from '@/setup/types'
import { useSetupStepContext } from '@/setup/useSetupStepContext'
import type { StackDetailsDto } from '@/types/stack.types'

interface StackSetupOverviewProps {
  stack: StackDetailsDto
  onSelectTab: (tab: SetupTabId) => void
  ctx?: SetupStepContext
}

export default function StackSetupOverview({ stack, onSelectTab, ctx: providedCtx }: StackSetupOverviewProps) {
  const localCtx = useSetupStepContext(stack, onSelectTab)
  const ctx = providedCtx ?? localCtx
  const allSteps = collectAllSteps(ctx)
  const visible = resolveVisibleSteps(allSteps, ctx)
  const progress = countSetupProgress(allSteps, ctx)

  if (visible.length === 0) return null

  return (
    <>
      <SetupProgressBar progress={progress} />
      {visible.map((step) => (
        <StackStatusItemRow
          key={step.id}
          id={step.id}
          level={step.level}
          title={step.title}
          summary={step.summary(ctx)}
          defaultExpanded={step.defaultExpanded}
          details={<step.Component {...ctx} />}
          action={step.Action ? <step.Action {...ctx} /> : undefined}
        />
      ))}
    </>
  )
}
