import { Link } from 'react-router-dom'

export default function HomePage() {
  return (
    <div className="max-w-4xl">
      <h1 className="text-4xl font-bold mb-4">Welcome to Azeroth Platform</h1>
      <p className="text-xl text-gray-600 mb-8">
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
      
      <div className="mt-12 bg-blue-50 border border-blue-200 rounded-lg p-6">
        <h3 className="text-lg font-semibold text-blue-900 mb-2">Features</h3>
        <ul className="list-disc list-inside text-blue-800 space-y-1">
          <li>Standard and Playerbots server types</li>
          <li>Modular architecture with custom module support</li>
          <li>Real-time build progress tracking</li>
          <li>Docker-based deployment</li>
          <li>Easy stack management and control</li>
        </ul>
      </div>
    </div>
  )
}
