import { useEffect, useState } from 'react'
import type { PatchNewsPreviewDto } from '@/types/patch.types'
import { fetchAuthenticatedImageBlobUrl, loadPatchNewsPreviewMedia } from '@/lib/patchNewsMedia'

/** Loads patch news cover + inline images via authenticated API requests. */
export function usePatchNewsPreviewMedia(
  preview: PatchNewsPreviewDto | undefined,
  enabled: boolean
) {
  const [coverUrl, setCoverUrl] = useState<string | null>(null)
  const [html, setHtml] = useState<string | null>(null)
  const [loadingMedia, setLoadingMedia] = useState(false)

  useEffect(() => {
    if (!enabled || !preview?.available) {
      setCoverUrl(null)
      setHtml(null)
      setLoadingMedia(false)
      return
    }

    let revoke = () => {}
    let cancelled = false

    setLoadingMedia(true)
    void loadPatchNewsPreviewMedia(preview)
      .then((result) => {
        if (cancelled) {
          result.revoke()
          return
        }

        revoke = result.revoke
        setCoverUrl(result.coverUrl)
        setHtml(result.html)
        setLoadingMedia(false)
      })
      .catch(() => {
        if (!cancelled) {
          setCoverUrl(null)
          setHtml(preview.html ?? null)
          setLoadingMedia(false)
        }
      })

    return () => {
      cancelled = true
      revoke()
    }
  }, [enabled, preview?.available, preview?.html, preview?.hasCover, preview?.coverUrl])

  return {
    coverUrl,
    html: html ?? preview?.html ?? '',
    loadingMedia,
  }
}

/** Loads a single authenticated API image path as a blob URL. */
export function useAuthenticatedImageUrl(imageUrl: string | null | undefined, enabled = true) {
  const [blobUrl, setBlobUrl] = useState<string | null>(null)

  useEffect(() => {
    if (!enabled || !imageUrl) {
      setBlobUrl(null)
      return
    }

    let objectUrl: string | null = null
    let cancelled = false

    void fetchAuthenticatedImageBlobUrl(imageUrl.split('?')[0]!)
      .then((url) => {
        if (cancelled) {
          URL.revokeObjectURL(url)
          return
        }

        objectUrl = url
        setBlobUrl(url)
      })
      .catch(() => {
        if (!cancelled) {
          setBlobUrl(null)
        }
      })

    return () => {
      cancelled = true
      if (objectUrl) {
        URL.revokeObjectURL(objectUrl)
      }
    }
  }, [enabled, imageUrl])

  return blobUrl
}
