import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { zodResolver } from '@hookform/resolvers/zod'
import { useQueryClient } from '@tanstack/react-query'
import { AlertCircle, X } from 'lucide-react'
import type { Path } from 'react-hook-form'
import { useForm } from 'react-hook-form'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { StepIndicator, type WizardStep } from '@/components/wizard/StepIndicator'
import { WizardNavigation } from '@/components/wizard/WizardNavigation'
import { AdvancedStep } from '@/components/wizard/steps/AdvancedStep'
import { DeploymentStep } from '@/components/wizard/steps/DeploymentStep'
import { DatabaseStep } from '@/components/wizard/steps/DatabaseStep'
import { EmailConfirmationStep } from '@/components/wizard/steps/EmailConfirmationStep'
import { ModulesStep } from '@/components/wizard/steps/ModulesStep'
import { PortsStep } from '@/components/wizard/steps/PortsStep'
import { ReviewStep } from '@/components/wizard/steps/ReviewStep'
import { ServerConfigStep } from '@/components/wizard/steps/ServerConfigStep'
import { useWizardDraft } from '@/hooks/useWizardDraft'
import { useCreateStack, useStacks, stackKeys } from '@/hooks/useStacks'
import { wizardSchema, WIZARD_DEFAULTS, STEP_TRIGGER_FIELDS_BY_ID, EMAIL_STEP_TRIGGER_FIELDS, type WizardFormData } from '@/schemas/wizard.schemas'
import { DEFAULT_ARMORY_EMAIL } from '@/lib/armory-email-defaults'
import { validationApi, buildApi, stackApi } from '@/services/api'
import { ServerType, DeploymentTarget, StackStatus } from '@/types/stack.types'
import type {
  DeploymentConfigDto,
  PortFieldPath,
  StackConfigurationDto,
  StackDetailsDto,
  StackSetupDraftDto,
  SuggestedPorts,
  ValidationResultDto,
} from '@/types/stack.types'

const BASE_STEPS: WizardStep[] = [
  { id: 'deployment', label: 'Deployment' },
  { id: 'server-config', label: 'Server' },
  { id: 'modules', label: 'Modules' },
  { id: 'database', label: 'Database' },
  { id: 'ports', label: 'Ports' },
  { id: 'advanced', label: 'Advanced' },
  { id: 'review', label: 'Review' },
]

function buildWizardSteps(useEmailConfirmation: boolean): WizardStep[] {
  if (!useEmailConfirmation) {
    return BASE_STEPS
  }

  const steps = [...BASE_STEPS]
  steps.splice(steps.length - 1, 0, { id: 'email', label: 'Email' })
  return steps
}

const VALIDATION_FIELD_PATHS = [
  'stackName',
  'customFork.repositoryUrl',
  'customFork.branch',
  'moduleIds',
  'database.rootPassword',
  'database.port',
  'ports.authServer',
  'ports.worldServer',
  'ports.soapPort',
  'advanced.realmName',
  'advanced.maxPlayers',
  'advanced.customEnvVars',
  'advanced.serviceEnvVars',
  'armoryAccounts.useEmailConfirmation',
  'armoryAccounts.email.smtpHost',
  'armoryAccounts.email.smtpPort',
  'armoryAccounts.email.smtpSecurity',
  'armoryAccounts.email.fromAddress',
  'armoryAccounts.email.verificationSubject',
  'armoryAccounts.email.verificationBodyHtml',
] as const satisfies readonly Path<WizardFormData>[]

const PORT_FIELD_PATHS = [
  'database.port',
  'ports.authServer',
  'ports.worldServer',
  'ports.soapPort',
] as const satisfies readonly PortFieldPath[]

const DEFAULT_PORTS: Record<PortFieldPath, number> = {
  'database.port': 3306,
  'ports.authServer': 3724,
  'ports.worldServer': 8085,
  'ports.soapPort': 7878,
}

