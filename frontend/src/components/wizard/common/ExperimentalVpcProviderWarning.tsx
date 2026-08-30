import { AlertTriangle } from 'lucide-react'
import { providerDisplayName } from '@/lib/cloud-auth'
import { isExperimentalVpcProvider } from '@/lib/vpc-providers'
import { CloudProvider } from '@/types/stack.types'

export function ExperimentalVpcProviderWarning({
  provider,
}: {
  provider?: CloudProvider | string | null
}) {
  if (!isExperimentalVpcProvider(provider)) {
    return null
  }

  const name = providerDisplayName(provider as CloudProvider)

  return (
    <div
      role="status"
      className="flex gap-2 rounded-md border border-amber-300 bg-amber-50 px-3 py-2 text-xs text-amber-950"
    >
      <AlertTriangle className="mt-0.5 h-4 w-4 shrink-0 text-amber-700" aria-hidden="true" />
      <p>
        <span className="font-semibold">Experimental and untested.</span> {name} VPC launch, firewall,
        and bootstrap are not fully validated. AWS is the supported provider.
      </p>
    </div>
  )
}
