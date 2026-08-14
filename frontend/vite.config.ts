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
    // SignalR ships misplaced /*#__PURE__*/ annotations that Rolldown warns on (aspnetcore#55286).
    rolldownOptions: {
      checks: {
        invalidAnnotation: false,
      },
      output: {
        codeSplitting: {
          groups: [
            {
              name: 'vendor-signalr',
              test: /node_modules[\\/]@microsoft[\\/]signalr/,
              priority: 30,
            },
            {
              name: 'vendor-tiptap',
              test: /node_modules[\\/]@tiptap/,
              priority: 25,
            },
            {
              name: 'vendor-markdown',
              test: /node_modules[\\/](react-markdown|remark-|rehype-|unist-|mdast-|micromark|markdown-table)/,
              priority: 24,
            },
            {
              name: 'vendor-forms',
              test: /node_modules[\\/](react-hook-form|@hookform|zod)/,
              priority: 23,
            },
            {
              name: 'vendor-query',
              test: /node_modules[\\/]@tanstack[\\/]react-query/,
              priority: 22,
            },
            {
              name: 'vendor-grid',
              test: /node_modules[\\/]react-grid-layout/,
              priority: 21,
            },
            {
              name: 'vendor-ui',
              test: /node_modules[\\/](lucide-react|sonner|clsx|tailwind-merge)/,
              priority: 20,
            },
            {
              name: 'vendor-http',
              test: /node_modules[\\/]axios/,
              priority: 19,
            },
            {
              name: 'vendor-react',
              test: /node_modules[\\/](react|react-dom|react-router|scheduler)[\\/]/,
              priority: 18,
            },
          ],
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
        timeout: 300_000,
      },
      '/hubs': {
        target: 'http://localhost:5128',
        changeOrigin: true,
        ws: true,
      },
    },
  },
})
