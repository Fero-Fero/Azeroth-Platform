import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { CheckCircle2, Cloud, Copy, Loader2, Server, XCircle } from 'lucide-react'
import { FormField } from '@/components/wizard/common/FormField'
import { VpcConnectionMethodTabs } from '@/components/wizard/common/VpcConnectionMethodTabs'
import { OsMismatchNotice, osMismatchDetected } from '@/components/wizard/common/OsMismatchNotice'
import { VpcSecurityOverviewSection } from '@/components/wizard/common/VpcSecurityOverviewSection'
import { SavedSshKeySelector } from '@/components/wizard/common/SavedSshKeySelector'
import { SshPrivateKeyField } from '@/components/wizard/common/SshPrivateKeyField'
import type { WizardForm } from '@/types/wizard.types'
import { DEFAULT_ARMORY_PORT, DEFAULT_CLIENT_PORT } from '@/lib/stack-network-defaults'
import { providerDisplayName } from '@/lib/cloud-auth'
import { cn, apiErrorMessage } from '@/lib/utils'
import { systemApi, cloudApi } from '@/services/api'
import {
  CloudProvider,
  DeploymentTarget,
  RemoteConnectionTestPhase,
  RemoteHostOs,
  type RemoteConnectionTestResultDto,
  type RemotePrerequisiteCheckDto,
  type RemoteProvisionRequestDto,
  type RemoteSetupResultDto,
  type VpcLaunchUserDataDto,
} from '@/types/stack.types'

interface DeploymentStepProps {
  form: WizardForm
  onVpcBound?: () => void
}

function plannedSetupItems() {
  return [
    {
      id: 'docker' as const,
      title: 'Setting up Docker',
      description: 'Install Docker Engine & Compose, start the service, and grant your SSH user access.',
    },
    {
      id: 'baseline' as const,
      title: 'OS security baselines',
      description: 'Enable automatic security updates on Ubuntu/Debian.',
    },
    {
      id: 'firewall' as const,
      title: 'Configure host firewall (ufw)',
      description:
        'Allow SSH and player/web ports; keep MySQL and SOAP closed (Docker binds them on the VPC IP).',
    },
    {
      id: 'cloud-sg' as const,
      title: 'Configure cloud security group',
      description:
        'Launch and pick apply these rules automatically on the VPC provider. Use the checklist if you need to inspect or repair by hand.',
    },
  ]
}

type SetupSectionId = 'docker' | 'baseline' | 'firewall' | 'cloud-sg'
type SetupSectionStatus = 'pending' | 'running' | 'passed' | 'failed' | 'skipped'

function matchesSetupSection(stepName: string, sectionId: SetupSectionId): boolean {
  switch (sectionId) {
    case 'docker':
      return (
        stepName === 'Setting up Docker'
        || stepName.startsWith('Update package')
        || stepName.startsWith('Install Docker')
        || stepName.startsWith('Start Docker')
        || stepName.startsWith('Enable Docker on boot')
        || stepName.startsWith('Grant Docker')
        || stepName.startsWith('Verify Docker')
      )
    case 'baseline':
      return stepName === 'OS security baselines'
    case 'firewall':
      return (
        stepName.includes('firewall')
        || stepName.includes('ufw')
        || stepName.startsWith('Allow TCP')
        || stepName.startsWith('Set firewall')
        || stepName.startsWith('Allow SSH')
      )
    case 'cloud-sg':
      return false
    default:
      return false
  }
}

function computeSetupSectionStatuses(
  steps: RemotePrerequisiteCheckDto[] | undefined,
  settingUp: boolean,
  enableHostFirewall: boolean,
  cloudSecurityGroupAcknowledged: boolean
): Record<SetupSectionId, SetupSectionStatus> {
  const statuses: Record<SetupSectionId, SetupSectionStatus> = {
    docker: 'pending',
    baseline: 'pending',
    firewall: enableHostFirewall ? 'pending' : 'skipped',
    'cloud-sg': cloudSecurityGroupAcknowledged ? 'passed' : 'pending',
  }

  if (settingUp && !steps?.length) {
    statuses.docker = 'running'
    statuses.baseline = 'running'
    if (enableHostFirewall) {
      statuses.firewall = 'running'
    }
    return statuses
  }

  if (!steps?.length) {
    return statuses
  }

  for (const sectionId of ['docker', 'baseline', 'firewall'] as const) {
    if (sectionId === 'firewall' && !enableHostFirewall) {
      statuses.firewall = 'skipped'
      continue
    }

    const matching = steps.filter((step) => matchesSetupSection(step.name, sectionId))
    if (matching.length === 0) {
      continue
    }

    statuses[sectionId] = matching.some((step) => !step.passed) ? 'failed' : 'passed'
  }

  return statuses
}

function getSetupSectionDetail(
  sectionId: SetupSectionId,
  steps: RemotePrerequisiteCheckDto[] | undefined,
  cloudSecurityGroupAcknowledged: boolean
): string | undefined {
  if (sectionId === 'cloud-sg') {
    return cloudSecurityGroupAcknowledged
      ? 'Cloud security group acknowledged.'
      : undefined
  }

  const matching = steps?.filter((step) => matchesSetupSection(step.name, sectionId)) ?? []
  if (matching.length === 0) {
    return undefined
  }

  return matching[matching.length - 1]?.message
}

function setupNeedsManualDockerInstall(steps: RemotePrerequisiteCheckDto[] | undefined): boolean {
  if (!steps?.length) {
    return false
  }

  return steps.some(
    (step) =>
      !step.passed &&
      /sudo|passwordless|password is required|apt-get/i.test(`${step.name} ${step.message}`),
  )
}

