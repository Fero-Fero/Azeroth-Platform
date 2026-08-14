import { Cloud, HardDrive, Loader2 } from 'lucide-react'
import { DeploymentTarget } from '@/types/stack.types'

export default function StackConnectionBanner({
  deploymentTarget,
  needsReconnect,
}: {
  deploymentTarget?: DeploymentTarget
  needsReconnect?: boolean
}) {
  const isExternal = deploymentTarget === DeploymentTarget.External

  if (needsReconnect) {
    return (
      <div className="mb-4 flex items-start gap-3 rounded-lg border border-amber-300 bg-amber-50 px-4 py-3 text-sm text-amber-950">
        <Cloud className="mt-0.5 h-4 w-4 shrink-0" />
        <div>
          <strong>VPC connection required.</strong> SSH credentials are missing or expired — reconnect from
          the VPC overview tab once the page finishes loading.
        </div>
      </div>
    )
  }

  return (
    <div className="mb-4 flex items-start gap-3 rounded-lg border border-blue-200 bg-blue-50 px-4 py-3 text-sm text-blue-900">
      {isExternal ? (
        <Cloud className="mt-0.5 h-4 w-4 shrink-0 text-blue-600" aria-hidden="true" />
      ) : (
        <HardDrive className="mt-0.5 h-4 w-4 shrink-0 text-blue-600" aria-hidden="true" />
      )}
      <div className="flex min-w-0 flex-1 items-start gap-2">
        <Loader2 className="mt-0.5 h-4 w-4 shrink-0 animate-spin" />
        <span>
          {isExternal
            ? 'Connecting to the remote VPC Docker engine over SSH and refreshing live container status…'
            : 'Refreshing live container status from the local Docker engine…'}
        </span>
      </div>
    </div>
  )
}
