import { describe, expect, it } from 'vitest'
import { STEP_IDS } from '@/setup/constants'
import { armoryDbcUploadApplies, clientUploadApplies, isArmoryDbcUploadComplete, isClientUploadComplete } from '@/setup/global-steps/uploadStatus'
import { createMockProgress, createMockStatus } from '@/setup/test/fixtures'

describe('uploadStatus', () => {
  it('applies client upload only when the container is running and data is missing', () => {
    expect(clientUploadApplies(createMockStatus({ client: { dataUploaded: false, containerRunning: false, loading: false } }))).toBe(false)
    expect(clientUploadApplies(createMockStatus({ client: { dataUploaded: false, containerRunning: true, loading: false } }))).toBe(true)
    expect(clientUploadApplies(createMockStatus({ client: { dataUploaded: true, containerRunning: true, loading: false } }))).toBe(false)
    expect(clientUploadApplies(createMockStatus({ client: { dataUploaded: false, containerRunning: true, loading: true } }))).toBe(false)
  })

  it('treats skipped client upload as complete', () => {
    const progress = createMockProgress()
    const status = createMockStatus({ progress, client: { dataUploaded: false, containerRunning: true, loading: false } })
    expect(isClientUploadComplete(status, progress)).toBe(false)
    progress.skip(STEP_IDS.uploadClient)
    expect(isClientUploadComplete(status, progress)).toBe(true)
  })

  it('applies armory upload only when the container is running and data is missing', () => {
    expect(armoryDbcUploadApplies(createMockStatus({ armory: { dbcUploaded: false, containerRunning: true, loading: false } }))).toBe(true)
    expect(armoryDbcUploadApplies(createMockStatus({ armory: { dbcUploaded: true, containerRunning: true, loading: false } }))).toBe(false)
  })

  it('does not apply armory upload when the stack excluded the armory', () => {
    const ctxApplies = (includeArmory: boolean) =>
      includeArmory !== false &&
      armoryDbcUploadApplies(createMockStatus({ armory: { dbcUploaded: false, containerRunning: true, loading: false } }))
    expect(ctxApplies(true)).toBe(true)
    expect(ctxApplies(false)).toBe(false)
  })

  it('treats skipped armory upload as complete', () => {
    const progress = createMockProgress()
    const status = createMockStatus({ progress, armory: { dbcUploaded: false, containerRunning: true, loading: false } })
    progress.skip(STEP_IDS.uploadArmoryDbc)
    expect(isArmoryDbcUploadComplete(status, progress)).toBe(true)
  })
})