export default function CreateStackWizardPage() {
  const navigate = useNavigate()
  const [searchParams, setSearchParams] = useSearchParams()
  const queryClient = useQueryClient()
  const { save: saveDraft, load: loadDraft, clear: clearDraft } = useWizardDraft()
  const createStack = useCreateStack()
  const { data: existingStacks = [] } = useStacks()
  const appliedDefaultPortsRef = useRef(false)
  const loadedServerDraftRef = useRef(false)
  const persistDraftChainRef = useRef(Promise.resolve())
  const allowPersistDraftRef = useRef(!searchParams.get('draft')?.trim())
  const initialResumeStackId = useRef(searchParams.get('draft')?.trim() ?? '')
  const resumeStackId = searchParams.get('draft')?.trim() ?? ''
  const initialDraft = useMemo(() => {
    if (initialResumeStackId.current) {
      return null
    }
    const draft = loadDraft()
    return draft && draft.data.stackName ? draft : null
  }, [loadDraft])

  const [currentStep, setCurrentStep] = useState(0)
  const [submitError, setSubmitError] = useState<string | null>(null)
  const [validationErrors, setValidationErrors] = useState<string[]>([])
  const [suggestedPorts, setSuggestedPorts] = useState<SuggestedPorts>({})
  const [isValidating, setIsValidating] = useState(false)
  const [deploymentStepError, setDeploymentStepError] = useState<string | null>(null)
  const [showResumeBanner, setShowResumeBanner] = useState(() => initialDraft !== null)
  const [pendingDraft, setPendingDraft] = useState<{ data: Partial<WizardFormData>; step: number } | null>(
    () => initialDraft
  )

  const form = useForm<WizardFormData>({
    // @ts-expect-error: zodResolver input/output types mismatch with coerce
    resolver: zodResolver(wizardSchema),
    defaultValues: WIZARD_DEFAULTS,
    mode: 'onTouched',
  })
  const { isDirty } = form.formState
  const useEmailConfirmation = form.watch('armoryAccounts.useEmailConfirmation')
  const draftStackId = form.watch('draftStackId')
  const isFinishingDraft = Boolean(resumeStackId || draftStackId)
  const steps = useMemo(() => buildWizardSteps(useEmailConfirmation), [useEmailConfirmation])
  const reviewStepIndex = steps.findIndex((step) => step.id === 'review')

  useEffect(() => {
    if (currentStep >= steps.length) {
      setCurrentStep(Math.max(steps.length - 1, 0))
    }
  }, [currentStep, steps.length])

  useEffect(() => {
    if (
      appliedDefaultPortsRef.current
      || pendingDraft
      || initialResumeStackId.current
      || isDirty
      || existingStacks.length === 0
    ) {
      return
    }

    const availableDefaults = getAvailableDefaultPorts(existingStacks)
    if (!PORT_FIELD_PATHS.some((field) => availableDefaults[field] !== DEFAULT_PORTS[field])) {
      appliedDefaultPortsRef.current = true
      return
    }

    form.setValue('database.port', availableDefaults['database.port'], { shouldValidate: true })
    form.setValue('ports.authServer', availableDefaults['ports.authServer'], { shouldValidate: true })
    form.setValue('ports.worldServer', availableDefaults['ports.worldServer'], { shouldValidate: true })
    form.setValue('ports.soapPort', availableDefaults['ports.soapPort'], { shouldValidate: true })
    appliedDefaultPortsRef.current = true
  }, [existingStacks, form, isDirty, pendingDraft])

  const validateWithBackend = useCallback(async (values: WizardFormData) => {
    setIsValidating(true)
    setValidationErrors([])
    setSuggestedPorts({})
    form.clearErrors([...VALIDATION_FIELD_PATHS])

    try {
      const config = formDataToDto(values)
      const draftId = values.draftStackId?.trim()
      const response = await validationApi.validate(config, draftId || undefined)
      const result: ValidationResultDto = response.data

      if (!result.isValid) {
        setValidationErrors(result.errors.map((error) => `${error.field}: ${error.message}`))
        setSuggestedPorts(result.suggestedPorts)

        result.errors.forEach((error) => {
          if (isValidationFieldPath(error.field)) {
            form.setError(error.field, { type: 'server', message: error.message })
          }
        })

        return { isValid: false, suggestedPorts: result.suggestedPorts }
      }

      return { isValid: true, suggestedPorts: {} }
    } catch {
      return { isValid: true, suggestedPorts: {} }
    } finally {
      setIsValidating(false)
    }
  }, [form])

  const persistSetupDraft = useCallback((stepId: string) => {
    persistDraftChainRef.current = persistDraftChainRef.current
      .catch(() => undefined)
      .then(async () => {
        if (!allowPersistDraftRef.current) {
          return
        }

        const values = form.getValues()
        if (values.deployment.target !== DeploymentTarget.External) {
          return
        }

        const host = values.deployment.externalHost?.trim() ?? ''
        const instanceId = values.deployment.cloudInstanceId?.trim() ?? ''
        if (!host && !instanceId) {
          return
        }

        const draftJson: WizardFormData = {
          ...values,
          deployment: {
            ...values.deployment,
            externalSshPrivateKey: '',
          },
        }
        const requestedName = values.stackName?.trim()
        const response = await stackApi.saveSetupDraft({
          stackId: values.draftStackId?.trim() || undefined,
          wizardStepId: stepId,
          wizardDraftJson: JSON.stringify(draftJson),
          stackName: isPlaceholderStackName(requestedName) ? undefined : requestedName || undefined,
          deployment: toDraftDeploymentDto(values),
        })
        const id = response.data.stackId
        loadedServerDraftRef.current = true
        if (id && id !== values.draftStackId) {
          form.setValue('draftStackId', id)
          setSearchParams({ draft: id }, { replace: true })
        }
        if (!requestedName && response.data.stackName && !isPlaceholderStackName(response.data.stackName)) {
          form.setValue('stackName', response.data.stackName)
        }
        void queryClient.invalidateQueries({ queryKey: stackKeys.lists() })
      })
      .catch((error) => {
        console.error('[WIZARD] Failed to save unfinished VPC stack:', error)
      })

    return persistDraftChainRef.current
  }, [form, queryClient, setSearchParams])

  useEffect(() => {
    const draftId = initialResumeStackId.current
    if (!draftId || loadedServerDraftRef.current) {
      return
    }

    loadedServerDraftRef.current = true
    appliedDefaultPortsRef.current = true
    let cancelled = false

    void (async () => {
      try {
        const response = await stackApi.getSetupDraft(draftId)
        if (cancelled) {
          return
        }

        const merged = mergeSetupDraft(response.data)
        form.reset(merged)
        const resumeSteps = buildWizardSteps(Boolean(merged.armoryAccounts.useEmailConfirmation))
        const stepId = resolveResumeStepId(merged, response.data.wizardStepId)
        const stepIndex = resumeSteps.findIndex((step) => step.id === stepId)
        setCurrentStep(stepIndex >= 0 ? stepIndex : 0)
        setShowResumeBanner(false)
        setPendingDraft(null)
        allowPersistDraftRef.current = true
      } catch (error) {
        console.error('[WIZARD] Failed to load unfinished VPC stack:', error)
        if (!cancelled) {
          setSubmitError('Could not load the unfinished VPC stack. It may have been completed or deleted.')
        }
      }
    })()

    return () => {
      cancelled = true
    }
  }, [form])

  useEffect(() => {
    const subscription = form.watch((values, info) => {
      if (info.name !== 'deployment.connectionVerified' || !values.deployment?.connectionVerified) {
        return
      }

      const currentId = steps[currentStep]?.id ?? 'deployment'
      void persistSetupDraft(currentId === 'deployment' ? 'server-config' : currentId)
    })
    return () => subscription.unsubscribe()
  }, [currentStep, form, persistSetupDraft, steps])

  const persistSetupDraftRef = useRef(persistSetupDraft)
  persistSetupDraftRef.current = persistSetupDraft
  const currentStepRef = useRef(currentStep)
  currentStepRef.current = currentStep
  const stepsRef = useRef(steps)
  stepsRef.current = steps

  useEffect(() => {
    return () => {
      void persistSetupDraftRef.current(stepsRef.current[currentStepRef.current]?.id ?? 'deployment')
    }
  }, [])

  const resumeDraft = useCallback(() => {
    if (!pendingDraft) {
      return
    }

    form.reset({ ...WIZARD_DEFAULTS, ...pendingDraft.data })
    const draftSteps = buildWizardSteps(Boolean(pendingDraft.data.armoryAccounts?.useEmailConfirmation))
    setCurrentStep(Math.min(Math.max(pendingDraft.step, 0), draftSteps.length - 1))
    setShowResumeBanner(false)
    setPendingDraft(null)
  }, [form, pendingDraft])

  const dismissDraft = useCallback(() => {
    clearDraft()
    setShowResumeBanner(false)
    setPendingDraft(null)
  }, [clearDraft])

  const goToStep = useCallback(async (targetStep: number) => {
    const activeStep = steps[currentStep]
    const fields = activeStep ? STEP_TRIGGER_FIELDS_BY_ID[activeStep.id] ?? [] : []

    if (targetStep > currentStep && activeStep?.id === 'email') {
      form.setValue('armoryAccounts.emailConfigured', true, { shouldDirty: true })
      const valid = await form.trigger([...EMAIL_STEP_TRIGGER_FIELDS])
      if (!valid) {
        return
      }
    }

    if (targetStep > currentStep && fields.length > 0) {
      const valid = await form.trigger(fields as Parameters<typeof form.trigger>[0])

      if (!valid) {
        return
      }
    }

    if (
      targetStep > currentStep
      && activeStep?.id === 'deployment'
      && form.getValues('deployment.target') === DeploymentTarget.External
    ) {
      if (form.getValues('deployment.sshCertificateVerified') === false) {
        setDeploymentStepError('Verify the downloaded SSH certificate before continuing.')
        return
      }
      if (!form.getValues('deployment.connectionVerified')) {
        setDeploymentStepError(
          'Verify VPC (host firewall, Docker, OS baselines, and cloud security groups) before continuing.'
        )
        return
      }
    }

    const values = form.getValues()
    saveDraft(values, targetStep)
    setCurrentStep(targetStep)
    setSubmitError(null)
    setDeploymentStepError(null)
    const targetId = steps[targetStep]?.id ?? 'deployment'
    void persistSetupDraft(targetId)

    if (targetStep === reviewStepIndex) {
      void validateWithBackend(values).then(result => {
        console.log('[WIZARD] Review step validation:', result)
      })
    }
  }, [currentStep, form, persistSetupDraft, reviewStepIndex, saveDraft, steps, validateWithBackend])

  const handleSkipEmailStep = useCallback(() => {
    form.setValue('armoryAccounts.emailConfigured', false, { shouldDirty: true })
    void goToStep(currentStep + 1)
  }, [currentStep, form, goToStep])

  const handleNext = () => {
    void goToStep(currentStep + 1)
  }

  const handleBack = () => {
    void goToStep(currentStep - 1)
  }

  const handleApplySuggestedPorts = useCallback(() => {
    const nextValues = applySuggestedPorts(form.getValues(), suggestedPorts)

    form.setValue('database.port', nextValues.database.port, { shouldDirty: true, shouldValidate: true })
    form.setValue('ports.authServer', nextValues.ports.authServer, { shouldDirty: true, shouldValidate: true })
    form.setValue('ports.worldServer', nextValues.ports.worldServer, { shouldDirty: true, shouldValidate: true })
    form.setValue('ports.soapPort', nextValues.ports.soapPort, { shouldDirty: true, shouldValidate: true })

    void validateWithBackend(nextValues)
  }, [form, suggestedPorts, validateWithBackend])

  const handleSubmit = form.handleSubmit(async (values) => {
    let typedValues = wizardSchema.parse(values)
    setSubmitError(null)

    console.log('[WIZARD] Starting submit with values:', typedValues)

    // First validation attempt
    let validationResult = await validateWithBackend(typedValues)
    console.log('[WIZARD] First validation result:', validationResult)
    
    // If validation failed due to port conflicts and we have suggestions, auto-apply them and retry
    if (!validationResult.isValid && Object.keys(validationResult.suggestedPorts).length > 0) {
      console.log('[WIZARD] Auto-applying suggested ports:', validationResult.suggestedPorts)
      typedValues = applySuggestedPorts(typedValues, validationResult.suggestedPorts)
      
      form.setValue('database.port', typedValues.database.port, { shouldDirty: true })
      form.setValue('ports.authServer', typedValues.ports.authServer, { shouldDirty: true })
      form.setValue('ports.worldServer', typedValues.ports.worldServer, { shouldDirty: true })
      form.setValue('ports.soapPort', typedValues.ports.soapPort, { shouldDirty: true })
      
      // Retry validation with suggested ports
      validationResult = await validateWithBackend(typedValues)
      console.log('[WIZARD] Retry validation result:', validationResult)
    }
    
    if (!validationResult.isValid) {
      console.log('[WIZARD] Validation still failed, aborting')
      return
    }

    console.log('[WIZARD] Validation passed, creating stack...')
    try {
      const config = formDataToDto(typedValues)
      
      console.log('[WIZARD] Calling createStack API with config:', config)
      // Create the stack
      const createResult = await createStack.mutateAsync(config)
      console.log('[WIZARD] Stack created:', createResult)
      const stackId = createResult.data.stackId
      
      console.log('[WIZARD] Starting build for stack:', stackId)
      // Start the build
      await buildApi.start(stackId, config)
      console.log('[WIZARD] Build started successfully')
      
      clearDraft()
      allowPersistDraftRef.current = false
      
      // Navigate to build progress page
      navigate(`/stacks/${stackId}/build`)
    } catch (error: unknown) {
      console.error('[WIZARD] Error during stack creation:', error)
      const message = error instanceof Error
        ? error.message
        : 'Failed to create stack. Please try again.'
      setSubmitError(message)
    }
  })

  return (
    <div className="mx-auto max-w-2xl">
      <div className="mb-6">
        <h1 className="text-3xl font-bold text-gray-900">
          {isFinishingDraft ? 'Finish stack setup' : 'Create New Stack'}
        </h1>
        <p className="mt-1 text-gray-500">
          {isFinishingDraft
            ? 'Continue configuring this VPC. The instance is already created.'
            : 'Configure and launch a new AzerothCore server stack.'}
        </p>
      </div>

      {showResumeBanner && pendingDraft && (
        <div
          className="mb-6 flex items-start justify-between gap-3 rounded-lg border border-blue-200 bg-blue-50 p-4"
          role="alert"
          aria-label="Saved draft found"
        >
          <div className="text-sm text-blue-800">
            <span className="font-medium">You have an unsaved draft</span>
            {pendingDraft.data.stackName && (
              <>
                {' '}for <strong>{pendingDraft.data.stackName}</strong>
              </>
            )}
            .{' '}
            <button
              type="button"
              onClick={resumeDraft}
              className="rounded font-medium underline hover:no-underline focus:outline-none focus:ring-2 focus:ring-blue-500"
            >
              Resume
            </button>
            {' or '}
            <button
              type="button"
              onClick={dismissDraft}
              className="rounded underline hover:no-underline focus:outline-none focus:ring-2 focus:ring-blue-500"
            >
              start fresh
            </button>
            .
          </div>
          <button
            type="button"
            onClick={dismissDraft}
            className="shrink-0 rounded text-blue-400 hover:text-blue-600 focus:outline-none focus:ring-2 focus:ring-blue-500"
            aria-label="Dismiss draft banner"
          >
            <X className="h-4 w-4" aria-hidden="true" />
          </button>
        </div>
      )}

      <div className="overflow-visible rounded-xl border border-gray-200 bg-white shadow-sm">
        <div className="border-b border-gray-200 bg-gray-50 px-6 py-4">
          <StepIndicator steps={steps} currentStep={currentStep} />
        </div>

        <div className="min-h-[24rem] overflow-visible px-6 py-6">
          {steps[currentStep]?.id === 'deployment' && (
            <DeploymentStep
              form={form}
              onVpcBound={() => {
                queueMicrotask(() => {
                  void persistSetupDraft(steps[currentStep]?.id ?? 'deployment')
                })
              }}
            />
          )}
          {steps[currentStep]?.id === 'server-config' && <ServerConfigStep form={form} />}
          {steps[currentStep]?.id === 'modules' && <ModulesStep form={form} />}
          {steps[currentStep]?.id === 'database' && <DatabaseStep form={form} />}
          {steps[currentStep]?.id === 'ports' && <PortsStep form={form} />}
          {steps[currentStep]?.id === 'advanced' && <AdvancedStep form={form} />}
          {steps[currentStep]?.id === 'email' && <EmailConfirmationStep form={form} />}
          {steps[currentStep]?.id === 'review' && (
            <ReviewStep
              form={form}
              validationErrors={validationErrors}
              isValidating={isValidating}
              suggestedPorts={suggestedPorts}
              onApplySuggestedPorts={handleApplySuggestedPorts}
            />
          )}
        </div>

        {submitError && (
          <div className="mx-6 mb-4 flex items-center gap-2 rounded-md border border-red-200 bg-red-50 p-3 text-sm text-red-700" role="alert">
            <AlertCircle className="h-4 w-4 shrink-0" aria-hidden="true" />
            {submitError}
          </div>
        )}

        {deploymentStepError && (
          <div className="mx-6 mb-4 flex items-center gap-2 rounded-md border border-amber-200 bg-amber-50 p-3 text-sm text-amber-800" role="alert">
            <AlertCircle className="h-4 w-4 shrink-0" aria-hidden="true" />
            {deploymentStepError}
          </div>
        )}

        <div className="px-6 pb-6">
          {steps[currentStep]?.id === 'email' && (
            <div className="mb-4 flex justify-end">
              <button
                type="button"
                onClick={handleSkipEmailStep}
                className="rounded-md border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-blue-500"
              >
                Skip for now
              </button>
            </div>
          )}
          <WizardNavigation
            currentStep={currentStep}
            totalSteps={steps.length}
            onBack={handleBack}
            onNext={handleNext}
            onSubmit={handleSubmit}
            isSubmitting={createStack.isPending}
            canGoBack={currentStep > 0}
          />
        </div>
      </div>
    </div>
  )
}

