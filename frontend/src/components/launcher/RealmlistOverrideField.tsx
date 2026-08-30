import RealmlistAddressField from '@/components/launcher/RealmlistAddressField'

/**
 * Stack IP address for a stack, shown in the Overview → Configuration section. Uses the same shared editor
 * (and therefore the exact same save logic) as the Server → Realms tab: it persists the override, applies
 * it to the live realmlist, and rescans the client so the new host propagates to players.
 */
export default function RealmlistOverrideField({ stackId }: { stackId: string }) {
  return (
    <div>
      <h3 className="font-medium text-gray-900 mb-2">Stack IP address</h3>
      <RealmlistAddressField stackId={stackId} />
    </div>
  )
}
