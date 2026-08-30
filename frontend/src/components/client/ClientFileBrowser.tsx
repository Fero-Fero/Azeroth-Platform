import FileBrowser from '@/components/common/FileBrowser'
import { useClientBrowse, useDeleteClientEntry, useUploadClientFile } from '@/hooks/useClient'

export default function ClientFileBrowser({ stackId }: { stackId: string }) {
  return (
    <FileBrowser
      stackId={stackId}
      title="Browse files (base + published patches)"
      rootLabel="game"
      useBrowse={useClientBrowse}
      useDelete={useDeleteClientEntry}
      useUpload={useUploadClientFile}
    />
  )
}
