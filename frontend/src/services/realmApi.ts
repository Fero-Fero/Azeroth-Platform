import apiClient from './api'
import type { RealmDto, CreateRealmRequest, UpdateRealmRequest } from '@/types/realm.types'

export const realmApi = {
  // List all realms for a stack
  list: (stackId: string) =>
    apiClient.get<RealmDto[]>(`/stacks/${stackId}/realms`),

  // Create a new realm
  create: (stackId: string, request: CreateRealmRequest) =>
    apiClient.post<RealmDto>(`/stacks/${stackId}/realms`, request),

  // Update a realm's editable properties
  update: (stackId: string, realmId: number, request: UpdateRealmRequest) =>
    apiClient.put<RealmDto>(`/stacks/${stackId}/realms/${realmId}`, request),

  // Set the address (host/IP) clients are redirected to for this stack's realms
  setAddress: (stackId: string, host: string) =>
    apiClient.put<RealmDto[]>(`/stacks/${stackId}/realms/address`, { host }),
}
