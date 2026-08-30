import { RemoteHostOs } from '@/types/stack.types'

export const DEFAULT_OPERATOR_SSH_USER = 'azp-admin'

const LINUX_USER = /^[a-z_][a-z0-9_-]{0,31}$/

const FORBIDDEN_SSH_USERS = new Set(['root', 'nobody', 'daemon', 'bin', 'sys', 'sync', 'sshd', 'www-data', 'messagebus'])

const IMAGE_DEFAULT_SSH_USERS = new Set([
  'ubuntu',
  'debian',
  'azureuser',
  'ec2-user',
  'admin',
  'centos',
  'fedora',
])

export function isForbiddenSshUser(user: string, _remoteOs?: RemoteHostOs): boolean {
  const value = user.trim().toLowerCase()
  return value.length === 0 || FORBIDDEN_SSH_USERS.has(value) || value.startsWith('systemd-')
}

export function isImageDefaultSshUser(user: string, _remoteOs?: RemoteHostOs): boolean {
  const value = user.trim().toLowerCase()
  return IMAGE_DEFAULT_SSH_USERS.has(value)
}

/** Image-default login for the bootstrap key / .pem filename (ubuntu.pem or root.pem). */
export function imageDefaultSshUser(provider?: string, _remoteOs?: RemoteHostOs): string {
  const value = (provider ?? '').trim().toLowerCase()
  if (value === 'digitalocean' || value === 'hetzner' || value === 'vultr') {
    return 'root'
  }

  return 'ubuntu'
}

export function isValidLinuxSshUser(user: string): boolean {
  const value = user.trim().toLowerCase()
  return LINUX_USER.test(value) && !isForbiddenSshUser(value)
}

export function sshUserWarning(user: string, _remoteOs?: RemoteHostOs): string | null {
  const value = user.trim()
  if (!value) {
    return null
  }
  const lowered = value.toLowerCase()
  if (isForbiddenSshUser(lowered)) {
    return `Do not use ${lowered}. Daily SSH must be a dedicated operator user such as ${DEFAULT_OPERATOR_SSH_USER}.`
  }
  if (isImageDefaultSshUser(lowered)) {
    return `${lowered} is the image default. Use it only until you create ${DEFAULT_OPERATOR_SSH_USER}, then Finalize SSH hardening.`
  }
  return null
}