function ManualVpcDockerSetupPanel({ sshUser }: { sshUser: string }) {
  const user = sshUser.trim() || 'YOUR_SSH_USER'
  const lines = [
    'sudo apt-get update',
    'sudo apt-get install -y docker.io docker-compose-v2',
    'sudo systemctl enable --now docker',
    `sudo usermod -aG docker ${user}`,
    'docker info',
  ]

  return (
    <div className="rounded-md border border-amber-300 bg-white p-3 text-xs text-amber-950">
      <p className="font-medium">Install Docker manually over SSH (Ubuntu/Debian)</p>
      <p className="mt-1 text-amber-900">
        SSH into the VPS as a user that can run <code className="text-[11px]">sudo</code> (often{' '}
        <code className="text-[11px]">root</code>). Paste these commands, then log out and back in
        (or run <code className="text-[11px]">newgrp docker</code>) so group membership applies. Click{' '}
        <strong>Test connection</strong> again  -  Setup Now will skip install if Docker is already running.
      </p>
      <pre className="mt-2 overflow-x-auto rounded border border-amber-200 bg-amber-50/80 p-2 font-mono text-[11px] leading-relaxed">
        {lines.join('\n')}
      </pre>
      <p className="mt-2 text-[11px] text-amber-900">
        Optional  -  allow the platform to run future setup without a sudo password (replace the username if
        needed):
      </p>
      <pre className="mt-1 overflow-x-auto rounded border border-amber-200 bg-amber-50/80 p-2 font-mono text-[11px] leading-relaxed">{`echo '${user} ALL=(ALL) NOPASSWD:ALL' | sudo tee /etc/sudoers.d/90-platform-setup
sudo chmod 440 /etc/sudoers.d/90-platform-setup`}</pre>
    </div>
  )
}

function VpcLaunchGuidePanel({
  sshUser,
  launchData,
  embedded = false,
  hintsOnly = false,
}: {
  sshUser: string
  launchData: VpcLaunchUserDataDto | undefined
  embedded?: boolean
  hintsOnly?: boolean
}) {
  const [copied, setCopied] = useState(false)
  const [provider, setProvider] = useState<'aws' | 'gcp' | 'digitalocean' | 'other'>('aws')
  const user = sshUser.trim() || launchData?.sshUser || 'ubuntu'

  const providerSteps: Record<typeof provider, { title: string; steps: string[] }> = {
    aws: {
      title: 'Amazon Web Services (EC2)',
      steps: [
        'EC2 → Launch instance → pick Ubuntu 22.04 or 24.04.',
        'Expand Advanced details at the bottom of the launch form (easy to miss  -  scroll down past storage and tags).',
        'Paste the script into User data, allow SSH (port 22) from your IP, create/download the .pem key pair, then Launch.',
      ],
    },
    gcp: {
      title: 'Google Cloud (Compute Engine)',
      steps: [
        'Compute Engine â†’ Create instance â†’ pick Ubuntu 22.04 or 24.04.',
        'Open Management â†’ Automation â†’ Startup script (not SSH keys).',
        'Paste the script, allow TCP:22 in the firewall, add your SSH key under Security â†’ SSH keys, then Create.',
      ],
    },
    digitalocean: {
      title: 'DigitalOcean (Droplet)',
      steps: [
        'Create â†’ Droplets â†’ pick Ubuntu 22.04 or 24.04.',
        'Expand Advanced Options â†’ check User data.',
        'Paste the script, add your SSH key, then Create Droplet.',
      ],
    },
    other: {
      title: 'Other provider (bare metal, etc.)',
      steps: [
        'Create a new Ubuntu 22.04/24.04 VM (not an existing one).',
        'Look for User data, Custom data, Cloud-init, or Startup script in the create wizard.',
        'Paste the script there  -  it runs once on first boot. Allow SSH (port 22) from your IP.',
      ],
    },
  }

  const handleCopy = async () => {
    if (!launchData?.script) {
      return
    }

    try {
      await navigator.clipboard.writeText(launchData.script)
      setCopied(true)
      window.setTimeout(() => setCopied(false), 2000)
    } catch {
      setCopied(false)
    }
  }

  const Wrapper = embedded ? 'details' : 'section'
  const wrapperClass = embedded
    ? 'group rounded-md border border-blue-200 bg-blue-50/60'
    : 'rounded-lg border border-blue-200 bg-blue-50 p-4'

  const summaryClass = embedded
    ? 'cursor-pointer list-none px-3 py-2 text-xs font-medium text-blue-950 marker:content-none [&::-webkit-details-marker]:hidden'
    : undefined

  const content = (
    <>
      {!embedded ? (
        <h3 className="text-sm font-semibold text-blue-950">Launch a new cloud server (optional)</h3>
      ) : hintsOnly ? null : (
        <p className="text-xs font-medium text-gray-800">
          Bootstrap script (Linux)
        </p>
      )}
      <p className={cn('text-xs text-blue-900', !embedded || hintsOnly ? undefined : 'mt-1', hintsOnly && 'pt-0')}>
        {hintsOnly ? (
          <>
            Creating a <span className="font-medium">new</span> VM? Paste the bootstrap script into your
            provider&apos;s startup / user-data field  -  it runs once on first boot. For an existing server, use
            the terminal sticky note instead.
          </>
        ) : (
          <>
            The script below works on <span className="font-medium">any</span> cloud that supports Ubuntu and a
            startup/user-data field (AWS, GCP, DigitalOcean, Azure, etc.). It only runs when you{' '}
            <span className="font-medium">create</span> a new VM  -  not on a server you already started.
          </>
        )}
      </p>
      {!hintsOnly ? (
        <p className="mt-2 rounded-md border border-blue-300 bg-white/80 px-2.5 py-2 text-xs text-blue-950">
          Paste the script into your SSH session after opening the terminal in step 2, or into your cloud
          provider&apos;s startup/user-data field when <span className="font-medium">creating</span> a new VM.
        </p>
      ) : null}
      <div className="mt-3 flex flex-wrap gap-2">
        {(
          [
            ['aws', 'AWS'],
            ['gcp', 'GCP'],
            ['digitalocean', 'DigitalOcean'],
            ['other', 'Other'],
          ] as const
        ).map(([id, label]) => (
          <button
            key={id}
            type="button"
            onClick={() => setProvider(id)}
            className={cn(
              'rounded-md border px-2.5 py-1 text-[11px] font-medium',
              provider === id
                ? 'border-blue-500 bg-white text-blue-900'
                : 'border-blue-200 bg-blue-100/50 text-blue-800 hover:bg-blue-100'
            )}
          >
            {label}
          </button>
        ))}
      </div>
      <p className="mt-2 text-xs font-medium text-blue-950">
        {providerSteps[provider].title}
      </p>
      <ol className="mt-1 list-decimal space-y-1 pl-5 text-xs text-blue-900">
        {providerSteps[provider].steps.map((step) => (
          <li key={step}>{step}</li>
        ))}
        {!hintsOnly ? <li>When the terminal is connected, paste the script below and press Enter.</li> : null}
      </ol>
      {!hintsOnly ? (
        <>
          <div className="mt-3 flex flex-wrap items-center gap-2">
            <button
              type="button"
              onClick={() => void handleCopy()}
              disabled={!launchData?.script}
              className="inline-flex items-center gap-1.5 rounded-md border border-blue-300 bg-white px-2.5 py-1 text-[11px] font-medium text-blue-900 hover:bg-blue-100 disabled:opacity-60"
            >
              <Copy className="h-3 w-3" aria-hidden="true" />
              {copied ? 'Copied!' : 'Copy launch script'}
            </button>
            <span className="text-[11px] text-blue-800">
              SSH user: <span className="font-mono">{user}</span>
            </span>
          </div>
          {launchData?.script ? (
            <pre className="mt-2 max-h-40 overflow-auto rounded border border-blue-200 bg-white p-2 font-mono text-[10px] leading-relaxed text-blue-950">
              {launchData.script}
            </pre>
          ) : (
            <p className="mt-2 text-xs text-blue-800">Loading launch script...</p>
          )}
        </>
      ) : null}
    </>
  )

  if (embedded && hintsOnly) {
    return (
      <Wrapper className={wrapperClass}>
        <summary className={summaryClass}>New VM? Paste at create time (optional)</summary>
        <div className="border-t border-blue-200 px-3 pb-3 pt-2">{content}</div>
      </Wrapper>
    )
  }

  return (
    <Wrapper className={wrapperClass}>
      {content}
    </Wrapper>
  )
}

