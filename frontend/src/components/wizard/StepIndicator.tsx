import { Check } from 'lucide-react'
import { cn } from '@/lib/utils'

export interface WizardStep {
  id: string
  label: string
}

interface StepIndicatorProps {
  steps: WizardStep[]
  currentStep: number
}

export function StepIndicator({ steps, currentStep }: StepIndicatorProps) {
  return (
    <nav aria-label="Setup wizard progress" className="flex items-center justify-center gap-0 overflow-x-auto py-2">
      {steps.map((step, index) => {
        const isCompleted = index < currentStep
        const isActive = index === currentStep
        const isPending = index > currentStep

        return (
          <div key={step.id} className="flex items-center">
            {index > 0 && (
              <div
                className={cn(
                  'h-0.5 w-8 shrink-0 sm:w-12',
                  isCompleted ? 'bg-blue-600' : 'bg-gray-200'
                )}
                aria-hidden="true"
              />
            )}

            <div className="flex shrink-0 flex-col items-center gap-1">
              <div
                className={cn(
                  'flex h-9 w-9 items-center justify-center rounded-full border-2 text-sm font-semibold transition-colors',
                  isCompleted && 'border-blue-600 bg-blue-600 text-white',
                  isActive && 'border-blue-600 bg-white text-blue-600',
                  isPending && 'border-gray-200 bg-white text-gray-400'
                )}
                aria-current={isActive ? 'step' : undefined}
                aria-label={`Step ${index + 1}: ${step.label}${isCompleted ? ' (completed)' : isActive ? ' (current)' : ''}`}
              >
                {isCompleted ? <Check className="h-4 w-4" aria-hidden="true" /> : index + 1}
              </div>
              <span
                className={cn(
                  'hidden text-xs font-medium sm:block',
                  isCompleted && 'text-blue-600',
                  isActive && 'text-blue-700',
                  isPending && 'text-gray-400'
                )}
              >
                {step.label}
              </span>
            </div>
          </div>
        )
      })}
    </nav>
  )
}
