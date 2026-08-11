import react from '@vitejs/plugin-react'
// `defineConfig` de 'vitest/config' reexporta o de 'vite' já tipado com a chave `test`.
import { defineConfig } from 'vitest/config'

// Porta fixa da API no perfil "http" de src/Prumo.Api/Properties/launchSettings.json (mesma
// porta documentada em .env.example, VITE_API_BASE_URL). Repetida aqui em vez de importada: o
// launchSettings é a fonte da verdade, mas vive num projeto .NET fora do grafo do frontend.
const API_DEV_SERVER_URL = 'http://localhost:5096'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      // Contrato API ↔ Frontend (specs/features/met-477-fundacao-repos-e-gates/spec.md): ambos os
      // lados já usam o prefixo /api, então não há `rewrite` a fazer. `changeOrigin` evita que o
      // backend veja o Host do Vite dev server — dispensa CORS na API em dev.
      '/api': {
        target: API_DEV_SERVER_URL,
        changeOrigin: true,
      },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/setupTests.ts'],
  },
})
