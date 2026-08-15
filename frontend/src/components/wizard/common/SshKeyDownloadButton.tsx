import { useState } from 'react'
import { Download, Loader2 } from 'lucide-react'
import { cloudApi } from '@/services/api'
import { downloadPemFile, pemDownloadFilename } from '@/lib/ssh-key-download'
import { cn } from '@/lib/utils'

interface SshKeyDownloadButtonProps {
  label: string
  pem?: string | null
  keyId?: string
  disabled?: boolean
  className?: string
}

export function SshKeyDownloadButton({
  label,
  pem,
  keyId,
  disabled = false,
  className,
}: SshKeyDownloadButtonProps) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const handleClick = async () => {
    setError(null)
    if (pem?.trim()) {
      downloadPemFile(label, pem)
      return
    }

    if (!keyId?.trim()) {
      setError('No SSH key is available to download.')
      return
    }

    setBusy(true)
    try {
      const exported = (await cloudApi.downloadSshKey(keyId)).data
      downloadPemFile(exported.label || label, exported.privateKey)
    } catch {
      setError('Could not download the SSH key.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <span className="inline-flex flex-col items-start gap-1">
      <button
        type="button"
        disabled={disabled || busy || (!pem?.trim() && !keyId?.trim())}
        onClick={() => void handleClick()}
        className={cn(
          'inline-flex items-center gap-1.5 rounded-md border border-amber-400 bg-white px-2.5 py-1.5 text-xs font-medium text-amber-950 hover:bg-amber-50 disabled:opacity-60',
          className
        )}
      >
        {busy ? (
          <Loader2 className="h-3.5 w-3.5 animate-spin" aria-hidden="true" />
        ) : (
          <Download className="h-3.5 w-3.5" aria-hidden="true" />
        )}
        Download {pemDownloadFilename(label)}
      </button>
      {error ? <span className="text-[11px] text-red-700">{error}</span> : null}
    </span>
  )
}
