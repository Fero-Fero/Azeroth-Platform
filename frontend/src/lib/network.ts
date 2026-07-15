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

async function probeManagerHost(host: string, port: string, timeoutMs: number): Promise<string> {
  const controller = new AbortController()
  const timer = window.setTimeout(() => controller.abort(), timeoutMs)
  try {
    const res = await fetch(`http://${host}:${port}/api/system/lan-probe`, {
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

async function probeChunk(candidates: string[], port: string, timeoutMs: number): Promise<string> {
  const results = await Promise.all(candidates.map((host) => probeManagerHost(host, port, timeoutMs)))
  return results.find(Boolean) || ''
}

export async function detectManagerLanHost(): Promise<string> {
  const port = window.location.port || (window.location.protocol === 'https:' ? '443' : '80')
  const ranges = ['192.168.1', '192.168.0', '10.0.0']
  const chunkSize = 32

  for (const range of ranges) {
    const candidates = Array.from({ length: 254 }, (_, i) => `${range}.${i + 1}`)
    for (let i = 0; i < candidates.length; i += chunkSize) {
      const found = await probeChunk(candidates.slice(i, i + chunkSize), port, 450)
      if (found) {
        return found
      }
    }
  }

  return ''
}
