import FileBrowser from '@/components/common/FileBrowser'
import { useArmoryDataBrowse, useDeleteArmoryData, useUploadArmoryDataFile } from '@/hooks/useArmoryAssets'

export default function ArmoryDataBrowser({ stackId }: { stackId: string }) {
  return (
    <FileBrowser
      stackId={stackId}
      title="Browse dataset files"
      rootLabel="data"
      useBrowse={useArmoryDataBrowse}
      useDelete={useDeleteArmoryData}
      useUpload={useUploadArmoryDataFile}
    />
  )
}
