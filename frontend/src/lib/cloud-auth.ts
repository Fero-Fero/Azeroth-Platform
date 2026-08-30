import type { CloudAuthMethod, CloudProvider, CloudProviderConnectionDto } from '@/types/stack.types'

export const CLOUD_OAUTH_MESSAGE_TYPE = 'azeroth-cloud-oauth'

export interface CloudOAuthMessage {
  type: typeof CLOUD_OAUTH_MESSAGE_TYPE
  status: 'success' | 'error'
  provider?: string
  connectionId?: string
  message?: string
}

export function isCloudOAuthMessage(value: unknown): value is CloudOAuthMessage {
  if (!value || typeof value !== 'object') {
    return false
  }

  const payload = value as Partial<CloudOAuthMessage>
  return payload.type === CLOUD_OAUTH_MESSAGE_TYPE
    && (payload.status === 'success' || payload.status === 'error')
}

export function openCloudOAuthPopup(authorizationUrl: string): Window | null {
  const width = 640
  const height = 760
  const left = window.screenX + Math.max(0, (window.outerWidth - width) / 2)
  const top = window.screenY + Math.max(0, (window.outerHeight - height) / 2)
  return window.open(
    authorizationUrl,
    'azeroth-cloud-oauth',
    `popup=yes,width=${width},height=${height},left=${left},top=${top}`
  )
}

export function authMethodLabel(method?: CloudAuthMethod): string {
  switch (method) {
    case 'OAuth':
      return 'oauth'
    case 'AssumedRole':
      return 'assumed role'
    default:
      return 'manual'
  }
}

export function formatTokenExpiry(expiresAtUtc?: string): string | null {
  if (!expiresAtUtc) {
    return null
  }

  const expires = new Date(expiresAtUtc)
  if (Number.isNaN(expires.getTime())) {
    return null
  }

  const ms = expires.getTime() - Date.now()
  if (ms <= 0) {
    return 'expired'
  }

  const days = Math.floor(ms / (24 * 60 * 60 * 1000))
  if (days >= 1) {
    return `expires in ${days} day${days === 1 ? '' : 's'}`
  }

  const hours = Math.max(1, Math.round(ms / (60 * 60 * 1000)))
  return `expires in ${hours} hour${hours === 1 ? '' : 's'}`
}

export function connectionStatusLine(connection: CloudProviderConnectionDto): string {
  const parts = [authMethodLabel(connection.authMethod)]
  if (connection.accountHint) {
    parts.unshift(connection.accountHint)
  }

  const expiry = formatTokenExpiry(connection.tokenExpiresAtUtc)
  if (expiry) {
    parts.push(expiry)
  }

  if (connection.needsReauth) {
    parts.push('needs reconnect')
  }

  return parts.join(' · ')
}

export function providerDisplayName(provider: CloudProvider): string {
  switch (provider) {
    case 'DigitalOcean':
      return 'DigitalOcean'
    case 'Aws':
      return 'AWS'
    case 'Gcp':
      return 'Google Cloud'
    case 'Azure':
      return 'Azure'
    case 'Hetzner':
      return 'Hetzner'
    case 'Vultr':
      return 'Vultr'
    default:
      return provider
  }
}