function formDataToDto(values: WizardFormData): StackConfigurationDto {
  return {
    stackName: values.stackName,
    serverType: values.serverType,
    moduleIds: values.moduleIds,
    database: {
      rootPassword: values.database.rootPassword,
      port: values.database.port,
    },
    ports: {
      authServer: values.ports.authServer,
      worldServer: values.ports.worldServer,
      soapPort: values.ports.soapPort,
    },
    advanced: {
      maxPlayers: values.advanced.maxPlayers,
      realmName: values.advanced.realmName,
      realmlistHost: values.advanced.realmlistHost ?? '',
      customEnvVars: values.advanced.customEnvVars ?? {},
      serviceEnvVars: values.advanced.serviceEnvVars ?? {},
    },
    deployment: values.deployment
      ? {
          target: values.deployment.target,
          externalHost: values.deployment.externalHost ?? '',
          externalSshPort: values.deployment.externalSshPort ?? 22,
          externalSshUser: values.deployment.externalSshUser ?? '',
          externalSshPrivateKey: values.deployment.externalSshPrivateKey ?? '',
          savedSshKeyId: values.deployment.savedSshKeyId ?? '',
          saveSshKeyToVault: values.deployment.saveSshKeyToVault ?? true,
          saveSshKeyLabel: values.deployment.saveSshKeyLabel ?? '',
          cloudConnectionId: values.deployment.cloudConnectionId ?? '',
          cloudInstanceId: values.deployment.cloudInstanceId ?? '',
          cloudRegion: values.deployment.cloudRegion ?? '',
          cloudProvider: values.deployment.cloudProvider ?? '',
          cloudInstanceType: values.deployment.cloudInstanceType ?? '',
        }
      : undefined,
    customFork: values.serverType === ServerType.Custom
      ? {
          repositoryUrl: values.customFork?.repositoryUrl?.trim() ?? '',
          branch: values.customFork?.branch?.trim() ?? '',
        }
      : undefined,
    armoryAccounts: {
      useEmailConfirmation: values.armoryAccounts.useEmailConfirmation,
      emailConfigured: values.armoryAccounts.emailConfigured,
      email: values.armoryAccounts.useEmailConfirmation ? values.armoryAccounts.email ?? null : null,
    },
    draftStackId: values.draftStackId?.trim() || undefined,
  }
}

