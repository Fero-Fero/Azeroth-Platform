import { FormField } from '@/components/wizard/common/FormField'
import type { WizardForm } from '@/components/wizard/types'
import { cn } from '@/lib/utils'

interface PortsStepProps {
  form: WizardForm
}

const PORT_FIELDS = [
  { name: 'ports.authServer' as const, label: 'Auth Server Port', hint: 'Default: 3724 — WoW login server', id: 'port-auth' },
  { name: 'ports.worldServer' as const, label: 'World Server Port', hint: 'Default: 8085 — game world connection', id: 'port-world' },
  { name: 'ports.soapPort' as const, label: 'SOAP Admin Port', hint: 'Default: 7878 — remote admin interface', id: 'port-soap' },
] as const

export function PortsStep({ form }: PortsStepProps) {
  const {
    register,
    formState: { errors },
  } = form

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-xl font-semibold text-gray-900">Port Configuration</h2>
        <p className="mt-1 text-sm text-gray-500">
          Set the host ports for AzerothCore services. Make sure these are not in use by other applications.
        </p>
      </div>

      <div className="grid gap-4 sm:grid-cols-3">
        {PORT_FIELDS.map(({ name, label, hint, id }) => {
          const fieldName = name.split('.')[1] as 'authServer' | 'worldServer' | 'soapPort'
          const error = errors.ports?.[fieldName]?.message

          return (
            <FormField key={name} label={label} htmlFor={id} error={error} hint={hint} required>
              <input
                id={id}
                type="number"
                min={1024}
                max={65535}
                className={cn(
                  'block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
                  error ? 'border-red-400' : 'border-gray-300'
                )}
                {...register(name, { valueAsNumber: true })}
              />
            </FormField>
          )
        })}
      </div>
    </div>
  )
}
