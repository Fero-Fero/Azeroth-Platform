import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import path from 'path'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  build: {
    rollupOptions: {
      output: {
        manualChunks: (id) => {
            if (id.includes('node_modules/@microsoft/signalr')) return 'vendor-signalr'
            if (id.includes('node_modules/@tanstack/react-query')) return 'vendor-query'
            if (id.includes('node_modules/react-hook-form') || id.includes('node_modules/@hookform') || id.includes('node_modules/zod')) return 'vendor-forms'
            if (id.includes('node_modules/lucide-react') || id.includes('node_modules/sonner') || id.includes('node_modules/clsx') || id.includes('node_modules/tailwind-merge')) return 'vendor-ui'
            if (id.includes('node_modules/axios')) return 'vendor-http'
            if (id.includes('node_modules/react') || id.includes('node_modules/react-dom') || id.includes('node_modules/react-router-dom') || id.includes('node_modules/scheduler')) return 'vendor-react'
          },
      },
    },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:5128',
        changeOrigin: true,
      },
      '/hubs': {
        target: 'http://localhost:5128',
        changeOrigin: true,
        ws: true,
      },
    },
  },
})
