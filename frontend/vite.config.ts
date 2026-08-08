import react from '@vitejs/plugin-react'
// `defineConfig` de 'vitest/config' reexporta o de 'vite' já tipado com a chave `test`.
import { defineConfig } from 'vitest/config'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/setupTests.ts'],
  },
})
