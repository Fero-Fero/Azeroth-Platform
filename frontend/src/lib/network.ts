export function isPrivateIPv4(host: string): boolean {
  const parts = host.trim().split('.').map((part) => Number(part))
  if (parts.length !== 4 || parts.some((part) => !Number.isInteger(part) || part < 0 || part > 255)) {
    return false
  }

  const [a, b] = parts
  return a === 10 || (a === 192 && b === 168) || (a === 172 && b >= 16 && b <= 31)
}

export function browserLanHost(): string {
  const host = window.location.hostname.trim()
  return isPrivateIPv4(host) ? host : ''
}

export async function detectBrowserLanIp(timeoutMs = 1000): Promise<string> {
  if (typeof RTCPeerConnection === 'undefined') {
    return ''
  }

  return new Promise((resolve) => {
    const pc = new RTCPeerConnection({ iceServers: [] })
    let settled = false

    const finish = (host = '') => {
      if (settled) {
        return
      }
      settled = true
      try {
        pc.close()
      } catch {
        // Ignore cleanup errors; detection is best-effort only.
      }
      resolve(host)
    }

    const timer = window.setTimeout(() => finish(''), timeoutMs)

    pc.onicecandidate = (event) => {
      const candidate = event.candidate?.candidate ?? ''
      const matches = candidate.match(/\b(?:\d{1,3}\.){3}\d{1,3}\b/g) ?? []
      const privateHost = matches.find(isPrivateIPv4)
      if (privateHost) {
        window.clearTimeout(timer)
        finish(privateHost)
      }
    }

    try {
      pc.createDataChannel('lan-ip-detection')
      pc.createOffer()
        .then((offer) => pc.setLocalDescription(offer))
        .catch(() => {
          window.clearTimeout(timer)
          finish('')
        })
    } catch {
      window.clearTimeout(timer)
      finish('')
    }
  })
}

type ManagerProbeTarget = { port: string; protocol: 'http' | 'https' }

function managerProbeTargets(): ManagerProbeTarget[] {
  const hostname = window.location.hostname.trim()
  const locPort = window.location.port

  // When the UI is on localhost the manager is usually on :8080 (direct) or :443 (Caddy TLS). HTTPS pages
  // cannot probe LAN IPs over HTTP (mixed content), so only include HTTP targets when the page is HTTP.
  if (hostname === 'localhost' || hostname === '127.0.0.1') {
    const targets: ManagerProbeTarget[] = []
    if (window.location.protocol === 'http:') {
      targets.push({ port: '8080', protocol: 'http' })
    }
    targets.push({ port: locPort || '443', protocol: 'https' })
    return targets
  }

  const port = locPort || (window.location.protocol === 'https:' ? '443' : '80')
  return [{ port, protocol: window.location.protocol === 'https:' ? 'https' : 'http' }]
}

async function probeManagerHost(
  host: string,
  port: string,
  protocol: 'http' | 'https',
  timeoutMs: number,
): Promise<string> {
  const controller = new AbortController()
  const timer = window.setTimeout(() => controller.abort(), timeoutMs)
  try {
    const res = await fetch(`${protocol}://${host}:${port}/api/system/lan-probe`, {
      cache: 'no-store',
      signal: controller.signal,
    })
    if (!res.ok) {
      return ''
    }
    const body = await res.json() as { app?: string; probe?: string; ok?: boolean }
    return body.app === 'azeroth-platform' && body.probe === 'lan-ip' && body.ok === true ? host : ''
  } catch {
    return ''
  } finally {
    window.clearTimeout(timer)
  }
}

async function probeChunk(
  candidates: string[],
  target: ManagerProbeTarget,
  timeoutMs: number,
): Promise<string> {
  const results = await Promise.all(
    candidates.map((host) => probeManagerHost(host, target.port, target.protocol, timeoutMs)),
  )
  return results.find(Boolean) || ''
}

export async function detectManagerLanHost(): Promise<string> {
  const ranges = ['192.168.1', '192.168.0', '10.0.0']
  const chunkSize = 32
  const targets = managerProbeTargets()

  for (const target of targets) {
    for (const range of ranges) {
      const candidates = Array.from({ length: 254 }, (_, i) => `${range}.${i + 1}`)
      for (let i = 0; i < candidates.length; i += chunkSize) {
        const found = await probeChunk(candidates.slice(i, i + chunkSize), target, 450)
        if (found) {
          return found
        }
      }
    }
  }

  return ''
}
