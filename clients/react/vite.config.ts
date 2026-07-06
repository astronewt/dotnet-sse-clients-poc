import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Proxy the SSE stream and admin hooks to the ASP.NET Core server so the
// browser talks to a single origin (no CORS, and EventSource works cleanly).
// https://vite.dev/config/
export default defineConfig({
  // Relative base so the build works when served under a subpath (/react/) behind the edge.
  base: './',
  plugins: [react()],
  server: {
    proxy: {
      '/sse': { target: 'http://localhost:5020', changeOrigin: true },
      '/admin': { target: 'http://localhost:5020', changeOrigin: true },
      '/auth': { target: 'http://localhost:5020', changeOrigin: true },
      '/sensors': { target: 'http://localhost:5020', changeOrigin: true },
    },
  },
})
