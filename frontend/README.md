# Prumo — Frontend

React + TypeScript + Vite. Node fixado em `.nvmrc` (ver `harness.manifest.yaml` do harness para os
gates completos).

## Scripts

- `npm run dev` — servidor de desenvolvimento.
- `npm run lint` — ESLint (`eslint.config.js`, flat config), zero warnings tolerados.
- `npm run test` — Vitest em modo não-interativo (`vitest run`).
- `npm run test:watch` — Vitest em modo watch.
- `npm run build` — type-check (`tsc -b`) seguido de `vite build`.
