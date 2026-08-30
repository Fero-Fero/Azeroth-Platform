/** Returns true when the CIDR is a usable public IPv4 for cloud SG SSH rules. */
export function isUsablePublicAdminSourceCidr(cidr?: string): boolean {
  if (!cidr?.trim()) {
    return false
  }

  const ip = cidr.split('/')[0]?.trim()
  if (!ip) {
    return false
  }

  const parts = ip.split('.').map((part) => Number(part))
  if (parts.length !== 4 || parts.some((part) => Number.isNaN(part) || part < 0 || part > 255)) {
    return false
  }

  const [a, b] = parts
  if (a === 10 || a === 127 || (a === 192 && b === 168) || (a === 169 && b === 254)) {
    return false
  }

  // RFC1918 + typical Docker bridge (172.17.x, 172.18.x, …)
  if (a === 172 && b >= 16 && b <= 31) {
    return false
  }

  return true
}

/** Looks up the browser's public IPv4 (what AWS/GCP need for SSH source rules). */
export async function fetchPublicIpv4(): Promise<string | undefined> {
  const jsonEndpoints = [
    'https://api.ipify.org?format=json',
    'https://api64.ipify.org?format=json',
  ]

  for (const url of jsonEndpoints) {
    try {
      const response = await fetch(url)
      if (!response.ok) {
        continue
      }

      const payload = (await response.json()) as { ip?: string }
      const ip = payload.ip?.trim()
      if (ip && isUsablePublicAdminSourceCidr(`${ip}/32`)) {
        return ip
      }
    } catch {
      // try next endpoint
    }
  }

  try {
    const response = await fetch('https://ifconfig.me/ip')
    if (response.ok) {
      const ip = (await response.text()).trim()
      if (isUsablePublicAdminSourceCidr(`${ip}/32`)) {
        return ip
      }
    }
  } catch {
    // no public IP available
  }

  return undefined
}

export async function resolvePublicAdminSourceCidr(apiCidr?: string): Promise<string | undefined> {
  const publicIp = await fetchPublicIpv4()
  if (publicIp) {
    return `${publicIp}/32`
  }

  if (isUsablePublicAdminSourceCidr(apiCidr)) {
    return apiCidr
  }

  return undefined
}
