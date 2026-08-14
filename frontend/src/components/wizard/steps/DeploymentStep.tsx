import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { CheckCircle2, Cloud, Copy, Loader2, Server, Sparkles, XCircle } from 'lucide-react'
import { FormField } from '@/components/wizard/common/FormField'
import { CloudTerminalPanel } from '@/components/wizard/common/CloudTerminalPanel'
import { VpcConnectionMethodTabs } from '@/components/wizard/common/VpcConnectionMethodTabs'
import { VpcSecurityOverviewSection } from '@/components/wizard/common/VpcSecurityOverviewSection'
import { SavedSshKeySelector } from '@/components/wizard/common/SavedSshKeySelector'
import { SshPrivateKeyField } from '@/components/wizard/common/SshPrivateKeyField'
import type { WizardForm } from '@/components/wizard/types'
import { DEFAULT_ARMORY_PORT, DEFAULT_CLIENT_PORT } from '@/lib/stack-network-defaults'
import { CloudSecurityGroupGuideDialog } from '@/components/stacks/CloudSecurityGroupGuideDialog'
import { cn } from '@/lib/utils'
import { systemApi } from '@/services/api'
import {
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
}

const PLANNED_SETUP_ITEMS = [
  {
    id: 'docker',
    title: 'Setting up Docker',
    description: 'Install Docker Engine & Compose, start the service, and grant your SSH user access.',
    available: true,
  },
  {
    id: 'baseline',
    title: 'OS security baselines',
    description: 'Enable automatic security updates on Ubuntu/Debian.',
    available: true,
  },
  {
    id: 'firewall',
    title: 'Configure host firewall (ufw)',
    description: 'Allow SSH and player/web ports; keep MySQL and SOAP closed (Docker binds them on the VPC IP).',
    available: true,
  },
  {
    id: 'cloud-sg',
    title: 'Configure cloud security group',
    description: 'Mirror the same allow/deny rules in AWS/GCP/Azure (manual checklist for now).',
    available: true,
  },
] as const

