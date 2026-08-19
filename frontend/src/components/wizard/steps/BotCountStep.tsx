import { FormField } from '@/components/wizard/common/FormField'
import type { WizardForm } from '@/components/wizard/types'
import { cn } from '@/lib/utils'

interface BotCountStepProps {
  form: WizardForm
}

export function BotCountStep({ form }: BotCountStepProps) {
  const {
    register,
    watch,
    formState: { errors },
  } = form
  const count = watch('randomBotCount') ?? 0

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-xl font-semibold text-gray-900">Random bots</h2>
        <p className="mt-1 text-sm text-gray-500">
          How many random playerbots should log in after Express Setup finishes. 0 keeps bots installed
          but not autologging.
        </p>
      </div>

      <FormField
        label="Random bot count"
        htmlFor="randomBotCount"
        error={errors.randomBotCount?.message}
        hint="0–2500. Written to playerbots.conf after the first patch is applied."
        required
      >
        <input
          id="randomBotCount"
          type="number"
          min={0}
          max={2500}
          className={cn(
            'block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
            errors.randomBotCount ? 'border-red-400' : 'border-gray-300',
          )}
          {...register('randomBotCount', { valueAsNumber: true })}
        />
      </FormField>

      <p className="text-sm text-gray-600">
        {count === 0
          ? 'Random bots will stay offline until you raise this later.'
          : `${count} random bots will autologin on the real start.`}
      </p>
    </div>
  )
}
