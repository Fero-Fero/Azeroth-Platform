import apiClient from '@/services/api'
import type { PatchNewsPreviewDto } from '@/types/patch.types'

const API_IMG_SRC = /src="(\/api\/[^"]+)"/g

/** Fetch an authenticated manager API path and return an object URL for use in &lt;img src&gt;. */
export async function fetchAuthenticatedImageBlobUrl(apiPath: string): Promise<string> {
  const path = apiPath.startsWith('/api') ? apiPath.slice(4) : apiPath
  const res = await apiClient.get(path, { responseType: 'blob' })
  return URL.createObjectURL(res.data)
}

/** Resolve cover + inline news-asset URLs to blob URLs (img tags cannot send bearer auth). */
export async function loadPatchNewsPreviewMedia(preview: PatchNewsPreviewDto): Promise<{
  coverUrl: string | null
  html: string
  revoke: () => void
}> {
  const blobUrls: string[] = []
  const revoke = () => {
    for (const url of blobUrls) {
      URL.revokeObjectURL(url)
    }
  }

  let coverUrl: string | null = null
  if (preview.hasCover && preview.coverUrl) {
    coverUrl = await fetchAuthenticatedImageBlobUrl(preview.coverUrl.split('?')[0]!)
    blobUrls.push(coverUrl)
  }

  let html = preview.html ?? ''
  const imageUrls = [...new Set([...html.matchAll(API_IMG_SRC)].map((match) => match[1]!))]
  for (const url of imageUrls) {
    if (!url.includes('/news-asset/')) {
      continue
    }

    const blobUrl = await fetchAuthenticatedImageBlobUrl(url.split('?')[0]!)
    blobUrls.push(blobUrl)
    html = html.replaceAll(`src="${url}"`, `src="${blobUrl}"`)
  }

  return { coverUrl, html, revoke }
}
