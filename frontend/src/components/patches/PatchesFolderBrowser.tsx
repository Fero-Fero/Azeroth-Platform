import { useMutation } from '@tanstack/react-query'
import FileBrowser from '@/components/common/FileBrowser'
import { useDeletePatchEntry, usePatchFilesBrowse } from '@/hooks/usePatches'

function useDisabledPatchUpload() {
  return useMutation({
    mutationFn: async (_input: { dir: string; file: File }) => {
      throw new Error('Uploads are not supported from the patches folder browser.')
    },
  })
}

export default function PatchesFolderBrowser({ stackId }: { stackId: string }) {
  return (
    <FileBrowser
      stackId={stackId}
      title="Browse patches folder"
      rootLabel="patches"
      useBrowse={usePatchFilesBrowse}
      useDelete={useDeletePatchEntry}
      useUpload={useDisabledPatchUpload}
      allowUpload={false}
    />
  )
}
