/** Strip scheme/port noise from a host, URL, or host:port string. */
function normalizeHost(value?: string | null): string {
  const raw = value?.trim()
  if (!raw) return ''
  try {
    const url = raw.includes('://') ? new URL(raw) : new URL(`http://${raw}`)
    return url.hostname || ''
  } catch {
    if (raw.startsWith('[')) {
      const end = raw.indexOf(']')
      return end > 0 ? raw.slice(1, end) : ''
    }
    return raw.split(':')[0] || ''
  }
}

/**
 * Hostname the operator's browser should use to open the armory, based on the stack's publish bind
 * address - not the game realmlist host (those diverge when armory is loopback-only).
 */
export function resolveArmoryBrowseHost(
  effectiveBindAddress: string,
  fallbackHost?: string | null,
): string {
  const bind = effectiveBindAddress.trim()

  if (bind === '127.0.0.1' || bind === '::1') {
    return 'localhost'
  }

  if (bind === '0.0.0.0' || !bind) {
    const fallback = normalizeHost(fallbackHost)
    if (fallback && fallback !== '127.0.0.1' && fallback !== 'localhost') {
      return fallback
    }
    if (typeof window !== 'undefined') {
      const browserHost = window.location.hostname.trim()
      if (browserHost && browserHost !== '127.0.0.1' && browserHost !== 'localhost') {
        return browserHost
      }
    }
    return 'localhost'
  }

  return bind
}

export function resolveArmoryBrowseUrl(
  effectiveBindAddress: string,
  armoryPort: number,
  fallbackHost?: string | null,
): string {
  const host = resolveArmoryBrowseHost(effectiveBindAddress, fallbackHost)
  return `http://${host}:${armoryPort}`
}
