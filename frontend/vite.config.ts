import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// In dev the frontend (5173) proxies /api and /health to the backend (8080),
// so the code can use relative paths just like in prod.
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': 'http://localhost:8080',
      '/health': 'http://localhost:8080',
    },
  },
})
