import { useState } from 'react'
import { Eye, EyeOff } from 'lucide-react'
import { FormField } from '@/components/wizard/common/FormField'
import type { WizardForm } from '@/components/wizard/types'
import { cn } from '@/lib/utils'

interface DatabaseStepProps {
  form: WizardForm
}

export function DatabaseStep({ form }: DatabaseStepProps) {
  const {
    register,
    formState: { errors },
  } = form
  const [showPassword, setShowPassword] = useState(false)

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-xl font-semibold text-gray-900">Database Configuration</h2>
        <p className="mt-1 text-sm text-gray-500">
          Configure the MySQL database that AzerothCore will use.
        </p>
      </div>

      <FormField
        label="Root Password"
        htmlFor="db-password"
        error={errors.database?.rootPassword?.message}
        hint="Used for the MySQL root account. At least 8 characters."
        required
      >
        <div className="relative">
          <input
            id="db-password"
            type={showPassword ? 'text' : 'password'}
            autoComplete="new-password"
            className={cn(
              'block w-full rounded-md border px-3 py-2 pr-10 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
              errors.database?.rootPassword ? 'border-red-400' : 'border-gray-300'
            )}
            {...register('database.rootPassword')}
          />
          <button
            type="button"
            className="absolute inset-y-0 right-0 flex items-center pr-3 text-gray-400 hover:text-gray-600"
            onClick={() => setShowPassword((value) => !value)}
            aria-label={showPassword ? 'Hide password' : 'Show password'}
          >
            {showPassword ? (
              <EyeOff className="h-4 w-4" aria-hidden="true" />
            ) : (
              <Eye className="h-4 w-4" aria-hidden="true" />
            )}
          </button>
        </div>
      </FormField>

      <FormField
        label="MySQL Port"
        htmlFor="db-port"
        error={errors.database?.port?.message}
        hint="Default: 3306. Change if you have port conflicts."
        required
      >
        <input
          id="db-port"
          type="number"
          min={1024}
          max={65535}
          className={cn(
            'block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
            errors.database?.port ? 'border-red-400' : 'border-gray-300'
          )}
          {...register('database.port', { valueAsNumber: true })}
        />
      </FormField>
    </div>
  )
}
