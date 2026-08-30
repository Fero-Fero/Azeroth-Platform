import { Link } from 'react-router-dom'

const FEATURE_GROUPS = [
  {
    title: 'Choose a server',
    items: [
      {
        name: 'Standard',
        detail: 'Official AzerothCore. A classic realm with no bot framework baked in.',
      },
      {
        name: 'Playerbots',
        detail: 'Fill the world with AI players that quest, dungeon, and raid.',
      },
      {
        name: 'NPC Bots',
        detail: 'Hire companion bots that follow you as extra party members.',
      },
      {
        name: 'Individual Progression',
        detail: 'Play through Classic, TBC, then WotLK on one character, at your own pace.',
      },
      {
        name: 'Express Setup',
        detail: 'Local one-click realm. After the first build, click Setup and Launch on Overview.',
      },
      {
        name: 'Custom fork',
        detail: 'Point the wizard at any AzerothCore git repo and branch you already use.',
      },
    ],
  },
  {
    title: 'Deploy and run',
    items: [
      {
        name: 'This computer',
        detail: 'Runs in Docker on the machine where you opened the manager.',
      },
      {
        name: 'Cloud or VPS',
        detail: 'Create a remote host (DigitalOcean, Hetzner, and others), then build there.',
      },
      {
        name: 'Start, stop, rebuild',
        detail: 'Watch clone and compile progress, then start or rebuild whenever you need.',
      },
      {
        name: 'Optional armory',
        detail: 'Turn the website off if you only want the game server and launcher.',
      },
    ],
  },
  {
    title: 'Players and the client',
    items: [
      {
        name: 'Game client',
        detail: 'Upload or download a 3.3.5a client so players do not hunt for files.',
      },
      {
        name: 'Launcher',
        detail: 'One installer that updates the client, sets realmlist, and starts the game.',
      },
      {
        name: 'Addons',
        detail: 'Push curated addons (quest helpers, DBM, and more) to every player automatically.',
      },
      {
        name: 'Accounts and characters',
        detail: 'Create GM accounts and browse characters without opening the database.',
      },
    ],
  },
  {
    title: 'World and content',
    items: [
      {
        name: 'Modules',
        detail: 'Pick catalog modules (AH bot, dungeon bots, Ollama, and others) or add your own repo.',
      },
      {
        name: 'Patches',
        detail: 'Apply SQL, DBC, maps, and MPQ packs in order. Reapply after a core update.',
      },
      {
        name: 'Server Wide Progression',
        detail: 'Unlock expansions for the whole realm as you apply the next patch.',
      },
      {
        name: 'Config and Lua',
        detail: 'Edit worldserver / module conf files and drop Lua scripts from the same screen.',
      },
    ],
  },
  {
    title: 'Website and news',
    items: [
      {
        name: 'Armory',
        detail: 'Players register, browse characters (with a 3D model), guilds, and a world map.',
      },
      {
        name: 'Look and layout',
        detail: 'Change colors, logo, and page widgets without editing the armory code.',
      },
      {
        name: 'News',
        detail: 'Write patch notes that show in the launcher and on the armory.',
      },
      {
        name: 'Email signup',
        detail: 'Optional: require a verification email before a new account can log in.',
      },
    ],
  },
  {
    title: 'Keep it healthy',
    items: [
      {
        name: 'Docker tools',
        detail: 'See disk use, prune unused images, and read container logs.',
      },
      {
        name: 'Revisions',
        detail: 'History of what you changed so you can see when a module or patch landed.',
      },
      {
        name: 'Updates',
        detail: 'Check the core and modules for new commits, then rebuild when you are ready.',
      },
    ],
  },
] as const

export default function HomePage() {
  return (
    <div className="mx-auto max-w-4xl">
      <h1 className="mb-4 text-center text-4xl font-bold">Welcome to Azeroth Platform</h1>
      <p className="mb-8 text-center text-xl text-gray-600">
        Easily manage your AzerothCore server stacks with Docker
      </p>
      
      <div className="grid grid-cols-1 md:grid-cols-2 gap-6 mt-8">
        <div className="bg-white p-6 rounded-lg shadow">
          <h2 className="text-2xl font-semibold mb-3">Manage Stacks</h2>
          <p className="text-gray-600 mb-4">
            View and control your existing AzerothCore server stacks
          </p>
          <Link 
            to="/stacks" 
            className="inline-block px-4 py-2 bg-blue-600 text-white rounded-md hover:bg-blue-700 transition"
          >
            View Stacks
          </Link>
        </div>
        
        <div className="bg-white p-6 rounded-lg shadow">
          <h2 className="text-2xl font-semibold mb-3">Create New Stack</h2>
          <p className="text-gray-600 mb-4">
            Set up a new AzerothCore server with custom modules
          </p>
          <Link 
            to="/stacks/new" 
            className="inline-block px-4 py-2 bg-green-600 text-white rounded-md hover:bg-green-700 transition"
          >
            Create Stack
          </Link>
        </div>
      </div>
      
      <div className="mt-12 rounded-lg border border-blue-200 bg-blue-50 p-6">
        <h3 className="mb-1 text-lg font-semibold text-blue-900">Features</h3>
        <p className="mb-6 text-sm text-blue-800">
          Everything you need to run a private WotLK server from this manager.
        </p>
        <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
          {FEATURE_GROUPS.map((group) => (
            <section key={group.title}>
              <h4 className="mb-2 text-sm font-semibold uppercase tracking-wide text-blue-900">
                {group.title}
              </h4>
              <ul className="space-y-2 text-sm text-blue-800">
                {group.items.map((item) => (
                  <li key={item.name}>
                    <span className="font-medium text-blue-950">{item.name}.</span>{' '}
                    {item.detail}
                  </li>
                ))}
              </ul>
            </section>
          ))}
        </div>
      </div>
    </div>
  )
}
