export function pemDownloadFilename(label: string, fallback = 'azeroth-launch'): string {
  const safe = (label || fallback)
    .trim()
    .toLowerCase()
    .replace(/[^\w.-]+/g, '-')
    .replace(/^-+|-+$/g, '')
  const base = safe.length > 0 ? safe : fallback
  return base.endsWith('.pem') ? base : `${base}.pem`
}

export function normalizePem(pem: string): string {
  return pem.replace(/^\uFEFF/, '').replace(/\r\n/g, '\n').trim()
}

export async function pemFingerprint(pem: string): Promise<string> {
  const bytes = new TextEncoder().encode(normalizePem(pem))
  const hash = await crypto.subtle.digest('SHA-256', bytes)
  return Array.from(new Uint8Array(hash), (value) => value.toString(16).padStart(2, '0'))
    .join('')
    .slice(0, 16)
}

export function downloadPemFile(filename: string, pem: string): void {
  const contents = pem.endsWith('\n') ? pem : `${pem}\n`
  const blob = new Blob([contents], { type: 'application/x-pem-file' })
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = pemDownloadFilename(filename)
  document.body.appendChild(anchor)
  anchor.click()
  document.body.removeChild(anchor)
  URL.revokeObjectURL(url)
}