function SetupSectionStatusIcon({ status }: { status: SetupSectionStatus }) {
  switch (status) {
    case 'running':
      return (
        <Loader2
          className="mt-0.5 h-4 w-4 shrink-0 animate-spin text-amber-700"
          aria-hidden="true"
        />
      )
    case 'passed':
      return <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-green-600" aria-hidden="true" />
    case 'failed':
      return <XCircle className="mt-0.5 h-4 w-4 shrink-0 text-red-600" aria-hidden="true" />
    case 'skipped':
      return (
        <CheckCircle2 className="mt-0.5 h-4 w-4 shrink-0 text-amber-600/70" aria-hidden="true" />
      )
    default:
      return (
        <span
          className="mt-0.5 inline-flex h-4 w-4 shrink-0 items-center justify-center rounded-full border-2 border-amber-300 bg-white"
          aria-hidden="true"
        />
      )
  }
}

function setupSectionStatusLabel(status: SetupSectionStatus): string {
  switch (status) {
    case 'running':
      return 'In progress'
    case 'passed':
      return 'Complete'
    case 'failed':
      return 'Failed'
    case 'skipped':
      return 'Skipped'
    default:
      return 'Pending'
  }
}

function buildProvisionRequest(
  externalHost: string,
  externalSshPort: number | string | undefined,
  externalSshUser: string,
  externalSshPrivateKey: string,
  savedSshKeyId: string,
  remoteOs: RemoteHostOs,
  enableHostFirewall: boolean,
  enableUnattendedUpgrades: boolean,
  authServerPort: number,
  worldServerPort: number,
  sshPort: number
): RemoteProvisionRequestDto {
  return {
    deployment: {
      target: DeploymentTarget.External,
      externalHost,
      externalSshPort: Number(externalSshPort) || 22,
      externalSshUser,
      externalSshPrivateKey,
      savedSshKeyId,
    },
    options: {
      remoteOs,
      enableHostFirewall,
      enableUnattendedUpgrades,
      authServerPort,
      worldServerPort,
      armoryPort: DEFAULT_ARMORY_PORT,
      clientPort: DEFAULT_CLIENT_PORT,
      sshPort: Number(externalSshPort) || sshPort,
    },
  }
}

function DeploymentSubstep({
  step,
  title,
  description,
  children,
}: {
  step: number
  title: string
  description?: string
  children: ReactNode
}) {
  return (
    <section className="overflow-visible rounded-lg border border-gray-200 bg-white p-4">
      <div className="flex items-start gap-3">
        <span className="flex h-7 w-7 shrink-0 items-center justify-center rounded-full bg-gray-900 text-xs font-semibold text-white">
          {step}
        </span>
        <div className="min-w-0 flex-1 space-y-3 overflow-visible">
          <div>
            <h3 className="text-sm font-semibold text-gray-900">{title}</h3>
            {description ? <p className="mt-1 text-xs text-gray-600">{description}</p> : null}
          </div>
          {children}
        </div>
      </div>
    </section>
  )
}

function vpcProviderLabel(cloudProvider: string): string {
  const match = (Object.values(CloudProvider) as string[]).find(
    (value) => value.toLowerCase() === cloudProvider.trim().toLowerCase()
  )
  return match ? providerDisplayName(match as CloudProvider) : 'VPC provider'
}

function sshCheckPassed(result: RemoteConnectionTestResultDto | null): boolean {
  if (result?.prerequisites?.some((check) => check.name === 'Operating system' && !check.passed)) {
    return false
  }

  return result?.prerequisites?.some((check) => check.name === 'SSH' && check.passed) ?? false
}

function prerequisitesMet(result: RemoteConnectionTestResultDto | null): boolean {
  return result?.success === true
}

type ProgressStepState = 'pending' | 'active' | 'complete' | 'failed'

interface ConnectionTestProgress {
  connection: ProgressStepState
  prerequisites: ProgressStepState
}

const CONNECTION_TEST_STEPS = [
  { id: 'connection', label: 'Root SSH & azp-admin SSH' },
  { id: 'prerequisites', label: 'Server ready (Docker)' },
] as const

