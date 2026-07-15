import { FormField } from '@/components/wizard/common/FormField'
import type { WizardForm } from '@/components/wizard/types'
import { DEFAULT_VERIFICATION_BODY_HTML, DEFAULT_VERIFICATION_SUBJECT } from '@/lib/armory-email-defaults'
import { cn } from '@/lib/utils'

interface EmailConfirmationStepProps {
  form: WizardForm
}

export function EmailConfirmationStep({ form }: EmailConfirmationStepProps) {
  const {
    register,
    watch,
    setValue,
    formState: { errors },
  } = form

  const realmName = watch('advanced.realmName') || 'Armory'
  const emailErrors = errors.armoryAccounts?.email

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-xl font-semibold text-gray-900">Email Delivery</h2>
        <p className="mt-1 text-sm text-gray-500">
          Configure SMTP so the armory can send verification emails. You can skip this step and set it up
          later from the stack overview.
        </p>
      </div>

      <div className="grid gap-4 sm:grid-cols-2">
        <FormField
          label="SMTP Host"
          htmlFor="smtp-host"
          error={emailErrors?.smtpHost?.message}
          required
        >
          <input
            id="smtp-host"
            type="text"
            placeholder="smtp.example.com"
            className={cn(
              'block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
              emailErrors?.smtpHost ? 'border-red-400' : 'border-gray-300'
            )}
            {...register('armoryAccounts.email.smtpHost')}
          />
        </FormField>

        <FormField
          label="SMTP Port"
          htmlFor="smtp-port"
          error={emailErrors?.smtpPort?.message}
          required
        >
          <input
            id="smtp-port"
            type="number"
            min={1}
            max={65535}
            className={cn(
              'block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
              emailErrors?.smtpPort ? 'border-red-400' : 'border-gray-300'
            )}
            {...register('armoryAccounts.email.smtpPort', { valueAsNumber: true })}
          />
        </FormField>
      </div>

      <FormField
        label="Security"
        htmlFor="smtp-security"
        error={emailErrors?.smtpSecurity?.message}
        required
      >
        <select
          id="smtp-security"
          className={cn(
            'block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
            emailErrors?.smtpSecurity ? 'border-red-400' : 'border-gray-300'
          )}
          {...register('armoryAccounts.email.smtpSecurity')}
        >
          <option value="starttls">STARTTLS (recommended)</option>
          <option value="tls">TLS / SSL</option>
          <option value="none">None</option>
        </select>
      </FormField>

      <div className="grid gap-4 sm:grid-cols-2">
        <FormField
          label="SMTP Username"
          htmlFor="smtp-username"
          error={emailErrors?.smtpUsername?.message}
          hint="Leave blank if your relay does not require authentication"
        >
          <input
            id="smtp-username"
            type="text"
            autoComplete="off"
            className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            {...register('armoryAccounts.email.smtpUsername')}
          />
        </FormField>

        <FormField
          label="SMTP Password"
          htmlFor="smtp-password"
          error={emailErrors?.smtpPassword?.message}
          hint="Stored encrypted on the platform"
        >
          <input
            id="smtp-password"
            type="password"
            autoComplete="new-password"
            className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            {...register('armoryAccounts.email.smtpPassword')}
          />
        </FormField>
      </div>

      <div className="grid gap-4 sm:grid-cols-2">
        <FormField
          label="From Address"
          htmlFor="from-address"
          error={emailErrors?.fromAddress?.message}
          required
        >
          <input
            id="from-address"
            type="email"
            placeholder="noreply@example.com"
            className={cn(
              'block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
              emailErrors?.fromAddress ? 'border-red-400' : 'border-gray-300'
            )}
            {...register('armoryAccounts.email.fromAddress')}
          />
        </FormField>

        <FormField
          label="From Name"
          htmlFor="from-name"
          error={emailErrors?.fromName?.message}
          hint="Shown as the sender name in the inbox"
        >
          <input
            id="from-name"
            type="text"
            placeholder={realmName}
            className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
            {...register('armoryAccounts.email.fromName')}
            onBlur={(event) => {
              if (!event.target.value.trim()) {
                setValue('armoryAccounts.email.fromName', realmName, { shouldDirty: true })
              }
            }}
          />
        </FormField>
      </div>

      <FormField
        label="Verification Email Subject"
        htmlFor="verification-subject"
        error={emailErrors?.verificationSubject?.message}
        required
      >
        <input
          id="verification-subject"
          type="text"
          placeholder={DEFAULT_VERIFICATION_SUBJECT}
          className={cn(
            'block w-full rounded-md border px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
            emailErrors?.verificationSubject ? 'border-red-400' : 'border-gray-300'
          )}
          {...register('armoryAccounts.email.verificationSubject')}
          onBlur={(event) => {
            if (!event.target.value.trim()) {
              setValue('armoryAccounts.email.verificationSubject', DEFAULT_VERIFICATION_SUBJECT, { shouldDirty: true })
            }
          }}
        />
      </FormField>

      <FormField
        label="Verification Email Body (HTML)"
        htmlFor="verification-body"
        error={emailErrors?.verificationBodyHtml?.message}
        hint="Placeholders: {{verifyUrl}}, {{siteName}}, {{expiryHours}}"
        required
      >
        <textarea
          id="verification-body"
          rows={6}
          className={cn(
            'block w-full rounded-md border px-3 py-2 font-mono text-xs shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500',
            emailErrors?.verificationBodyHtml ? 'border-red-400' : 'border-gray-300'
          )}
          {...register('armoryAccounts.email.verificationBodyHtml')}
          onBlur={(event) => {
            if (!event.target.value.trim()) {
              setValue('armoryAccounts.email.verificationBodyHtml', DEFAULT_VERIFICATION_BODY_HTML, { shouldDirty: true })
            }
          }}
        />
      </FormField>
    </div>
  )
}