type SetupSectionId = (typeof PLANNED_SETUP_ITEMS)[number]['id']
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
        <code className="text-[11px]">ubuntu</code> on AWS). Paste these commands, then log out and back in
        (or run <code className="text-[11px]">newgrp docker</code>) so group membership applies. Click{' '}
        <strong>Test connection</strong> again — Setup Now will skip install if Docker is already running.
      </p>
      <pre className="mt-2 overflow-x-auto rounded border border-amber-200 bg-amber-50/80 p-2 font-mono text-[11px] leading-relaxed">
        {lines.join('\n')}
      </pre>
      <p className="mt-2 text-[11px] text-amber-900">
        Optional — allow the platform to run future setup without a sudo password (replace the username if
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
        'Expand Advanced details at the bottom of the launch form (easy to miss — scroll down past storage and tags).',
        'Paste the script into User data, allow SSH (port 22) from your IP, create/download the .pem key pair, then Launch.',
      ],
    },
    gcp: {
      title: 'Google Cloud (Compute Engine)',
      steps: [
        'Compute Engine → Create instance → pick Ubuntu 22.04 or 24.04.',
        'Open Management → Automation → Startup script (not SSH keys).',
        'Paste the script, allow TCP:22 in the firewall, add your SSH key under Security → SSH keys, then Create.',
      ],
    },
    digitalocean: {
      title: 'DigitalOcean (Droplet)',
      steps: [
        'Create → Droplets → pick Ubuntu 22.04 or 24.04.',
        'Expand Advanced Options → check User data.',
        'Paste the script, add your SSH key, then Create Droplet.',
      ],
    },
    other: {
      title: 'Other provider (bare metal, etc.)',
      steps: [
        'Create a new Ubuntu 22.04/24.04 VM (not an existing one).',
        'Look for User data, Custom data, Cloud-init, or Startup script in the create wizard.',
        'Paste the script there — it runs once on first boot. Allow SSH (port 22) from your IP.',
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
        <p className="text-xs font-medium text-gray-800">Bootstrap script (Linux)</p>
      )}
      <p className={cn('text-xs text-blue-900', !embedded || hintsOnly ? undefined : 'mt-1', hintsOnly && 'pt-0')}>
        {hintsOnly ? (
          <>
            Creating a <span className="font-medium">new</span> VM? Paste the bootstrap script into your
            provider&apos;s startup / user-data field — it runs once on first boot. For an existing server, use
            the terminal sticky note instead.
          </>
        ) : (
          <>
            The script below works on <span className="font-medium">any</span> cloud that supports Ubuntu and a
            startup/user-data field (AWS, GCP, DigitalOcean, Azure, etc.). It only runs when you{' '}
            <span className="font-medium">create</span> a new VM — not on a server you already started.
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
            <p className="mt-2 text-xs text-blue-800">Loading launch script…</p>
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

function sshCheckPassed(result: RemoteConnectionTestResultDto | null): boolean {
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
  { id: 'connection', label: 'SSH connection' },
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

export function DeploymentStep({ form }: DeploymentStepProps) {
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
  const firstTimeSetupCompleted = watch('deployment.firstTimeSetupCompleted')
  const remoteOs = watch('deployment.remoteOs') ?? RemoteHostOs.Linux
  const enableHostFirewall = watch('deployment.enableHostFirewall') ?? true
  const cloudSecurityGroupAcknowledged = watch('deployment.cloudSecurityGroupAcknowledged')
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
    }),
    [
      externalHost,
      externalSshPort,
      externalSshPrivateKey,
      externalSshUser,
      saveSshKeyLabel,
      saveSshKeyToVault,
      savedSshKeyId,
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
  const [sgGuideOpen, setSgGuideOpen] = useState(false)
  const [cloudConnectionId, setCloudConnectionId] = useState('')

  const connectionFieldsReady =
    externalHost.trim().length > 0 && externalSshUser.trim().length > 0

  const credentialsReady =
    connectionFieldsReady
    && (usingSavedKey || externalSshPrivateKey.trim().length > 0)

  const sshVerified = sshCheckPassed(testResult)
  const dockerReady = prerequisitesMet(testResult)

  useEffect(() => {
    setValue('deployment.connectionVerified', false, { shouldDirty: true })
    setValue('deployment.firstTimeSetupCompleted', false, { shouldDirty: true })
    setValue('deployment.cloudSecurityGroupAcknowledged', false, { shouldDirty: true })
    setTestResult(null)
    setTestProgress(null)
    setSshTestResult(null)
    setSetupResult(null)
  }, [deploymentTarget, externalHost, externalSshPort, externalSshUser, externalSshPrivateKey, savedSshKeyId, setValue])

  const { data: securityProfile } = useQuery({
    queryKey: ['vpc-security-profile', externalHost, authServerPort, worldServerPort],
    queryFn: async () =>
      (
        await systemApi.vpcSecurityProfile({
          host: externalHost.trim(),
          authPort: authServerPort,
          worldPort: worldServerPort,
          armoryPort: DEFAULT_ARMORY_PORT,
          clientPort: DEFAULT_CLIENT_PORT,
        })
      ).data,
    enabled: deploymentTarget === DeploymentTarget.External && externalHost.trim().length > 0,
  })

  const { data: launchData } = useQuery({
    queryKey: ['vpc-launch-user-data', externalSshUser],
    queryFn: async () => (await systemApi.vpcLaunchUserData(externalSshUser.trim() || 'ubuntu')).data,
    enabled: deploymentTarget === DeploymentTarget.External,
  })

  const runPrerequisiteCheck = useCallback(
    async (sshData: RemoteConnectionTestResultDto): Promise<RemoteConnectionTestResultDto> => {
      const prereqRes = await systemApi.testRemoteConnection(
        deploymentPayload,
        RemoteConnectionTestPhase.PrerequisitesOnly
      )
      return {
        ...prereqRes.data,
        prerequisites: [
          ...(sshData.prerequisites ?? []),
          ...(prereqRes.data.prerequisites ?? []).filter((check) => check.name !== 'SSH'),
        ],
      }
    },
    [deploymentPayload]
  )

  const runConnectionTest = useCallback(async (): Promise<RemoteConnectionTestResultDto | null> => {
    setTestProgress({ connection: 'active', prerequisites: 'pending' })

    const sshRes = await systemApi.testRemoteConnection(deploymentPayload, RemoteConnectionTestPhase.SshOnly)
    const sshData = sshRes.data
    const sshPassed = sshCheckPassed(sshData)

    if (!sshPassed) {
      setTestProgress({ connection: 'failed', prerequisites: 'pending' })
      setTestResult(sshData)
      setValue('deployment.connectionVerified', false, { shouldDirty: true })
      return sshData
    }

    setTestProgress({ connection: 'complete', prerequisites: 'active' })

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
  }, [deploymentPayload, externalHost, form, runPrerequisiteCheck, setValue])

  const handleTestConnection = useCallback(async () => {
    setTesting(true)
    setTestResult(null)
    setTestProgress({ connection: 'active', prerequisites: 'pending' })
    setSetupResult(null)
    setValue('deployment.connectionVerified', false, { shouldDirty: true })
    setValue('deployment.firstTimeSetupCompleted', false, { shouldDirty: true })

    try {
      await runConnectionTest()
    } catch {
      setTestProgress({ connection: 'failed', prerequisites: 'pending' })
      setTestResult({
        success: false,
        message: 'Failed to reach the platform to run the test.',
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
      const sshRes = await systemApi.testRemoteConnection(deploymentPayload, RemoteConnectionTestPhase.SshOnly)
      setSshTestResult(sshRes.data)
    } catch {
      setSshTestResult({
        success: false,
        message: 'Failed to reach the platform to run the test.',
        prerequisites: [],
      })
    } finally {
      setSshTesting(false)
    }
  }, [deploymentPayload])

  const handleSetupNow = useCallback(async () => {
    if (remoteOs === RemoteHostOs.Windows) {
      return
    }

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
    } catch {
      setSetupResult({
        success: false,
        message: 'Failed to reach the platform to run setup.',
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
    if (settingUp || testing || !credentialsReady || remoteOs === RemoteHostOs.Windows) {
      return true
    }
    return !sshVerified
  }, [credentialsReady, remoteOs, settingUp, sshVerified, testing])

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
      return 'Docker is already configured — Setup Now will verify each step and skip what is already in place.'
    }
    return 'Ready — the platform will install and configure Docker on your VPC.'
  }, [credentialsReady, dockerReady, sshVerified, testResult])

  const setupSectionStatuses = useMemo(
    () =>
      computeSetupSectionStatuses(
        setupResult?.steps,
        settingUp,
        enableHostFirewall,
        cloudSecurityGroupAcknowledged
      ),
    [cloudSecurityGroupAcknowledged, enableHostFirewall, settingUp, setupResult?.steps]
  )

  return (
    <div className="space-y-6">
      <div>
        <h2 className="text-xl font-semibold text-gray-900">Deployment Target</h2>
        <p className="mt-1 text-sm text-gray-500">
          Choose where this stack&rsquo;s containers will run. Select an external VPC if you have already
          provisioned a server on AWS, GCP, or another cloud provider.
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
            Follow the sections below: review ports, choose how to connect, add your SSH key, then bootstrap over the terminal.
          </p>

          <DeploymentSubstep
            step={1}
            title="Operating system"
            description="Choose the OS running on your remote host."
          >
            <fieldset>
              <legend className="sr-only">Remote host operating system</legend>
              <div className="flex flex-wrap gap-3">
                {[
                  { value: RemoteHostOs.Linux, label: 'Linux (Ubuntu / Debian)', supported: true },
                  { value: RemoteHostOs.Windows, label: 'Windows', supported: false },
                ].map((option) => (
                  <label
                    key={option.value}
                    className={cn(
                      'flex cursor-pointer items-center gap-2 rounded-md border px-3 py-2 text-sm',
                      remoteOs === option.value ? 'border-blue-500 bg-blue-50' : 'border-gray-300',
                      !option.supported && 'opacity-70'
                    )}
                  >
                    <input
                      type="radio"
                      name="remote-os"
                      disabled={!option.supported}
                      checked={remoteOs === option.value}
                      onChange={() =>
                        setValue('deployment.remoteOs', option.value, { shouldDirty: true, shouldValidate: true })
                      }
                    />
                    <span>
                      {option.label}
                      {!option.supported && (
                        <span className="ml-1.5 text-[10px] font-semibold uppercase text-gray-500">Coming soon</span>
                      )}
                    </span>
                  </label>
                ))}
              </div>
            </fieldset>
          </DeploymentSubstep>

          <DeploymentSubstep
            step={2}
            title="Connection & bootstrap"
            description="Connect to your server, run the bootstrap script, and prepare Docker on the VM."
          >
            <div className="space-y-4">
              <VpcSecurityOverviewSection />

              <VpcConnectionMethodTabs
                disabled={testing || settingUp || sshTesting}
                cloudConnectionId={cloudConnectionId}
                onCloudConnectionIdChange={setCloudConnectionId}
                externalHost={externalHost}
                externalSshUser={externalSshUser}
                savedSshKeyId={savedSshKeyId}
                register={register}
                errors={errors}
                connectionFieldsReady={connectionFieldsReady}
                credentialsReady={credentialsReady}
                sshTesting={sshTesting}
                onTestConnection={handleTestSshConnection}
                sshTestResult={sshTestResult}
                onSelectInstance={(instance) => {
                  setValue('deployment.externalHost', instance.publicHost, { shouldDirty: true, shouldValidate: true })
                  if (instance.suggestedSshUser) {
                    setValue('deployment.externalSshUser', instance.suggestedSshUser, {
                      shouldDirty: true,
                      shouldValidate: true,
                    })
                  }
                }}
                onLaunched={(result) => {
                  setValue('deployment.externalHost', result.instance.publicHost, {
                    shouldDirty: true,
                    shouldValidate: true,
                  })
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
                }}
              >
                <div className="space-y-3">
                  <div>
                    <p className="text-xs font-semibold text-gray-900">SSH credentials</p>
                    <p className="mt-0.5 text-[11px] text-gray-600">
                      Required for the connection test and terminal below.
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
                            placeholder="e.g. AWS production key"
                            className="block w-full rounded-md border border-gray-300 px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                            {...register('deployment.saveSshKeyLabel')}
                          />
                        </FormField>
                      ) : null}
                    </>
                  ) : (
                    <p className="text-xs text-gray-600">
                      Using saved key — paste a new key by choosing “Paste a new key below…” in the dropdown.
                    </p>
                  )}
                </div>
              </VpcConnectionMethodTabs>

              {remoteOs === RemoteHostOs.Linux ? (
                <>
                  <CloudTerminalPanel
                    deployment={deploymentPayload}
                    credentialsReady={credentialsReady}
                    disabled={remoteOs !== RemoteHostOs.Linux}
                    bootstrapScript={launchData?.script}
                    sshUser={externalSshUser}
                  />
                  <VpcLaunchGuidePanel
                    sshUser={externalSshUser}
                    launchData={launchData}
                    embedded
                    hintsOnly
                  />
                </>
              ) : (
                <p className="text-xs text-gray-600">Windows bootstrap scripts will be added when Windows VPC support ships.</p>
              )}
            </div>
          </DeploymentSubstep>

          <DeploymentSubstep
            step={3}
            title="Verify connection"
            description="Confirm the platform can reach your server and Docker is available."
          >
            <div className="space-y-3">
            <div className="flex flex-wrap items-center gap-3">
              <button
                type="button"
                onClick={() => void handleTestConnection()}
                disabled={testing || settingUp || !credentialsReady}
                className="inline-flex items-center gap-2 rounded-md border border-gray-300 bg-white px-3 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50 focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:opacity-60"
              >
                {testing && <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />}
                Test connection &amp; verify prerequisites
              </button>
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
                    <p className="mb-2 text-xs font-medium text-gray-700">Remote host prerequisites</p>
                    <ul className="space-y-1.5">
                      {testResult.prerequisites.map((check) => (
                        <li key={check.name} className="flex items-start gap-2 text-xs">
                          {check.passed ? (
                            <CheckCircle2 className="mt-0.5 h-3.5 w-3.5 shrink-0 text-green-600" aria-hidden="true" />
                          ) : (
                            <XCircle className="mt-0.5 h-3.5 w-3.5 shrink-0 text-red-600" aria-hidden="true" />
                          )}
                          <span className={check.passed ? 'text-green-800' : 'text-red-800'}>
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
              </div>
            )}
            </div>
          </DeploymentSubstep>

          <DeploymentSubstep
            step={4}
            title="First Time Setup"
            description="Optional — install Docker (if you did not bootstrap manually), apply security baselines, configure host firewall, and acknowledge cloud security group rules."
          >
          <section className="rounded-lg border-2 border-amber-300 bg-amber-50 p-4">
            <div className="flex items-start gap-3">
              <Sparkles className="mt-0.5 h-5 w-5 shrink-0 text-amber-700" aria-hidden="true" />
              <div className="flex-1 space-y-3">
                <div>
                  <p className="text-xs text-amber-900">
                    New cloud instances usually need a one-time bootstrap before stacks can deploy. After SSH
                    is working, run setup here — the platform will configure the remote host for you.
                  </p>
                </div>

                <ul className="space-y-2">
                  {PLANNED_SETUP_ITEMS.map((item) => {
                    const status = item.available
                      ? setupSectionStatuses[item.id]
                      : ('pending' as SetupSectionStatus)
                    const detail = getSetupSectionDetail(
                      item.id,
                      setupResult?.steps,
                      cloudSecurityGroupAcknowledged
                    )

                    return (
                      <li key={item.id} className="flex items-start gap-2 text-xs">
                        {item.available ? (
                          <SetupSectionStatusIcon status={status} />
                        ) : (
                          <span className="mt-0.5 inline-flex h-4 w-4 shrink-0 items-center justify-center rounded-full border border-amber-400 text-[9px] font-semibold text-amber-700">
                            …
                          </span>
                        )}
                        <span
                          className={cn(
                            item.available ? 'text-amber-950' : 'text-amber-800/80',
                            status === 'failed' && 'text-red-900',
                            status === 'passed' && item.available && 'text-green-900'
                          )}
                        >
                          <span className="flex flex-wrap items-center gap-2">
                            <span className="font-medium">{item.title}</span>
                            {item.available && (
                              <span
                                className={cn(
                                  'rounded px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide',
                                  status === 'running' && 'bg-blue-100 text-blue-800',
                                  status === 'passed' && 'bg-green-100 text-green-800',
                                  status === 'failed' && 'bg-red-100 text-red-800',
                                  status === 'skipped' && 'bg-amber-100 text-amber-800',
                                  status === 'pending' && 'bg-amber-100/80 text-amber-800'
                                )}
                              >
                                {setupSectionStatusLabel(status)}
                              </span>
                            )}
                            {!item.available && (
                              <span className="rounded bg-amber-200/80 px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-amber-900">
                                Coming soon
                              </span>
                            )}
                          </span>
                          <span className="mt-0.5 block text-amber-900/90">{item.description}</span>
                          {item.id === 'cloud-sg' && item.available && (
                            <button
                              type="button"
                              onClick={() => setSgGuideOpen(true)}
                              className="mt-1.5 inline-flex items-center rounded-md border border-amber-400 bg-white px-2 py-1 text-[11px] font-medium text-amber-950 hover:bg-amber-100"
                            >
                              Open setup guide
                            </button>
                          )}
                          {detail && (
                            <span
                              className={cn(
                                'mt-1 block text-[11px]',
                                status === 'failed' ? 'text-red-800' : 'text-green-800'
                              )}
                            >
                              {detail}
                            </span>
                          )}
                        </span>
                      </li>
                    )
                  })}
                </ul>

                <div className="flex flex-wrap items-center gap-3">
                  <button
                    type="button"
                    onClick={() => void handleSetupNow()}
                    disabled={setupButtonDisabled}
                    className="inline-flex items-center gap-2 rounded-md bg-amber-700 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-800 focus:outline-none focus:ring-2 focus:ring-amber-500 disabled:opacity-60"
                  >
                    {settingUp && <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />}
                    Setup Now
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

                {setupResult && !setupResult.success && setupNeedsManualDockerInstall(setupResult.steps) && (
                  <ManualVpcDockerSetupPanel sshUser={externalSshUser} />
                )}

                {securityProfile && (
                  <p className="text-xs text-amber-900">
                    Ports for this stack: auth <span className="font-mono">{authServerPort}</span>, world{' '}
                    <span className="font-mono">{worldServerPort}</span>, armory{' '}
                    <span className="font-mono">{DEFAULT_ARMORY_PORT}</span>, client{' '}
                    <span className="font-mono">{DEFAULT_CLIENT_PORT}</span>. Open the cloud setup guide for
                    the full inbound rule list.
                  </p>
                )}

                <div className="rounded-md border border-amber-200 bg-white/80 p-3">
                  <p className="text-xs font-medium text-amber-950">Cloud security group (manual)</p>
                  <p className="mt-1 text-xs text-amber-900">
                    Setup Now configures <span className="font-medium">ufw</span> on the Linux host. You must
                    also add matching inbound rules in your cloud provider&apos;s security group — the platform
                    does not change AWS/GCP/Azure rules automatically yet.
                  </p>
                  <div className="mt-3 flex flex-wrap items-center gap-3">
                    <button
                      type="button"
                      onClick={() => setSgGuideOpen(true)}
                      className="inline-flex items-center rounded-md bg-amber-800 px-3 py-1.5 text-xs font-semibold text-white hover:bg-amber-900"
                    >
                      Open setup guide
                    </button>
                    {cloudSecurityGroupAcknowledged ? (
                      <span className="inline-flex items-center gap-1.5 text-xs font-medium text-green-800">
                        <CheckCircle2 className="h-3.5 w-3.5" aria-hidden="true" />
                        Cloud rules acknowledged
                      </span>
                    ) : (
                      <span className="text-xs text-amber-900">
                        Required before continuing — follow the guide, then confirm in the dialog.
                      </span>
                    )}
                  </div>
                </div>

                <CloudSecurityGroupGuideDialog
                  open={sgGuideOpen}
                  onClose={() => setSgGuideOpen(false)}
                  host={externalHost}
                  sshPort={Number(externalSshPort) || 22}
                  profile={securityProfile}
                  acknowledged={cloudSecurityGroupAcknowledged}
                  onAcknowledgedChange={(value) =>
                    setValue('deployment.cloudSecurityGroupAcknowledged', value, { shouldDirty: true })
                  }
                />

                {!connectionVerified && sshVerified && !dockerReady && !firstTimeSetupCompleted && (
                  <p className="text-xs font-medium text-amber-900">
                    Run <span className="font-semibold">Setup Now</span> to install Docker before continuing.
                  </p>
                )}
                {connectionVerified && dockerReady && !firstTimeSetupCompleted && (
                  <p className="text-xs font-medium text-amber-900">
                    Prerequisites are met. Run <span className="font-semibold">Setup Now</span> to apply
                    security baselines and host firewall rules, or continue if you have configured those manually.
                  </p>
                )}
                {connectionVerified && cloudSecurityGroupAcknowledged && (
                  <p className="flex items-center gap-1.5 text-xs font-medium text-green-800">
                    <CheckCircle2 className="h-3.5 w-3.5" aria-hidden="true" />
                    Remote host is ready — you can continue to the next step.
                  </p>
                )}
                {connectionVerified && !cloudSecurityGroupAcknowledged && (
                  <p className="text-xs font-medium text-amber-900">
                    Connection verified — acknowledge the cloud security group checklist before continuing.
                  </p>
                )}
              </div>
            </div>
          </section>
          </DeploymentSubstep>

          {deploymentTarget === DeploymentTarget.External && !connectionVerified && (
            <p className="text-xs text-amber-700">
              Complete connection verification (and first-time setup if Docker is not installed) before continuing.
            </p>
          )}
        </div>
      )}
    </div>
  )
}
