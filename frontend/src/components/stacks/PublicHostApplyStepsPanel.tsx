import { AlertCircle, CheckCircle2, Circle, CircleDot, Loader2, MinusCircle } from 'lucide-react'
import type { PublicHostApplyStepStatus, StackJobStatus } from '@/types/stack.types'

/**
 * Step progress for the detached ApplyPublicHost background job. Shown beside the Stack IP address
 * field so it stays near the action that started it; status survives tab switches and refreshes.
 */
export function PublicHostApplyStepsPanel({ job }: { job: StackJobStatus }) {
  const failed = job.phase === 'Failed'
  const done = job.phase === 'Completed'
  const running = job.isRunning
  const visibleSteps = (job.steps ?? []).filter((step) => step.status !== 'Skipped' || step.detail)

  return (
    <div className="mt-2 rounded-md border border-blue-200 bg-blue-50 px-3 py-2">
      <p className="text-sm font-medium text-blue-900">
        {failed ? 'Stack IP apply failed' : done ? 'Stack IP apply complete' : 'Applying stack IP address…'}
      </p>
      {running ? (
        <p className="mt-1 text-xs text-blue-800">
          This runs in the background - you can refresh or switch tabs without cancelling it.
        </p>
      ) : null}
      <ul className="mt-2 space-y-1">
        {visibleSteps.map((step) => (
          <li key={step.id} className="flex items-start gap-2 text-xs text-gray-700">
            <StepIcon status={step.status} />
            <span>
              {step.label}
              {step.detail ? <span className="text-gray-500"> - {step.detail}</span> : null}
            </span>
          </li>
        ))}
      </ul>
      {failed && job.error ? <p className="mt-2 text-xs text-red-700">{job.error}</p> : null}
    </div>
  )
}

function StepIcon({ status }: { status: PublicHostApplyStepStatus }) {
  switch (status) {
    case 'Running':
      return <Loader2 className="mt-0.5 h-3.5 w-3.5 shrink-0 animate-spin text-blue-600" />
    case 'Completed':
      return <CheckCircle2 className="mt-0.5 h-3.5 w-3.5 shrink-0 text-green-600" />
    case 'Skipped':
      return <MinusCircle className="mt-0.5 h-3.5 w-3.5 shrink-0 text-gray-400" />
    case 'Failed':
      return <AlertCircle className="mt-0.5 h-3.5 w-3.5 shrink-0 text-red-600" />
    default:
      return status === 'Pending' ? (
        <Circle className="mt-0.5 h-3.5 w-3.5 shrink-0 text-gray-300" />
      ) : (
        <CircleDot className="mt-0.5 h-3.5 w-3.5 shrink-0 text-gray-300" />
      )
  }
}

export function isPublicHostApplyJob(job: StackJobStatus | null | undefined): job is StackJobStatus {
  return job?.action === 'ApplyPublicHost'
}