function isValidationFieldPath(field: string): field is (typeof VALIDATION_FIELD_PATHS)[number] {
  return VALIDATION_FIELD_PATHS.includes(field as (typeof VALIDATION_FIELD_PATHS)[number])
}

function applySuggestedPorts(values: WizardFormData, suggestedPorts: SuggestedPorts): WizardFormData {
  return {
    ...values,
    database: {
      ...values.database,
      port: suggestedPorts['database.port'] ?? values.database.port,
    },
    ports: {
      authServer: suggestedPorts['ports.authServer'] ?? values.ports.authServer,
      worldServer: suggestedPorts['ports.worldServer'] ?? values.ports.worldServer,
      soapPort: suggestedPorts['ports.soapPort'] ?? values.ports.soapPort,
    },
  }
}

function getAvailableDefaultPorts(existingStacks: StackDetailsDto[]): Record<PortFieldPath, number> {
  const usedPorts = new Set<number>()

  existingStacks.forEach((stack) => {
    if (stack.status === StackStatus.SetupIncomplete) {
      return
    }
    usedPorts.add(stack.configuration.database.port)
    usedPorts.add(stack.configuration.ports.authServer)
    usedPorts.add(stack.configuration.ports.worldServer)
    usedPorts.add(stack.configuration.ports.soapPort)
  })

  return PORT_FIELD_PATHS.reduce<Record<PortFieldPath, number>>((accumulator, field) => {
    const nextPort = findAvailablePort(usedPorts, DEFAULT_PORTS[field])
    accumulator[field] = nextPort
    usedPorts.add(nextPort)
    return accumulator
  }, { ...DEFAULT_PORTS })
}

