import { CloudProvider, RemoteHostOs } from '@/types/stack.types'

export function providerSupportsRemoteOs(
  _provider: CloudProvider,
  _remoteOs?: RemoteHostOs | string
): boolean {
  return true
}

export function filterProvidersForRemoteOs<T extends { id: CloudProvider }>(
  providers: readonly T[],
  _remoteOs?: RemoteHostOs | string
): T[] {
  return [...providers]
}

/** AWS is the supported VPC provider. All others are experimental. */
export function isExperimentalVpcProvider(provider?: CloudProvider | string | null): boolean {
  return Boolean(provider) && provider !== CloudProvider.Aws
}

