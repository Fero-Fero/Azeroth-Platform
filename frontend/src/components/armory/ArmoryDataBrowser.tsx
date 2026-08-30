import { useMutation } from '@tanstack/react-query'
import FileBrowser from '@/components/common/FileBrowser'
import { useArmoryDataBrowse, useDeleteArmoryData } from '@/hooks/useArmoryAssets'

function useDisabledArmoryDataUpload() {
  return useMutation({
    mutationFn: async (_input: { dir: string; file: File }) => {
      throw new Error('Uploads are not supported from the dataset browser. Use the zip upload above.')
    },
  })
}

/** Read-only view of the stack's armory-assets Docker volume (model-viewer dataset). */
export default function ArmoryDataBrowser({ stackId }: { stackId: string }) {
  return (
    <FileBrowser
      stackId={stackId}
      title="Browse dataset files"
      rootLabel="data"
      useBrowse={useArmoryDataBrowse}
      useDelete={useDeleteArmoryData}
      useUpload={useDisabledArmoryDataUpload}
      allowUpload={false}
      readOnly
    />
  )
}