function findAvailablePort(usedPorts: Set<number>, preferredPort: number): number {
  for (let port = preferredPort; port <= 65535; port += 1) {
    if (!usedPorts.has(port)) {
      return port
    }
  }

  for (let port = 1024; port < preferredPort; port += 1) {
    if (!usedPorts.has(port)) {
      return port
    }
  }

  return preferredPort
}

function toDraftDeploymentDto(values: WizardFormData): DeploymentConfigDto {
  return {
    target: DeploymentTarget.External,
    externalHost: values.deployment.externalHost ?? '',
    externalSshPort: values.deployment.externalSshPort ?? 22,
    externalSshUser: values.deployment.externalSshUser ?? '',
    externalSshPrivateKey: values.deployment.externalSshPrivateKey ?? '',
    savedSshKeyId: values.deployment.savedSshKeyId ?? '',
    saveSshKeyToVault: values.deployment.saveSshKeyToVault ?? true,
    saveSshKeyLabel: values.deployment.saveSshKeyLabel ?? '',
    cloudConnectionId: values.deployment.cloudConnectionId ?? '',
    cloudInstanceId: values.deployment.cloudInstanceId ?? '',
    cloudRegion: values.deployment.cloudRegion ?? '',
    cloudProvider: values.deployment.cloudProvider ?? '',
    cloudInstanceType: values.deployment.cloudInstanceType ?? '',
  }
}

