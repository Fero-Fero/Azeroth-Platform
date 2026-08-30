import { RemoteHostOs, type RemoteConnectionTestResultDto } from '@/types/stack.types'

export function osLabel(os: RemoteHostOs | string | undefined): string {
  return os === RemoteHostOs.Windows ? 'Windows' : 'Linux'
}

/** Host OS reported by SSH that does not match the wizard setting. */
export function osMismatchDetected(
  result: RemoteConnectionTestResultDto | null | undefined,
  selectedOs: RemoteHostOs | string | undefined
): RemoteHostOs | null {
  const detected = result?.detectedOs
  if (detected === RemoteHostOs.Windows) {
    return RemoteHostOs.Windows
  }
  if (!detected || !selectedOs || detected === selectedOs) {
    return null
  }

  return detected
}

interface OsMismatchNoticeProps {
  detectedOs: RemoteHostOs
  selectedOs: RemoteHostOs | string | undefined
  onSwitchOs: (os: RemoteHostOs) => void
}

export function OsMismatchNotice({ detectedOs, selectedOs, onSwitchOs }: OsMismatchNoticeProps) {
  if (detectedOs === RemoteHostOs.Windows) {
    return (
      <div className="mt-2 rounded-md border border-amber-300 bg-amber-50 px-3 py-2 text-xs text-amber-950">
        <p className="font-medium">This host is Windows Server.</p>
        <p className="mt-1 text-[11px] text-amber-900">
          Azeroth Platform only supports Ubuntu or Debian VPC hosts. Launch or connect a Linux VM.
        </p>
      </div>
    )
  }

  const selectedLabel = osLabel(selectedOs)
  const detectedLabel = osLabel(detectedOs)

  return (
    <div className="mt-2 rounded-md border border-amber-300 bg-amber-50 px-3 py-2 text-xs text-amber-950">
      <p className="font-medium">This host is {detectedLabel}, but the wizard is set to {selectedLabel}.</p>
      <p className="mt-1 text-[11px] text-amber-900">
        Switch the operating system setting to {detectedLabel} before continuing. Verify VPC stays blocked until
        the setting matches the host.
      </p>
      <button
        type="button"
        onClick={() => onSwitchOs(detectedOs)}
        className="mt-2 inline-flex items-center rounded-md bg-amber-800 px-2.5 py-1 text-[11px] font-semibold text-white hover:bg-amber-900 focus:outline-none focus:ring-2 focus:ring-amber-500"
      >
        Switch to {detectedLabel}
      </button>
    </div>
  )
}
