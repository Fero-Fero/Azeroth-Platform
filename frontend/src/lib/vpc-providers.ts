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
