import { useMemo } from 'react'
import { useMutation, useQueryClient } from '@tanstack/react-query'
import { PLAYERBOTS_ENABLED_KEY } from '@/setup/constants'
import { useServerConfig, useServerConfigs } from '@/hooks/useServerFiles'
import { getConfValue, setConfValue } from '@/lib/conf-file'
import { serverConfigApi } from '@/services/api'

export function usePlayerbotsConf(stackId: string) {
  const queryClient = useQueryClient()
  const configsQuery = useServerConfigs(stackId)
  const path = useMemo(
    () =>
      configsQuery.data?.files.find((file) =>
        file.path.replace(/\\/g, '/').toLowerCase().endsWith('modules/playerbots.conf'),
      )?.path ?? null,
    [configsQuery.data?.files],
  )

  const configQuery = useServerConfig(stackId, path)
  const enabledValue = configQuery.data?.content
    ? getConfValue(configQuery.data.content, PLAYERBOTS_ENABLED_KEY)
    : null

  const toggleMutation = useMutation({
    mutationFn: async (nextEnabled: boolean) => {
      if (!path) {
        throw new Error('playerbots.conf is not available yet. Finish the stack build first.')
      }
      const current =
        configQuery.data?.content ?? (await serverConfigApi.read(stackId, path)).data.content
      const nextContent = setConfValue(current, PLAYERBOTS_ENABLED_KEY, nextEnabled ? '1' : '0')
      await serverConfigApi.save(stackId, path, nextContent)
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['server-config', stackId] })
    },
  })

  return {
    path,
    enabled: enabledValue === null ? null : enabledValue === '1',
    isLoading: configsQuery.isLoading || (!!path && configQuery.isLoading),
    toggle: toggleMutation.mutate,
    toggleAsync: toggleMutation.mutateAsync,
    isToggling: toggleMutation.isPending,
    toggleError: toggleMutation.error,
  }
}
