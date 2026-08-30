import { ChevronLeft, ChevronRight, Rocket, Loader2 } from 'lucide-react'
import { cn } from '@/lib/utils'

interface WizardNavigationProps {
  currentStep: number
  totalSteps: number
  onBack: () => void
  onNext: () => void
  onSubmit: () => void
  isSubmitting?: boolean
  canGoBack?: boolean
  nextLabel?: string
}

export function WizardNavigation({
  currentStep,
  totalSteps,
  onBack,
  onNext,
  onSubmit,
  isSubmitting = false,
  canGoBack = true,
  nextLabel,
}: WizardNavigationProps) {
  const isLastStep = currentStep === totalSteps - 1

  return (
    <div className="flex items-center justify-between border-t border-gray-200 pt-6">
      <button
        type="button"
        onClick={onBack}
        disabled={!canGoBack || currentStep === 0}
        className={cn(
          'inline-flex items-center gap-2 rounded-md px-4 py-2 text-sm font-medium transition-colors',
          currentStep === 0 || !canGoBack
            ? 'invisible'
            : 'border border-gray-300 bg-white text-gray-700 hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-2'
        )}
        aria-label="Go to previous step"
      >
        <ChevronLeft className="h-4 w-4" aria-hidden="true" />
        Back
      </button>

      {isLastStep ? (
        <button
          type="button"
          onClick={onSubmit}
          disabled={isSubmitting}
          className="inline-flex items-center gap-2 rounded-md bg-green-600 px-6 py-2 text-sm font-medium text-white transition-colors hover:bg-green-700 focus:outline-none focus:ring-2 focus:ring-green-500 focus:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-60"
          aria-label="Create the stack"
        >
          {isSubmitting ? (
            <>
              <Loader2 className="h-4 w-4 animate-spin" aria-hidden="true" />
              Creating…
            </>
          ) : (
            <>
              <Rocket className="h-4 w-4" aria-hidden="true" />
              Create Stack
            </>
          )}
        </button>
      ) : (
        <button
          type="button"
          onClick={onNext}
          className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white transition-colors hover:bg-blue-700 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-2"
          aria-label="Go to next step"
        >
          {nextLabel ?? 'Next'}
          <ChevronRight className="h-4 w-4" aria-hidden="true" />
        </button>
      )}
    </div>
  )
}
