import { useEffect, useMemo, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { AlertTriangle, CheckCircle2, Loader2, Mail, Save } from 'lucide-react'
import { stackApi, validationApi } from '@/services/api'
import { stackKeys } from '@/hooks/useStacks'
import { apiErrorMessage } from '@/lib/utils'
import {
  DEFAULT_ARMORY_EMAIL,
  DEFAULT_VERIFICATION_BODY_HTML,
  DEFAULT_VERIFICATION_SUBJECT,
} from '@/lib/armory-email-defaults'
import type {
  ArmoryAccountsConfigDto,
  ArmoryEmailConfigDto,
  StackDetailsDto,
  StackConfigurationDto,
} from '@/types/stack.types'

function buildAccountsDraft(stack: StackDetailsDto): ArmoryAccountsConfigDto {
  const current = stack.configuration.armoryAccounts
  return {
    useEmailConfirmation: current?.useEmailConfirmation ?? false,
    emailConfigured: current?.emailConfigured ?? false,
    email: {
      ...DEFAULT_ARMORY_EMAIL,
      ...current?.email,
      verificationSubject: current?.email?.verificationSubject || DEFAULT_VERIFICATION_SUBJECT,
      verificationBodyHtml: current?.email?.verificationBodyHtml || DEFAULT_VERIFICATION_BODY_HTML,
    },
  }
}

function isEmailConfigComplete(email: ArmoryEmailConfigDto | null | undefined): boolean {
  if (!email) return false
  return Boolean(
    email.smtpHost.trim() &&
      email.smtpPort > 0 &&
      email.fromAddress.trim() &&
      email.verificationSubject.trim() &&
      email.verificationBodyHtml.trim(),
  )
}

export default function ArmoryEmailTab({ stack }: { stack: StackDetailsDto }) {
  const queryClient = useQueryClient()
  const [accounts, setAccounts] = useState<ArmoryAccountsConfigDto>(() => buildAccountsDraft(stack))
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [message, setMessage] = useState<string | null>(null)
  const [pageError, setPageError] = useState<string | null>(null)
  const [testEmailAddress, setTestEmailAddress] = useState('')

  useEffect(() => {
    setAccounts(buildAccountsDraft(stack))
  }, [stack])

  const statusQuery = useQuery({
    queryKey: [...stackKeys.detail(stack.stackId), 'armory-accounts-status'],
    queryFn: () => stackApi.armoryAccountsStatus(stack.stackId).then((res) => res.data),
    enabled: accounts.useEmailConfirmation,
  })

  const email = accounts.email ?? DEFAULT_ARMORY_EMAIL
  const configured = accounts.useEmailConfirmation && accounts.emailConfigured && isEmailConfigComplete(email)
  const pendingCount = statusQuery.data?.pendingRegistrationCount ?? 0
  const disableBlocked = accounts.useEmailConfirmation === false && pendingCount > 0

  const saveMutation = useMutation({
    mutationFn: async () => {
      const nextAccounts: ArmoryAccountsConfigDto = {
        useEmailConfirmation: accounts.useEmailConfirmation,
        emailConfigured: accounts.useEmailConfirmation && isEmailConfigComplete(email),
        email: accounts.useEmailConfirmation
          ? {
              ...email,
              smtpPassword: email.smtpPassword,
            }
          : null,
      }

      const config: StackConfigurationDto = {
        ...stack.configuration,
        armoryAccounts: nextAccounts,
        advanced: {
          ...stack.configuration.advanced,
          customEnvVars: stack.configuration.advanced.serviceEnvVars?.worldserver ?? {},
        },
      }

      const validation = await validationApi.validate(config, stack.stackId)
      if (!validation.data.isValid) {
        const fieldErrors: Record<string, string> = {}
        validation.data.errors.forEach((err) => {
          fieldErrors[err.field] = err.message
        })
        throw { fieldErrors }
      }

      return stackApi.updateConfig(stack.stackId, config)
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: stackKeys.detail(stack.stackId) })
      queryClient.invalidateQueries({ queryKey: stackKeys.lists() })
      setErrors({})
      setPageError(null)
      setMessage('Email settings saved. Restart the armory if it is already running to apply SMTP changes.')
    },
    onError: (error: unknown) => {
      const fieldErrors = (error as { fieldErrors?: Record<string, string> })?.fieldErrors
      if (fieldErrors) {
        setErrors(fieldErrors)
        setPageError('Fix the highlighted issues before saving.')
        return
      }
      setPageError(apiErrorMessage(error) || 'Failed to save email settings.')
    },
  })

  const testEmailMutation = useMutation({
    mutationFn: () => stackApi.sendArmoryTestEmail(stack.stackId, testEmailAddress.trim()),
    onSuccess: (res) => {
      setPageError(null)
      setMessage(res.data.message)
    },
    onError: (error: unknown) => {
      const response = (error as { response?: { data?: { message?: string } } })?.response?.data
      setMessage(null)
      setPageError(response?.message ?? apiErrorMessage(error) ?? 'Failed to send test email.')
    },
  })

  const realmName = stack.configuration.advanced.realmName || stack.stackName
  const canTestEmail = accounts.useEmailConfirmation && isEmailConfigComplete(email)

  const statusLabel = useMemo(() => {
    if (!accounts.useEmailConfirmation) return 'Disabled'
    if (configured) return 'Configured'
    return 'Not configured'
  }, [accounts.useEmailConfirmation, configured])

  const setEmailField = <K extends keyof ArmoryEmailConfigDto>(key: K, value: ArmoryEmailConfigDto[K]) => {
    setAccounts((prev) => ({
      ...prev,
      email: { ...(prev.email ?? DEFAULT_ARMORY_EMAIL), [key]: value },
    }))
  }

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div>
          <h2 className="text-xl font-semibold text-gray-900">Player Registration Email</h2>
          <p className="mt-1 max-w-2xl text-sm text-gray-600">
            Require email verification before new armory accounts activate. SMTP credentials are stored encrypted
            on the platform and injected into the armory container at runtime.
          </p>
        </div>
        <div className="rounded-md border border-gray-200 bg-gray-50 px-3 py-2 text-sm">
          <span className="text-gray-500">Status:</span>{' '}
          <span className={configured ? 'font-medium text-green-700' : 'font-medium text-amber-700'}>
            {statusLabel}
          </span>
        </div>
      </div>

      {message && (
        <div className="flex items-start gap-2 rounded-md border border-green-200 bg-green-50 p-3 text-sm text-green-800">
          <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0" />
          {message}
        </div>
      )}
      {pageError && (
        <div className="flex items-start gap-2 rounded-md border border-red-200 bg-red-50 p-3 text-sm text-red-700">
          <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0" />
          {pageError}
        </div>
      )}

      <div className="rounded-lg border border-gray-200 p-4">
        <label className="flex cursor-pointer items-start gap-3">
          <input
            type="checkbox"
            className="mt-0.5 h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500"
            checked={accounts.useEmailConfirmation}
            onChange={(event) => {
              const enabled = event.target.checked
              setAccounts((prev) => ({
                ...prev,
                useEmailConfirmation: enabled,
                emailConfigured: enabled ? prev.emailConfigured : false,
              }))
              setErrors({})
            }}
          />
          <span>
            <span className="text-sm font-medium text-gray-900">Require email confirmation before account activation</span>
            <span className="mt-0.5 block text-xs text-gray-500">
              Players register with email, verify via link, then choose their WoW username.
            </span>
          </span>
        </label>
        {errors['armoryAccounts.useEmailConfirmation'] && (
          <p className="mt-2 text-sm text-red-600">{errors['armoryAccounts.useEmailConfirmation']}</p>
        )}
        {pendingCount > 0 && (
          <p className="mt-3 text-sm text-amber-800">
            {pendingCount} pending registration{pendingCount === 1 ? '' : 's'} awaiting completion.
            {disableBlocked && ' Email confirmation cannot be disabled until these are finished or expired.'}
          </p>
        )}
      </div>

      {accounts.useEmailConfirmation && (
        <div className="space-y-4 rounded-lg border border-gray-200 p-4">
          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="SMTP Host" error={errors['armoryAccounts.email.smtpHost']} required>
              <input
                type="text"
                value={email.smtpHost}
                onChange={(e) => setEmailField('smtpHost', e.target.value)}
                className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
              />
            </Field>
            <Field label="SMTP Port" error={errors['armoryAccounts.email.smtpPort']} required>
              <input
                type="number"
                min={1}
                max={65535}
                value={email.smtpPort}
                onChange={(e) => setEmailField('smtpPort', Number(e.target.value) || 0)}
                className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
              />
            </Field>
          </div>

          <Field label="Security" error={errors['armoryAccounts.email.smtpSecurity']} required>
            <select
              value={email.smtpSecurity}
              onChange={(e) => setEmailField('smtpSecurity', e.target.value as ArmoryEmailConfigDto['smtpSecurity'])}
              className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
            >
              <option value="starttls">STARTTLS (recommended)</option>
              <option value="tls">TLS / SSL</option>
              <option value="none">None</option>
            </select>
          </Field>

          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="SMTP Username" hint="Leave blank if your relay does not require authentication">
              <input
                type="text"
                value={email.smtpUsername}
                onChange={(e) => setEmailField('smtpUsername', e.target.value)}
                className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
              />
            </Field>
            <Field label="SMTP Password" hint="Leave blank to keep the current password">
              <input
                type="password"
                value={email.smtpPassword}
                onChange={(e) => setEmailField('smtpPassword', e.target.value)}
                className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
              />
            </Field>
          </div>

          <div className="grid gap-4 sm:grid-cols-2">
            <Field label="From Address" error={errors['armoryAccounts.email.fromAddress']} required>
              <input
                type="email"
                value={email.fromAddress}
                onChange={(e) => setEmailField('fromAddress', e.target.value)}
                className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
              />
            </Field>
            <Field label="From Name" hint={`Defaults to ${realmName}`}>
              <input
                type="text"
                value={email.fromName}
                placeholder={realmName}
                onChange={(e) => setEmailField('fromName', e.target.value)}
                className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
              />
            </Field>
          </div>

          <Field label="Verification Email Subject" error={errors['armoryAccounts.email.verificationSubject']} required>
            <input
              type="text"
              value={email.verificationSubject}
              onChange={(e) => setEmailField('verificationSubject', e.target.value)}
              className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
            />
          </Field>

          <Field
            label="Verification Email Body (HTML)"
            error={errors['armoryAccounts.email.verificationBodyHtml']}
            hint="Placeholders: {{verifyUrl}}, {{siteName}}, {{expiryHours}}"
            required
          >
            <textarea
              rows={6}
              value={email.verificationBodyHtml}
              onChange={(e) => setEmailField('verificationBodyHtml', e.target.value)}
              className="w-full rounded-md border border-gray-300 px-3 py-2 font-mono text-xs"
            />
          </Field>

          <div className="rounded-md border border-gray-200 bg-gray-50 p-4">
            <div className="mb-2 flex items-center gap-2 text-sm font-medium text-gray-800">
              <Mail className="h-4 w-4" />
              Send test email
            </div>
            <div className="flex flex-wrap items-end gap-3">
              <div className="min-w-[16rem] flex-1">
                <label className="mb-1 block text-xs text-gray-500">Recipient</label>
                <input
                  type="email"
                  value={testEmailAddress}
                  onChange={(e) => setTestEmailAddress(e.target.value)}
                  placeholder="you@example.com"
                  className="w-full rounded-md border border-gray-300 px-3 py-2 text-sm"
                />
              </div>
              <button
                type="button"
                disabled={!canTestEmail || testEmailMutation.isPending || !testEmailAddress.trim()}
                onClick={() => testEmailMutation.mutate()}
                className="inline-flex items-center gap-2 rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 disabled:opacity-50"
              >
                {testEmailMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Mail className="h-4 w-4" />}
                Send test
              </button>
            </div>
            <p className="mt-2 text-xs text-gray-500">
              Save your SMTP settings before sending a test email. Delivery uses the values stored for this stack.
            </p>
          </div>
        </div>
      )}

      <div className="flex items-center gap-3">
        <button
          type="button"
          onClick={() => {
            setMessage(null)
            setPageError(null)
            saveMutation.mutate()
          }}
          disabled={saveMutation.isPending}
          className="inline-flex items-center gap-2 rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white hover:bg-blue-700 disabled:opacity-50"
        >
          {saveMutation.isPending ? <Loader2 className="h-4 w-4 animate-spin" /> : <Save className="h-4 w-4" />}
          Save email settings
        </button>
        {saveMutation.isPending && <span className="text-sm text-gray-500">Validating and saving…</span>}
      </div>
    </div>
  )
}

function Field({
  label,
  children,
  error,
  hint,
  required,
}: {
  label: string
  children: React.ReactNode
  error?: string
  hint?: string
  required?: boolean
}) {
  return (
    <div>
      <label className="mb-1 block text-sm font-medium text-gray-700">
        {label}
        {required && <span className="text-red-500"> *</span>}
      </label>
      {children}
      {hint && <p className="mt-1 text-xs text-gray-500">{hint}</p>}
      {error && <p className="mt-1 text-sm text-red-600">{error}</p>}
    </div>
  )
}
