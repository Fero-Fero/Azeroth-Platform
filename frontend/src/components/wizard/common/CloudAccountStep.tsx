import { useEffect, useMemo, useRef, useState } from 'react'
import { CheckCircle2, Repeat, Settings2, ShieldCheck } from 'lucide-react'
import { useQuery } from '@tanstack/react-query'
import { cloudApi } from '@/services/api'
import {
  CloudProvider,
  RemoteHostOs,
  type CloudInstanceDto,
  type CloudLaunchResultDto,
  type CloudProviderConnectionDto,
} from '@/types/stack.types'
import { cn } from '@/lib/utils'
import { normalizePem, pemDownloadFilename, pemFingerprint } from '@/lib/ssh-key-download'
import { DEFAULT_OPERATOR_SSH_USER, imageDefaultSshUser } from '@/lib/ssh-user'
import { filterProvidersForRemoteOs, providerSupportsRemoteOs } from '@/lib/vpc-providers'
import { CloudConnectionLinkForm } from '@/components/wizard/common/CloudConnectionLinkForm'
import { CloudInstanceSetupDialog } from '@/components/wizard/common/CloudInstanceSetupDialog'
import { SshKeyDownloadButton } from '@/components/wizard/common/SshKeyDownloadButton'

const PROVIDER_OPTIONS: Array<{ id: CloudProvider; label: string }> = [
  { id: CloudProvider.Aws, label: 'AWS' },
  { id: CloudProvider.DigitalOcean, label: 'DigitalOcean' },
  { id: CloudProvider.Hetzner, label: 'Hetzner' },
  { id: CloudProvider.Vultr, label: 'Vultr' },
  { id: CloudProvider.Gcp, label: 'GCP' },
  { id: CloudProvider.Azure, label: 'Azure' },
]

function parseCloudProvider(value?: string): CloudProvider | null {
  return PROVIDER_OPTIONS.find((option) => option.id === value)?.id ?? null
}

interface CloudAccountStepProps {
  disabled?: boolean
  remoteOs?: RemoteHostOs
  connectionId: string
  onConnectionIdChange: (id: string) => void
  cloudProvider?: string
  onCloudProviderChange?: (provider: string) => void
  externalHost: string
  externalSshUser: string
  savedSshKeyId: string
  bootstrapSshKeyId?: string
  sshCertificateVerified: boolean
  onSshCertificateVerifiedChange: (verified: boolean) => void
  onSelectInstance: (instance: CloudInstanceDto) => void
  onLaunched: (result: CloudLaunchResultDto) => void
}

