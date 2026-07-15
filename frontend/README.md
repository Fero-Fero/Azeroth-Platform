# Azeroth Platform - Frontend

Modern React-based frontend for the Azeroth Platform application, built with Vite, TypeScript, and TailwindCSS.

## Technology Stack

- **React 19** - UI library
- **TypeScript** - Type safety
- **Vite** - Build tool and dev server
- **TailwindCSS v4** - Utility-first CSS framework
- **React Router** - Client-side routing
- **React Query (TanStack Query)** - Server state management
- **Axios** - HTTP client
- **SignalR** - Real-time communication
- **React Hook Form** - Form handling
- **Zod** - Schema validation
- **Lucide React** - Icon library

## Project Structure

```
frontend/
├── src/
│   ├── components/       # Reusable UI components
│   ├── pages/           # Page components
│   ├── services/        # API client, SignalR
│   │   └── api.ts       # Axios instance and API endpoints
│   ├── hooks/           # Custom React hooks
│   ├── types/           # TypeScript type definitions
│   │   └── stack.types.ts  # Backend DTO types
│   ├── lib/             # Utilities
│   │   ├── utils.ts     # Utility functions (cn helper)
│   │   └── queryClient.ts  # React Query configuration
│   ├── App.tsx          # Root component with routing
│   ├── main.tsx         # Application entry point
│   └── index.css        # Global styles with Tailwind
├── public/              # Static assets
├── package.json         # Dependencies and scripts
├── tsconfig.json        # TypeScript configuration
├── tsconfig.app.json    # App-specific TypeScript config
├── vite.config.ts       # Vite configuration
├── tailwind.config.js   # TailwindCSS configuration
└── postcss.config.js    # PostCSS configuration
```

## Getting Started

### Prerequisites

- Node.js 20.x or higher (LTS recommended)
- npm 11.x or higher

### Installation

```bash
cd frontend
npm install
```

### Development

Start the development server:

```bash
npm run dev
```

The application will be available at [http://localhost:5173](http://localhost:5173)

### Building for Production

```bash
npm run build
```

The production build will be output to the `dist/` directory.

### Preview Production Build

```bash
npm run preview
```

### Linting

```bash
npm run lint
```

## Configuration

### Environment Variables

Create a `.env.local` file in the frontend directory for local environment variables:

```env
VITE_API_URL=http://localhost:5000
```

### API Proxy

The Vite dev server is configured to proxy API requests to the backend:

- `/api/*` → `http://localhost:5000/api/*`

This is configured in `vite.config.ts` and eliminates CORS issues during development.

### Path Aliases

TypeScript path aliases are configured for cleaner imports:

```typescript
import { cn } from '@/lib/utils'
import { StackDetailsDto } from '@/types/stack.types'
```

The `@/` alias maps to the `src/` directory.

## Key Features

### Type Safety

All backend DTOs are mirrored in `src/types/stack.types.ts`, ensuring type safety across the entire application.

### Server State Management

React Query is configured with sensible defaults:
- 5-minute stale time for queries
- Automatic retry on failure (1 attempt)
- Optimistic updates support

### Styling

TailwindCSS v4 with custom CSS variables for theming:
- Light and dark mode support
- Consistent design tokens
- shadcn/ui compatible color scheme

### Real-time Updates

SignalR client configured for build progress streaming and container status updates.

## Available Scripts

| Script | Description |
|--------|-------------|
| `npm run dev` | Start development server on port 5173 |
| `npm run build` | Build for production |
| `npm run preview` | Preview production build locally |
| `npm run lint` | Run ESLint |

## Development Guidelines

### Component Structure

- Use functional components with hooks
- Keep components small and focused
- Extract reusable logic into custom hooks
- Use TypeScript for all components

### Naming Conventions

- Components: PascalCase (e.g., `HomePage.tsx`)
- Hooks: camelCase with "use" prefix (e.g., `useStackQuery.ts`)
- Utilities: camelCase (e.g., `utils.ts`)
- Types: PascalCase with Dto/Interface suffix (e.g., `StackDetailsDto`)

### Styling

- Use TailwindCSS utility classes
- Avoid inline styles
- Use the `cn()` utility for conditional classes
- Follow mobile-first responsive design

### API Integration

All API calls should go through the `apiClient` in `src/services/api.ts`:

```typescript
import apiClient from '@/services/api'

export const stackApi = {
  getAll: () => apiClient.get<StackListDto[]>('/stacks'),
  getById: (id: string) => apiClient.get<StackDetailsDto>(`/stacks/${id}`),
  create: (data: CreateStackRequest) => apiClient.post('/stacks', data),
}
```

### State Management

- Use React Query for server state
- Use React Router for URL state
- Use Context API sparingly for global UI state
- Avoid prop drilling with composition patterns

## Troubleshooting

### Port 5173 already in use

Kill the process using the port:

```bash
lsof -ti:5173 | xargs kill
```

Or change the port in `vite.config.ts`.

### TypeScript errors after installing packages

Delete node_modules and reinstall:

```bash
rm -rf node_modules package-lock.json
npm install
```

### TailwindCSS classes not working

Ensure your file is included in `tailwind.config.js` content array and that the import is present in `index.css`.

## Next Steps

1. **Setup Wizard** - Implement multi-step wizard for stack creation
2. **Stack Management** - Build stack list and details pages
3. **Real-time Logs** - Implement log streaming with SignalR
4. **Build Progress** - Create build progress UI with real-time updates
5. **Error Handling** - Add error boundaries and toast notifications

## Resources

- [React Documentation](https://react.dev/)
- [Vite Documentation](https://vite.dev/)
- [TailwindCSS Documentation](https://tailwindcss.com/)
- [React Query Documentation](https://tanstack.com/query/latest)
- [React Router Documentation](https://reactrouter.com/)
- [TypeScript Documentation](https://www.typescriptlang.org/)