function ConnectionTestProgressBar({
  progress,
}: {
  progress: ConnectionTestProgress
}) {
  const states = [progress.connection, progress.prerequisites]

  return (
    <nav aria-label="Connection test progress" className="mt-3">
      <ol className="flex items-center">
        {CONNECTION_TEST_STEPS.map((step, index) => {
          const state = states[index]
          const isLast = index === CONNECTION_TEST_STEPS.length - 1

          return (
            <li key={step.id} className="flex flex-1 items-center">
              <div className="flex min-w-0 flex-1 flex-col items-center gap-1">
                <div
                  className={cn(
                    'flex h-7 w-7 items-center justify-center rounded-full border-2 text-xs font-semibold',
                    state === 'complete' && 'border-green-600 bg-green-600 text-white',
                    state === 'active' && 'border-blue-600 bg-white text-blue-600',
                    state === 'failed' && 'border-red-500 bg-red-50 text-red-600',
                    state === 'pending' && 'border-gray-200 bg-white text-gray-400'
                  )}
                  aria-current={state === 'active' ? 'step' : undefined}
                >
                  {state === 'active' ? (
                    <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
                  ) : state === 'complete' ? (
                    <CheckCircle2 className="h-3.5 w-3.5" aria-hidden="true" />
                  ) : state === 'failed' ? (
                    <XCircle className="h-3.5 w-3.5" aria-hidden="true" />
                  ) : (
                    index + 1
                  )}
                </div>
                <span
                  className={cn(
                    'max-w-[8rem] text-center text-[11px] font-medium leading-tight',
                    state === 'complete' && 'text-green-700',
                    state === 'active' && 'text-blue-700',
                    state === 'failed' && 'text-red-700',
                    state === 'pending' && 'text-gray-400'
                  )}
                >
                  {step.label}
                </span>
              </div>
              {!isLast && (
                <div
                  className={cn(
                    'mx-2 h-0.5 flex-1',
                    state === 'complete' ? 'bg-green-600' : 'bg-gray-200'
                  )}
                  aria-hidden="true"
                />
              )}
            </li>
          )
        })}
      </ol>
    </nav>
  )
}