function mergeSetupDraft(draft: StackSetupDraftDto): WizardFormData {
  let parsed: Partial<WizardFormData> = {}
  try {
    parsed = JSON.parse(draft.wizardDraftJson) as Partial<WizardFormData>
  } catch {
    parsed = {}
  }

  return {
    ...WIZARD_DEFAULTS,
    ...parsed,
    draftStackId: draft.stackId,
    stackName: isPlaceholderStackName(parsed.stackName) ? '' : (parsed.stackName?.trim() || ''),
    database: { ...WIZARD_DEFAULTS.database, ...parsed.database },
    ports: { ...WIZARD_DEFAULTS.ports, ...parsed.ports },
    advanced: { ...WIZARD_DEFAULTS.advanced, ...parsed.advanced },
    customFork: { ...WIZARD_DEFAULTS.customFork, ...parsed.customFork },
    armoryAccounts: {
      ...WIZARD_DEFAULTS.armoryAccounts,
      ...parsed.armoryAccounts,
      email: {
        ...DEFAULT_ARMORY_EMAIL,
        ...(parsed.armoryAccounts?.email ?? {}),
      } as typeof DEFAULT_ARMORY_EMAIL,
    },
    deployment: {
      ...WIZARD_DEFAULTS.deployment,
      ...parsed.deployment,
      target: DeploymentTarget.External,
      externalHost: draft.deployment.externalHost || parsed.deployment?.externalHost || '',
      externalSshPort: draft.deployment.externalSshPort || parsed.deployment?.externalSshPort || 22,
      externalSshUser: draft.deployment.externalSshUser || parsed.deployment?.externalSshUser || '',
      cloudConnectionId: draft.deployment.cloudConnectionId || parsed.deployment?.cloudConnectionId || '',
      cloudInstanceId: draft.deployment.cloudInstanceId || parsed.deployment?.cloudInstanceId || '',
      cloudRegion: draft.deployment.cloudRegion || parsed.deployment?.cloudRegion || '',
      cloudProvider: draft.deployment.cloudProvider || parsed.deployment?.cloudProvider || '',
      cloudInstanceType: draft.deployment.cloudInstanceType || parsed.deployment?.cloudInstanceType || '',
      savedSshKeyId: parsed.deployment?.savedSshKeyId || '',
      externalSshPrivateKey: draft.externalSshPrivateKey || parsed.deployment?.externalSshPrivateKey || '',
    },
  }
}

function resolveResumeStepId(form: WizardFormData, savedStepId: string): string {
  if (!form.deployment.connectionVerified) {
    return 'deployment'
  }

  if (!savedStepId || savedStepId === 'deployment') {
    return 'server-config'
  }

  return savedStepId
}

function isPlaceholderStackName(name?: string): boolean {
  const value = (name ?? '').trim().toLowerCase()
  return !value
    || value.startsWith('unnamed-instance')
    || /^vpc-[a-f0-9]{8}$/.test(value)
}


