import { describe, expect, it } from 'vitest'
import { isDbImportInProgress, isDatabaseRunning } from '@/setup/stackServices'
import { StackStatus, type StackDetailsDto, type StackJobStatus } from '@/types/stack.types'

function stack(overrides: Partial<StackDetailsDto> = {}): StackDetailsDto {
  return {
    stackId: 's1',
    status: StackStatus.Stopped,
    services: [],
    ...overrides,
  } as StackDetailsDto
}

describe('isDatabaseRunning', () => {
  it('is true when ac-database is running', () => {
    expect(
      isDatabaseRunning(stack({ services: [{ service: 'ac-database', state: 'running' } as never] })),
    ).toBe(true)
  })
})

describe('isDbImportInProgress', () => {
  it('is true while the stack is initializing or db-import is running', () => {
    expect(isDbImportInProgress(stack({ status: StackStatus.Initializing }))).toBe(true)
    expect(
      isDbImportInProgress(
        stack({
          status: StackStatus.Starting,
          services: [{ service: 'ac-db-import', state: 'running' } as never],
        }),
      ),
    ).toBe(true)
  })

  it('is false after db-import has exited or the stack is running', () => {
    expect(
      isDbImportInProgress(
        stack({
          status: StackStatus.Starting,
          services: [{ service: 'ac-db-import', state: 'exited' } as never],
        }),
      ),
    ).toBe(false)
    expect(isDbImportInProgress(stack({ status: StackStatus.Running }))).toBe(false)
  })

  it('treats a start job still in the import phase as in progress', () => {
    const job = { isRunning: true, action: 'Start', message: 'Running first-time database and client-data setup…' } as StackJobStatus
    expect(isDbImportInProgress(stack({ status: StackStatus.Starting }), job)).toBe(true)
  })
})