export function DeploymentStep({ form, onVpcBound }: DeploymentStepProps) {
  const {
    register,
    watch,
    setValue,
    formState: { errors },
  } = form

  const deploymentTarget = watch('deployment.target')
  const externalHost = watch('deployment.externalHost') ?? ''
  const externalSshPort = watch('deployment.externalSshPort')
  const externalSshUser = watch('deployment.externalSshUser') ?? ''
  const externalSshPrivateKey = watch('deployment.externalSshPrivateKey') ?? ''
  const savedSshKeyId = watch('deployment.savedSshKeyId') ?? ''
  const saveSshKeyToVault = watch('deployment.saveSshKeyToVault') ?? true
  const saveSshKeyLabel = watch('deployment.saveSshKeyLabel') ?? ''
  const connectionVerified = watch('deployment.connectionVerified')
  const remoteOs = watch('deployment.remoteOs') ?? RemoteHostOs.Linux
  const enableHostFirewall = watch('deployment.enableHostFirewall') ?? true
  const sshCertificateVerified = watch('deployment.sshCertificateVerified') ?? true
  const cloudConnectionId = watch('deployment.cloudConnectionId') ?? ''
  const cloudInstanceId = watch('deployment.cloudInstanceId') ?? ''
  const cloudRegion = watch('deployment.cloudRegion') ?? ''
  const cloudProvider = watch('deployment.cloudProvider') ?? ''
  const bootstrapSshKeyId = watch('deployment.bootstrapSshKeyId') ?? ''
  const bootstrapUserSecured = watch('deployment.bootstrapUserSecured') ?? false
  const authServerPort = watch('ports.authServer') ?? 3724
  const worldServerPort = watch('ports.worldServer') ?? 8085

  const deploymentPayload = useMemo(
    () => ({
      target: DeploymentTarget.External,
      externalHost,
      externalSshPort: Number(externalSshPort) || 22,
      externalSshUser,
      externalSshPrivateKey: savedSshKeyId ? '' : externalSshPrivateKey,
      savedSshKeyId,
      saveSshKeyToVault,
      saveSshKeyLabel,
      cloudConnectionId,
      cloudInstanceId,
      cloudProvider,
      bootstrapSshKeyId,
      bootstrapUserSecured,
      remoteOs,
    }),
    [
      bootstrapSshKeyId,
      bootstrapUserSecured,
      cloudConnectionId,
      cloudInstanceId,
      cloudProvider,
      externalHost,
      externalSshPort,
      externalSshPrivateKey,
      externalSshUser,
      saveSshKeyLabel,
      saveSshKeyToVault,
      savedSshKeyId,
      remoteOs,
    ]
  )

  const usingSavedKey = savedSshKeyId.trim().length > 0

  const [testing, setTesting] = useState(false)
  const [testResult, setTestResult] = useState<RemoteConnectionTestResultDto | null>(null)
  const [testProgress, setTestProgress] = useState<ConnectionTestProgress | null>(null)
  const [sshTesting, setSshTesting] = useState(false)
  const [sshTestResult, setSshTestResult] = useState<RemoteConnectionTestResultDto | null>(null)
  const [settingUp, setSettingUp] = useState(false)
  const [setupResult, setSetupResult] = useState<RemoteSetupResultDto | null>(null)
  const skipCredentialResetRef = useRef(true)

  const handleCloudConnectionIdChange = useCallback((id: string) => {
    setValue('deployment.cloudConnectionId', id, { shouldDirty: true })
  }, [setValue])

  const applyRemoteOs = useCallback((next: RemoteHostOs) => {
    setValue('deployment.remoteOs', RemoteHostOs.Linux, { shouldDirty: true, shouldValidate: true })
    const current = (externalSshUser ?? '').trim()
    if (next === RemoteHostOs.Linux && current.toLowerCase() === 'administrator') {
      setValue('deployment.externalSshUser', 'azp-admin', { shouldDirty: true, shouldValidate: true })
    }
  }, [externalSshUser, setValue])

  useEffect(() => {
    if (remoteOs === RemoteHostOs.Windows) {
      applyRemoteOs(RemoteHostOs.Linux)
    }
  }, [applyRemoteOs, remoteOs])

  const hostOsMismatch = osMismatchDetected(testResult, remoteOs)

  const handleCloudProviderChange = useCallback((provider: string) => {
    setValue('deployment.cloudProvider', provider, { shouldDirty: true })
  }, [setValue])

  const connectionFieldsReady =
    externalHost.trim().length > 0 && externalSshUser.trim().length > 0

  const credentialsReady =
    connectionFieldsReady
    && (usingSavedKey || externalSshPrivateKey.trim().length > 0)

  const sshVerified = sshCheckPassed(testResult)
  const dockerReady = prerequisitesMet(testResult)

  useEffect(() => {
    if (skipCredentialResetRef.current) {
      skipCredentialResetRef.current = false
      return
    }

    setValue('deployment.connectionVerified', false, { shouldDirty: true })
    setValue('deployment.firstTimeSetupCompleted', false, { shouldDirty: true })
    setValue('deployment.cloudSecurityGroupAcknowledged', false, { shouldDirty: true })
    setTestResult(null)
    setTestProgress(null)
    setSshTestResult(null)
    setSetupResult(null)
  }, [deploymentTarget, externalHost, externalSshPort, externalSshUser, externalSshPrivateKey, savedSshKeyId, remoteOs, setValue])

  const runPrerequisiteCheck = useCallback(
    async (sshData: RemoteConnectionTestResultDto): Promise<RemoteConnectionTestResultDto> => {
      const prereqRes = await systemApi.testRemoteConnection(
        deploymentPayload,
        RemoteConnectionTestPhase.PrerequisitesOnly
      )
      const hostChecks = [
        ...(sshData.prerequisites ?? []),
        ...(prereqRes.data.prerequisites ?? []).filter((check) => check.name !== 'SSH'),
      ]

      let cloudChecks: RemotePrerequisiteCheckDto[] = []
      let cloudMessage: string | undefined
      if (cloudConnectionId.trim() && externalHost.trim()) {
        try {
          const probe = (
            await cloudApi.probeFirewall(cloudConnectionId, {
              publicHost: externalHost.trim(),
              instanceId: cloudInstanceId.trim() || undefined,
              region: cloudRegion.trim() || undefined,
            })
          ).data
          cloudChecks = probe.checks ?? []
          cloudMessage = probe.message
          if (!probe.success) {
            prereqRes.data.success = false
          }
        } catch (error) {
          cloudChecks = [
            {
              name: 'Cloud security group',
              passed: false,
              message: apiErrorMessage(error, 'Retry Verify VPC after the cloud account can list this VM.'),
            },
          ]
          prereqRes.data.success = false
        }
      }

      const prerequisites = [...hostChecks, ...cloudChecks]
      const success = (prereqRes.data.success ?? false) && cloudChecks.every((check) => check.passed)
      return {
        ...prereqRes.data,
        success,
        message: success
          ? prereqRes.data.message
          : [prereqRes.data.message, cloudMessage].filter(Boolean).join(' '),
        prerequisites,
      }
    },
    [cloudConnectionId, cloudInstanceId, cloudRegion, deploymentPayload, externalHost]
  )

  const runConnectionTest = useCallback(async (): Promise<RemoteConnectionTestResultDto | null> => {
    setTestProgress({ connection: 'active', prerequisites: 'pending' })

    const sshRes = await systemApi.testRemoteConnection(deploymentPayload, RemoteConnectionTestPhase.SshOnly)
    const sshData = sshRes.data
    const sshPassed = sshCheckPassed(sshData)

    if (sshData.bootstrapUserSecured) {
      setValue('deployment.bootstrapUserSecured', true, { shouldDirty: true })
      setValue('deployment.bootstrapSshKeyId', '', { shouldDirty: true })
    }

    if (!sshPassed) {
      setTestProgress({ connection: 'failed', prerequisites: 'pending' })
      setTestResult(sshData)
      setValue('deployment.connectionVerified', false, { shouldDirty: true })
      return sshData
    }

    setTestProgress({ connection: 'complete', prerequisites: 'active' })

    try {
      const merged = await runPrerequisiteCheck(sshData)

      setTestResult(merged)
      setTestProgress({
        connection: 'complete',
        prerequisites: merged.success ? 'complete' : 'failed',
      })

      if (merged.success) {
        setValue('deployment.connectionVerified', true, { shouldDirty: true })
        const currentRealmlistHost = form.getValues('advanced.realmlistHost')?.trim()
        if (!currentRealmlistHost && externalHost.trim()) {
          setValue('advanced.realmlistHost', externalHost.trim(), { shouldDirty: true })
        }
      } else {
        setValue('deployment.connectionVerified', false, { shouldDirty: true })
      }

      return merged
    } catch (error) {
      setTestProgress({ connection: 'complete', prerequisites: 'failed' })
      setValue('deployment.connectionVerified', false, { shouldDirty: true })
      const failed: RemoteConnectionTestResultDto = {
        success: false,
        message: apiErrorMessage(
          error,
          'SSH already succeeded. The manager timed out while installing Docker. Confirm the manager container is running, then Verify again.'
        ),
        prerequisites: sshData.prerequisites ?? [],
      }
      setTestResult(failed)
      return failed
    }
  }, [deploymentPayload, externalHost, form, runPrerequisiteCheck, setValue])

  const handleTestConnection = useCallback(async () => {
    setTesting(true)
    setTestResult(null)
    setTestProgress({ connection: 'active', prerequisites: 'pending' })
    setSetupResult(null)
    setValue('deployment.connectionVerified', false, { shouldDirty: true })

    try {
      await runConnectionTest()
    } catch (error) {
      setTestProgress({ connection: 'failed', prerequisites: 'pending' })
      setTestResult({
        success: false,
        message: apiErrorMessage(
          error,
          'Confirm the manager is running, then wait until the VM has finished booting and SSH port 22 is reachable.'
        ),
        prerequisites: [],
      })
    } finally {
      setTesting(false)
    }
  }, [runConnectionTest, setValue])

  const handleTestSshConnection = useCallback(async () => {
    setSshTesting(true)
    setSshTestResult(null)

    try {
      const sshRes = await systemApi.testRemoteConnection(
        {
          ...deploymentPayload,
          bootstrapSshKeyId: '',
          bootstrapUserSecured: false,
        },
        RemoteConnectionTestPhase.SshOnly
      )
      setSshTestResult(sshRes.data)
    } catch (error) {
      setSshTestResult({
        success: false,
        message: apiErrorMessage(
          error,
          'Confirm the manager is running, then wait until the VM has finished booting and SSH port 22 is reachable.'
        ),
        prerequisites: [],
      })
    } finally {
      setSshTesting(false)
    }
  }, [deploymentPayload])

  const handleSetupNow = useCallback(async () => {
    setSettingUp(true)
    setSetupResult(null)

    try {
      const res = await systemApi.provisionRemoteHost(
        buildProvisionRequest(
          externalHost,
          externalSshPort,
          externalSshUser,
          externalSshPrivateKey,
          savedSshKeyId,
          remoteOs,
          enableHostFirewall,
          true,
          authServerPort,
          worldServerPort,
          Number(externalSshPort) || 22
        )
      )
      setSetupResult(res.data)

      if (res.data.success) {
        setValue('deployment.firstTimeSetupCompleted', true, { shouldDirty: true })
        setTesting(true)
        try {
          await runConnectionTest()
        } finally {
          setTesting(false)
        }
      } else {
        setValue('deployment.firstTimeSetupCompleted', false, { shouldDirty: true })
        setValue('deployment.connectionVerified', false, { shouldDirty: true })
      }
    } catch (error) {
      setSetupResult({
        success: false,
        message: apiErrorMessage(
          error,
          'Confirm the manager is running, then wait until the VM has finished booting.'
        ),
        steps: [],
      })
    } finally {
      setSettingUp(false)
    }
  }, [
    authServerPort,
    enableHostFirewall,
    externalHost,
    externalSshPort,
    externalSshPrivateKey,
    externalSshUser,
    remoteOs,
    runConnectionTest,
    setValue,
    worldServerPort,
  ])

  const setupButtonDisabled = useMemo(() => {
    if (settingUp || testing || !credentialsReady) {
      return true
    }
    return !sshVerified
  }, [credentialsReady, settingUp, sshVerified, testing])

  const setupHint = useMemo(() => {
    if (!credentialsReady) {
      return 'Fill in the remote host, SSH user, and private key first.'
    }
    if (!testResult) {
      return 'Test the connection first so the platform can reach your VPC over SSH.'
    }
    if (!sshVerified) {
      return 'Fix the SSH connection before running first-time setup.'
    }
    if (dockerReady) {
      return 'Docker is already configured  -  Setup Now will verify each step and skip what is already in place.'
    }
    return 'Ready - the platform will install and configure Docker on your VPC.'
  }, [credentialsReady, dockerReady, sshVerified, testResult])

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-xl font-semibold text-gray-900">Deployment Target</h2>
        <p className="mt-1 text-sm text-gray-500">
          Choose where this stack&rsquo;s containers will run.
        </p>
      </div>

      <div className="flex gap-3">
        {[
          {
            value: DeploymentTarget.Local,
            label: 'Local',
            desc: 'Runs on this machine',
            icon: Server,
          },
          {
            value: DeploymentTarget.External,
            label: 'External VPC',
            desc: 'Runs on a remote host over SSH',
            icon: Cloud,
          },
        ].map((option) => (
          <label
            key={option.value}
            className={cn(
              'flex flex-1 cursor-pointer flex-col rounded-md border px-3 py-3 text-sm',
              deploymentTarget === option.value ? 'border-blue-500 bg-blue-50' : 'border-gray-300'
            )}
          >
            <span className="flex items-center gap-2 font-medium text-gray-800">
              <input
                type="radio"
                value={option.value}
                checked={deploymentTarget === option.value}
                onChange={() =>
                  setValue('deployment.target', option.value, { shouldDirty: true, shouldValidate: true })
                }
              />
              <option.icon className="h-4 w-4 text-gray-500" aria-hidden="true" />
              {option.label}
            </span>
            <span className="ml-6 mt-1 text-xs text-gray-500">{option.desc}</span>
          </label>
        ))}
      </div>

      {deploymentTarget === DeploymentTarget.Local && (
        <div className="rounded-lg border border-gray-200 bg-gray-50 p-4 text-sm text-gray-600">
          Containers will be built and run on the machine hosting this platform. No remote connection is
          required.
        </div>
      )}

      {deploymentTarget === DeploymentTarget.External && (
        <div className="space-y-4 rounded-lg border border-gray-200 p-4">
          <p className="text-sm text-gray-600">
            Connect to the server, then choose whether to configure the VPC or skip ahead to verification.
          </p>

          <DeploymentSubstep
            step={1}
            title="Operating system"
            description="Remote VPC hosts must run Ubuntu or Debian."
          >
            <p className="text-sm text-gray-700">Linux (Ubuntu / Debian)</p>
            <p className="mt-1 text-xs text-gray-600">
              Windows Server VPC hosts are not supported. Launch or connect an Ubuntu or Debian VM.
            </p>
          </DeploymentSubstep>

          <DeploymentSubstep
            step={2}
            title="Connect"
            description="Connect a cloud account to pick or launch a VM, or enter a host you already have."
          >
            <div className="space-y-4">
              <VpcConnectionMethodTabs
                disabled={testing || settingUp || sshTesting}
                remoteOs={remoteOs}
                cloudConnectionId={cloudConnectionId}
                onCloudConnectionIdChange={handleCloudConnectionIdChange}
                cloudProvider={cloudProvider}
                onCloudProviderChange={handleCloudProviderChange}
                externalHost={externalHost}
                externalSshUser={externalSshUser}
                savedSshKeyId={savedSshKeyId}
                bootstrapSshKeyId={bootstrapSshKeyId}
                sshCertificateVerified={sshCertificateVerified}
                onSshCertificateVerifiedChange={(verified) =>
                  setValue('deployment.sshCertificateVerified', verified, {
                    shouldDirty: true,
                    shouldValidate: true,
                  })
                }
                register={register}
                errors={errors}
                connectionFieldsReady={connectionFieldsReady}
                credentialsReady={credentialsReady}
                sshTesting={sshTesting}
                onTestConnection={handleTestSshConnection}
                sshTestResult={sshTestResult}
                onSwitchRemoteOs={applyRemoteOs}
                onSelectInstance={(instance) => {
                  setValue('deployment.externalHost', instance.publicHost, { shouldDirty: true, shouldValidate: true })
                  setValue('deployment.cloudInstanceId', instance.id, { shouldDirty: true })
                  setValue('deployment.cloudRegion', instance.region ?? '', { shouldDirty: true })
                  if (cloudConnectionId.trim()) {
                    setValue('deployment.cloudConnectionId', cloudConnectionId, { shouldDirty: true })
                  }
                  setValue('deployment.cloudProvider', instance.provider, { shouldDirty: true })
                  setValue('deployment.cloudInstanceType', instance.instanceType ?? '', { shouldDirty: true })
                  if (instance.suggestedSshUser) {
                    setValue('deployment.externalSshUser', instance.suggestedSshUser, {
                      shouldDirty: true,
                      shouldValidate: true,
                    })
                  }
                  onVpcBound?.()
                }}
                onLaunched={(result) => {
                  setValue('deployment.externalHost', result.instance.publicHost, {
                    shouldDirty: true,
                    shouldValidate: true,
                  })
                  setValue('deployment.cloudInstanceId', result.instance.id, { shouldDirty: true })
                  setValue('deployment.cloudRegion', result.instance.region ?? '', { shouldDirty: true })
                  if (cloudConnectionId.trim()) {
                    setValue('deployment.cloudConnectionId', cloudConnectionId, { shouldDirty: true })
                  }
                  setValue('deployment.cloudProvider', result.instance.provider, { shouldDirty: true })
                  setValue('deployment.cloudInstanceType', result.instance.instanceType ?? '', { shouldDirty: true })
                  if (result.instance.suggestedSshUser) {
                    setValue('deployment.externalSshUser', result.instance.suggestedSshUser, {
                      shouldDirty: true,
                      shouldValidate: true,
                    })
                  }
                  if (result.savedSshKeyId) {
                    setValue('deployment.savedSshKeyId', result.savedSshKeyId, {
                      shouldDirty: true,
                      shouldValidate: true,
                    })
                    setValue('deployment.externalSshPrivateKey', '', { shouldDirty: true, shouldValidate: true })
                  }
                  if (result.bootstrapSshKeyId) {
                    setValue('deployment.bootstrapSshKeyId', result.bootstrapSshKeyId, { shouldDirty: true })
                    setValue('deployment.bootstrapUserSecured', false, { shouldDirty: true })
                  }
                  setValue('deployment.vpcSetupMode', 'skip', { shouldDirty: true })
                  setValue('deployment.firstTimeSetupCompleted', true, { shouldDirty: true })
                  setValue('deployment.cloudSecurityGroupAcknowledged', true, { shouldDirty: true })
                  onVpcBound?.()
                }}
              >
                <div className="space-y-3">
                  <div>
                    <p className="text-xs font-semibold text-gray-900">SSH credentials</p>
                    <p className="mt-0.5 text-[11px] text-gray-600">
                      Required for a host you already have. Cloud account setup generates a key for you.
                    </p>
                  </div>

                  <SavedSshKeySelector
                    selectedKeyId={savedSshKeyId}
                    disabled={testing || settingUp || sshTesting}
                    onSelectedKeyIdChange={(id) => {
                      setValue('deployment.savedSshKeyId', id, { shouldDirty: true, shouldValidate: true })
                      if (id) {
                        setValue('deployment.externalSshPrivateKey', '', { shouldDirty: true, shouldValidate: true })
                      }
                    }}
                    onSelectKey={(key) => {
                      if (key?.defaultSshUser && !externalSshUser.trim()) {
                        setValue('deployment.externalSshUser', key.defaultSshUser, {
                          shouldDirty: true,
                          shouldValidate: true,
                        })
                      }
                    }}
                  />

                  {!usingSavedKey ? (
                    <>
                      <SshPrivateKeyField
                        id="external-ssh-key"
                        value={externalSshPrivateKey}
                        onChange={(value) => {
                          setValue('deployment.externalSshPrivateKey', value, {
                            shouldDirty: true,
                            shouldValidate: true,
                          })
                          if (value.trim()) {
                            setValue('deployment.savedSshKeyId', '', { shouldDirty: true, shouldValidate: true })
                          }
                        }}
                        error={errors.deployment?.externalSshPrivateKey?.message}
                        hint="PEM-encoded private key (.pem). Used for this session and saved encrypted when the box below is checked."
                        required
                      />
                      <label className="flex items-start gap-2 text-sm text-gray-700">
                        <input
                          type="checkbox"
                          checked={saveSshKeyToVault}
                          onChange={(event) =>
                            setValue('deployment.saveSshKeyToVault', event.target.checked, { shouldDirty: true })
                          }
                          className="mt-0.5 rounded border-gray-300"
                        />
                        <span>
                          Save SSH key on this platform{' '}
                          <span className="block text-xs text-gray-500">
                            Encrypted at rest so you can reuse it when creating more stacks.
                          </span>
                        </span>
                      </label>
                      {saveSshKeyToVault ? (
                        <FormField
                          label="Key label (optional)"
                          htmlFor="save-ssh-key-label"
                          hint="Shown in the saved key dropdown"
                        >
                          <input
                            id="save-ssh-key-label"
                            type="text"
                            placeholder="e.g. production operator key"
                            className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                            {...register('deployment.saveSshKeyLabel')}
                          />
                        </FormField>
                      ) : null}
                    </>
                  ) : (
                    <p className="text-xs text-gray-600">
                      Using saved key - paste a new key by choosing "Paste a new key below..." in the dropdown.
                    </p>
                  )}
                </div>
              </VpcConnectionMethodTabs>
            </div>
          </DeploymentSubstep>

          {!sshCertificateVerified ? (
            <p className="rounded-md border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-950">
              Verify azp-admin.pem in Connect before Verify VPC. Do not upload the bootstrap root.pem.
            </p>
          ) : (
          <>
          <DeploymentSubstep
            step={3}
            title="Verify VPC"
            description="Verify azp-admin.pem (not the bootstrap root.pem). Verify VPC then SSHs as root with that bootstrap key, locks those accounts, and finally confirms azp-admin SSH on its own."
          >
            <div className="space-y-4">
              <ul className="list-disc space-y-1 pl-4 text-[11px] text-gray-600">
                {plannedSetupItems().map((item) => (
                  <li key={item.id}>
                    <span className="font-medium text-gray-800">{item.title}.</span> {item.description}
                  </li>
                ))}
              </ul>

              <div className="flex flex-wrap items-center gap-3">
                <button
                  type="button"
                  onClick={() => void handleTestConnection()}
                  disabled={testing || settingUp || !credentialsReady}
                  className="inline-flex items-center gap-2 rounded-md bg-blue-700 px-3 py-1.5 text-xs font-semibold text-white hover:bg-blue-800 focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:opacity-60"
                >
                  {testing && <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />}
                  Verify VPC
                </button>
                <p className="text-xs text-gray-500">
                  {bootstrapUserSecured
                    ? 'Root bootstrap already finished on this VM. This run only confirms azp-admin SSH, then Docker and the cloud firewall.'
                    : `Logs in as root with a manager-only key, sets up azp-admin, disables internet SSH for root (${vpcProviderLabel(cloudProvider)} console stays), then verifies azp-admin separately.`}
                </p>
              </div>

              {(testing || testProgress) && (
                <div className="rounded-md border border-gray-200 bg-gray-50 p-3">
                  <ConnectionTestProgressBar
                    progress={testProgress ?? { connection: 'active', prerequisites: 'pending' }}
                  />
                </div>
              )}

              {testResult && (
                <div className="rounded-md border border-gray-200 bg-gray-50 p-3">
                  {testResult.prerequisites && testResult.prerequisites.length > 0 ? (
                    <>
                      <p className="mb-2 text-xs font-medium text-gray-700">Host and cloud checks</p>
                      <ul className="space-y-1.5">
                        {testResult.prerequisites.map((check) => (
                          <li key={check.name} className="flex items-start gap-2 text-xs">
                            {check.passed ? (
                              <CheckCircle2 className="mt-0.5 h-3.5 w-3.5 shrink-0 text-green-600" aria-hidden="true" />
                            ) : (
                              <XCircle className="mt-0.5 h-3.5 w-3.5 shrink-0 text-red-600" aria-hidden="true" />
                            )}
                            <span className={check.passed ? 'text-green-800' : 'text-red-800 whitespace-pre-wrap break-all'}>
                              <span className="font-medium">{check.name}:</span> {check.message}
                            </span>
                          </li>
                        ))}
                      </ul>
                    </>
                  ) : null}
                  <p
                    className={cn(
                      'text-xs',
                      testResult.prerequisites?.length ? 'mt-2' : '',
                      testResult.success ? 'text-green-700' : 'text-red-700'
                    )}
                  >
                    {testResult.message}
                  </p>
                  {hostOsMismatch ? (
                    <OsMismatchNotice
                      detectedOs={hostOsMismatch}
                      selectedOs={remoteOs}
                      onSwitchOs={applyRemoteOs}
                    />
                  ) : null}
                </div>
              )}

              {connectionVerified ? (
                <p className="flex items-center gap-1.5 text-xs font-medium text-green-800">
                  <CheckCircle2 className="h-3.5 w-3.5" aria-hidden="true" />
                  VPC verified on the host and cloud provider.
                </p>
              ) : (
                <p className="text-xs text-amber-800">
                  Verify VPC must pass before you can continue. It waits for launch user-data and installs
                  Docker if the VPC still does not have it. Use Repair host setup only when Verify cannot
                  finish setup on an existing VM.
                </p>
              )}

              <details className="rounded-md border border-gray-200 bg-white p-3">
                <summary className="cursor-pointer text-xs font-semibold text-gray-800">
                  Repair host setup
                </summary>
                <div className="mt-3 space-y-3">
                  <p className="text-xs text-gray-600">
                    Re-runs Docker, ufw, and OS baselines over SSH.
                    Use this if Verify VPC fails after cloud-init should have finished, or when you selected an existing VM.
                  </p>
                  <VpcSecurityOverviewSection />
                  <div className="flex flex-wrap items-center gap-3">
                    <button
                      type="button"
                      onClick={() => void handleSetupNow()}
                      disabled={setupButtonDisabled}
                      className="inline-flex items-center gap-2 rounded-md bg-amber-700 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-800 focus:outline-none focus:ring-2 focus:ring-amber-500 disabled:opacity-60"
                    >
                      {settingUp && <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />}
                      Repair setup
                    </button>
                    <p className="text-xs text-amber-900">{setupHint}</p>
                  </div>
                  {setupResult && (
                    <div
                      className={cn(
                        'rounded-md border p-3',
                        setupResult.success ? 'border-green-200 bg-green-50' : 'border-red-200 bg-red-50'
                      )}
                    >
                      <p
                        className={cn(
                          'text-xs font-medium',
                          setupResult.success ? 'text-green-800' : 'text-red-800'
                        )}
                      >
                        {setupResult.message}
                      </p>
                    </div>
                  )}
                </div>
              </details>
            </div>
          </DeploymentSubstep>

          {deploymentTarget === DeploymentTarget.External && !connectionVerified && (
            <p className="text-xs text-amber-700">
              Verify VPC (host firewall, OS baselines, Docker, and cloud security groups) before continuing.
            </p>
          )}
          </>
          )}
        </div>
      )}
    </div>
  )
}

export {
  ManualVpcDockerSetupPanel,
  SetupSectionStatusIcon,
  VpcLaunchGuidePanel,
  computeSetupSectionStatuses,
  getSetupSectionDetail,
  setupNeedsManualDockerInstall,
  setupSectionStatusLabel,
}
