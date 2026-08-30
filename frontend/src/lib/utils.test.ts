import { describe, expect, it } from 'vitest'
import { apiErrorMessage } from '@/lib/utils'

describe('apiErrorMessage', () => {
  it('formats platform ValidationErrorDto arrays instead of returning objects', () => {
    const message = apiErrorMessage({
      response: {
        data: {
          isValid: false,
          errors: [
            { field: 'moduleIds', message: "Unknown module 'mod-gone'." },
            { field: 'moduleIds', message: 'Ollama Bot Buddy is required for the Express server type.' },
          ],
        },
      },
      message: 'Request failed with status code 400',
    })

    expect(message).toBe(
      "Unknown module 'mod-gone'. Ollama Bot Buddy is required for the Express server type.",
    )
    expect(typeof message).toBe('string')
  })

  it('reads ASP.NET ModelState dictionaries', () => {
    expect(
      apiErrorMessage({
        response: { data: { errors: { stackName: ['A stack with this name already exists.'] } } },
      }),
    ).toBe('A stack with this name already exists.')
  })

  it('prefers a string message field over errors', () => {
    expect(
      apiErrorMessage({
        response: { data: { message: 'Ports conflict.', errors: [{ field: 'ports', message: 'ignored' }] } },
      }),
    ).toBe('Ports conflict.')
  })
})
