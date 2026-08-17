import { useServerTypes } from '@/hooks/useModules'
import { assertServerTypeRegistry } from '@/server-types/registry/registry'

/** Throws when the API catalog lists a server type with no frontend definition. */
export function useAssertServerTypeRegistry() {
  const { data } = useServerTypes()
  if (data) {
    assertServerTypeRegistry(data.map((type) => type.id))
  }
}