export function CloudAccountStep({
  disabled = false,
  remoteOs,
  connectionId,
  onConnectionIdChange,
  cloudProvider,
  onCloudProviderChange,
  externalHost,
  externalSshUser,
  savedSshKeyId,
  bootstrapSshKeyId = '',
  sshCertificateVerified,
  onSshCertificateVerifiedChange,
  onSelectInstance,
  onLaunched,
}: CloudAccountStepProps) {
  const [provider, setProvider] = useState<CloudProvider>(
    () => parseCloudProvider(cloudProvider) ?? CloudProvider.Aws
  )
  const [setupOpen, setSetupOpen] = useState(false)
  const [pendingConnection, setPendingConnection] = useState<CloudProviderConnectionDto | null>(null)
  const [launchedKey, setLaunchedKey] = useState<{
    pem: string | null
    keyId: string
    label: string
  } | null>(null)
  const [bootstrapKey, setBootstrapKey] = useState<{
    pem: string | null
    keyId: string
    label: string
  } | null>(null)
  const [verifyError, setVerifyError] = useState<string | null>(null)
  const [verifying, setVerifying] = useState(false)
  const [didVerifyLaunchKey, setDidVerifyLaunchKey] = useState(false)
  const verifyInputRef = useRef<HTMLInputElement>(null)

  const { data: connections } = useQuery({
    queryKey: ['cloud-connections'],
    queryFn: async () => (await cloudApi.listConnections()).data,
  })

  const providerConnections = useMemo(
    () => (connections ?? []).filter((connection) => connection.provider === provider),
    [connections, provider]
  )

  const visibleProviders = useMemo(
    () => filterProvidersForRemoteOs(PROVIDER_OPTIONS, remoteOs),
    [remoteOs]
  )

  useEffect(() => {
    const saved = parseCloudProvider(cloudProvider)
    if (saved && providerSupportsRemoteOs(saved, remoteOs)) {
      setProvider((current) => (current === saved ? current : saved))
      return
    }

    if (!providerSupportsRemoteOs(provider, remoteOs)) {
      const next = visibleProviders[0]?.id
      if (next) {
        setProvider(next)
        onCloudProviderChange?.(next)
      }
    }
  }, [cloudProvider, onCloudProviderChange, provider, remoteOs, visibleProviders])

  useEffect(() => {
    if (!connections) {
      return
    }

    const matches = connections.filter((connection) => connection.provider === provider)
    if (matches.some((connection) => connection.id === connectionId)) {
      return
    }

    const nextId = matches[0]?.id ?? ''
    if (nextId !== connectionId) {
      if (nextId) {
        onCloudProviderChange?.(provider)
      }
      onConnectionIdChange(nextId)
    }
  }, [connectionId, connections, onCloudProviderChange, onConnectionIdChange, provider])

  const selectedConnection = useMemo(() => {
    if (pendingConnection && pendingConnection.provider === provider) {
      if (!connectionId || pendingConnection.id === connectionId) {
        return pendingConnection
      }
    }

    return providerConnections.find((connection) => connection.id === connectionId) ?? null
  }, [connectionId, pendingConnection, provider, providerConnections])

  const handleLinked = (created: CloudProviderConnectionDto) => {
    setPendingConnection(created)
    onCloudProviderChange?.(created.provider)
    onConnectionIdChange(created.id)
    setSetupOpen(true)
  }

  const handleVerified = () => {
    setLaunchedKey(null)
    setVerifyError(null)
    setDidVerifyLaunchKey(true)
    onSshCertificateVerifiedChange(true)
  }

  const handleVerifyFile = async (file: File) => {
    const key = launchedKey ?? (
      savedSshKeyId.trim()
        ? { pem: null, keyId: savedSshKeyId.trim(), label: externalSshUser.trim() || DEFAULT_OPERATOR_SSH_USER }
        : null
    )
    if (!key) {
      return
    }

    setVerifying(true)
    setVerifyError(null)
    try {
      const uploaded = normalizePem(await file.text())
      if (!uploaded.includes('BEGIN') || !uploaded.includes('PRIVATE KEY')) {
        setVerifyError('Select the downloaded .pem private key file.')
        return
      }

      if (key.pem) {
        if (normalizePem(key.pem) !== uploaded) {
          setVerifyError(
            `This file does not match ${pemDownloadFilename(key.label)}. Use that file, not ${pemDownloadFilename(bootstrapKey?.label || imageDefaultSshUser(provider, remoteOs))}.`,
          )
          return
        }

        handleVerified()
        return
      }

      if (!key.keyId) {
        setVerifyError('No launched key is available to verify.')
        return
      }

      const keys = (await cloudApi.listSshKeys()).data
      const expected = keys.find((item) => item.id === key.keyId)?.fingerprint
      const actual = await pemFingerprint(uploaded)
      if (!expected || expected !== actual) {
        setVerifyError(
          `This file does not match ${pemDownloadFilename(key.label)}. Use that file, not ${pemDownloadFilename(bootstrapKey?.label || imageDefaultSshUser(provider, remoteOs))}.`,
        )
        return
      }

      handleVerified()
    } catch {
      setVerifyError('Could not read that file. Try the downloaded .pem again.')
    } finally {
      setVerifying(false)
      if (verifyInputRef.current) {
        verifyInputRef.current.value = ''
      }
    }
  }

  const restoredLaunchKey =
    !launchedKey && !sshCertificateVerified && savedSshKeyId.trim()
      ? {
          pem: null,
          keyId: savedSshKeyId.trim(),
          label: externalSshUser.trim() || DEFAULT_OPERATOR_SSH_USER,
        }
      : null
  const activeLaunchKey = launchedKey ?? restoredLaunchKey
  const pendingCertificate = activeLaunchKey != null && !sshCertificateVerified
  const operatorPemName = pemDownloadFilename(activeLaunchKey?.label || DEFAULT_OPERATOR_SSH_USER)
  const bootstrapPemName = pemDownloadFilename(
    bootstrapKey?.label || imageDefaultSshUser(provider, remoteOs),
  )
  const activeBootstrapKey =
    bootstrapKey
    ?? (bootstrapSshKeyId.trim()
      ? {
          pem: null,
          keyId: bootstrapSshKeyId.trim(),
          label: imageDefaultSshUser(provider, remoteOs),
        }
      : null)

  return (
    <div className="space-y-3">
      <p className="text-xs text-gray-600">
        Connect the account, then set up the instance. Launch downloads two keys named after the SSH users
        (root.pem and azp-admin.pem), applies the firewall, and fills in host and user.
      </p>

      <div className="flex flex-wrap gap-2" role="tablist" aria-label="Cloud provider">
        {visibleProviders.map((option) => (
          <button
            key={option.id}
            type="button"
            role="tab"
            aria-selected={provider === option.id}
            disabled={disabled}
            onClick={() => {
              setProvider(option.id)
              onCloudProviderChange?.(option.id)
            }}
            className={cn(
              'rounded-md border px-2.5 py-1 text-[11px] font-medium',
              provider === option.id
                ? 'border-blue-500 bg-white text-blue-900'
                : 'border-gray-200 bg-gray-50 text-gray-700 hover:bg-gray-100'
            )}
          >
            {option.label}
          </button>
        ))}
      </div>

      {providerConnections.length > 1 ? (
        <div>
          <label htmlFor="cloud-account-select" className="block text-xs font-medium text-gray-800">
            Linked account
          </label>
          <select
            id="cloud-account-select"
            value={selectedConnection?.id ?? ''}
            disabled={disabled}
            onChange={(event) => onConnectionIdChange(event.target.value)}
            className="mt-1 block w-full rounded-md border border-gray-300 bg-white px-3 py-2 text-sm shadow-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          >
            {providerConnections.map((connection) => (
              <option key={connection.id} value={connection.id}>
                {connection.label}
                {connection.accountHint ? ` · ${connection.accountHint}` : ''}
              </option>
            ))}
          </select>
        </div>
      ) : null}

      <CloudConnectionLinkForm
        disabled={disabled}
        provider={provider}
        simple
        idPrefix="wizard-cloud-connect"
        linkedConnection={selectedConnection}
        onLinked={handleLinked}
        onDisconnected={() => {
          setPendingConnection(null)
          onConnectionIdChange('')
        }}
      />

      {externalHost.trim() ? (
        <div
          className={cn(
            'space-y-2 rounded-md border px-3 py-2',
            pendingCertificate
              ? 'border-amber-300 bg-amber-50'
              : 'border-green-200 bg-green-50'
          )}
        >
          <p className={cn('text-xs', pendingCertificate ? 'text-amber-950' : 'text-green-900')}>
            Selected host:{' '}
            <span className="font-mono font-medium">{externalHost.trim()}</span>
            {externalSshUser.trim() ? (
              <>
                {' '}
                · SSH user <span className="font-mono">{externalSshUser.trim()}</span>
              </>
            ) : null}
          </p>
          {activeLaunchKey && !sshCertificateVerified ? (
            <div className="space-y-2">
              <p className="text-[11px] text-amber-950">
                Launch downloaded {bootstrapPemName} (first SSH as that user) and {operatorPemName} (daily SSH).
                Verify {operatorPemName} - not the bootstrap file.
              </p>
              <div className="flex flex-wrap items-center gap-2">
                {activeBootstrapKey ? (
                  <SshKeyDownloadButton
                    label={activeBootstrapKey.label}
                    pem={activeBootstrapKey.pem}
                    keyId={activeBootstrapKey.keyId}
                    disabled={disabled || verifying}
                    buttonLabel={`${bootstrapPemName} again`}
                  />
                ) : null}
                <SshKeyDownloadButton
                  label={activeLaunchKey.label}
                  pem={activeLaunchKey.pem}
                  keyId={activeLaunchKey.keyId}
                  disabled={disabled || verifying}
                  buttonLabel={`${operatorPemName} again`}
                />
                <input
                  ref={verifyInputRef}
                  type="file"
                  accept=".pem,.key,text/plain,application/x-pem-file"
                  className="sr-only"
                  onChange={(event) => {
                    const file = event.target.files?.[0]
                    if (file) {
                      void handleVerifyFile(file)
                    }
                  }}
                />
                <button
                  type="button"
                  disabled={disabled || verifying}
                  onClick={() => verifyInputRef.current?.click()}
                  className="inline-flex items-center gap-1.5 rounded-md bg-amber-800 px-2.5 py-1.5 text-xs font-semibold text-white hover:bg-amber-900 disabled:opacity-60"
                >
                  <ShieldCheck className="h-3.5 w-3.5" aria-hidden="true" />
                  {verifying ? 'Verifying…' : 'Verify certificate'}
                </button>
              </div>
              {verifyError ? <p className="text-[11px] text-red-700">{verifyError}</p> : null}
            </div>
          ) : didVerifyLaunchKey ? (
            <p className="inline-flex items-center gap-1 text-[11px] font-medium text-green-800">
              <CheckCircle2 className="h-3.5 w-3.5" aria-hidden="true" />
              SSH certificate verified. The cached copy was removed.
            </p>
          ) : null}
        </div>
      ) : null}

      {selectedConnection ? (
        <button
          type="button"
          disabled={disabled}
          onClick={() => setSetupOpen(true)}
          className={cn(
            'inline-flex items-center gap-1.5 rounded-md px-3 py-1.5 text-xs font-semibold disabled:opacity-60',
            externalHost.trim()
              ? 'border border-gray-300 bg-white text-gray-800 hover:bg-gray-50'
              : 'bg-blue-600 text-white hover:bg-blue-700'
          )}
        >
          {externalHost.trim() ? (
            <>
              <Repeat className="h-3.5 w-3.5" aria-hidden="true" />
              Select different instance
            </>
          ) : (
            <>
              <Settings2 className="h-3.5 w-3.5" aria-hidden="true" />
              Set up instance
            </>
          )}
        </button>
      ) : null}

      <CloudInstanceSetupDialog
        open={setupOpen}
        onClose={() => setSetupOpen(false)}
        connection={selectedConnection}
        disabled={disabled}
        remoteOs={remoteOs}
        sshUser={externalSshUser}
        savedSshKeyId={savedSshKeyId}
        onSelectInstance={(instance) => {
          setLaunchedKey(null)
          setBootstrapKey(null)
          setVerifyError(null)
          setDidVerifyLaunchKey(false)
          onSshCertificateVerifiedChange(true)
          onSelectInstance(instance)
        }}
        onLaunched={(result) => {
          const operatorUser = result.instance.suggestedSshUser?.trim() || DEFAULT_OPERATOR_SSH_USER
          const bootstrapUser = result.bootstrapSshUser?.trim() || imageDefaultSshUser(provider, remoteOs)
          if (result.privateKeyPem || result.savedSshKeyId) {
            setLaunchedKey({
              pem: result.privateKeyPem ?? null,
              keyId: result.savedSshKeyId ?? '',
              label: operatorUser,
            })
            onSshCertificateVerifiedChange(false)
          } else {
            setLaunchedKey(null)
            onSshCertificateVerifiedChange(true)
          }
          if (result.bootstrapPrivateKeyPem || result.bootstrapSshKeyId) {
            setBootstrapKey({
              pem: result.bootstrapPrivateKeyPem ?? null,
              keyId: result.bootstrapSshKeyId ?? '',
              label: bootstrapUser,
            })
          } else {
            setBootstrapKey(null)
          }
          setDidVerifyLaunchKey(false)
          setVerifyError(null)
          onLaunched(result)
        }}
      />
    </div>
  )
}
