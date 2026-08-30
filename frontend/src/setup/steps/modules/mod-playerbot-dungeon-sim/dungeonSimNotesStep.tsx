import { MODULE_IDS, STEP_IDS } from '@/setup/constants'
import { hasModule } from '@/setup/stackServices'
import type { SetupStep } from '@/setup/types'
import { setupActionButton } from '@/setup/ui'

function DungeonSimDetails() {
  return (
    <ul className="list-disc space-y-1 pl-5 text-sm">
      <li>
        Autonomous bot runs need{' '}
        <code className="rounded bg-white px-1 text-xs">DungeonClear.AllowAutonomousBotRuns = 1</code> in
        mod-dungeon-clear config. The platform checks out TopHatMan&apos;s{' '}
        <code className="rounded bg-white px-1 text-xs">auto-playerbots</code> branch of Dungeon Clear so
        this module can call <code className="rounded bg-white px-1 text-xs">StartAutonomousClear</code>.
      </li>
      <li>
        Apply the module SQL (<code className="rounded bg-white px-1 text-xs">playerbot_dungeon_progression</code>
        ) to the characters database after install.
      </li>
      <li>
        Install the <strong>Dungeon Clear</strong> client addon from the Addons tab for in-game control.
      </li>
    </ul>
  )
}

export function dungeonSimNotesStep(): SetupStep {
  return {
    id: STEP_IDS.dungeonSim,
    moduleId: MODULE_IDS.dungeonSim,
    level: 'warning',
    title: 'Playerbot Dungeon Sim - setup notes',
    applies: (ctx) =>
      hasModule(ctx.stack, MODULE_IDS.dungeonSim) && !ctx.status.progress.isDismissed(STEP_IDS.dungeonSim),
    isComplete: (ctx) => ctx.status.progress.isDismissed(STEP_IDS.dungeonSim),
    summary: () => 'Configure mod-dungeon-clear, apply SQL, and install the client addon.',
    Component: () => <DungeonSimDetails />,
    Action: (ctx) => (
      <div className="flex flex-wrap items-center gap-2">
        <button
          type="button"
          onClick={() => ctx.status.progress.dismiss(STEP_IDS.dungeonSim)}
          className="rounded-md border border-gray-300 bg-white px-3 py-1.5 text-sm font-medium text-gray-700 hover:bg-gray-50"
        >
          Dismiss
        </button>
        {setupActionButton('Open addons', () => ctx.onSelectTab('addons'))}
      </div>
    ),
  }
}
