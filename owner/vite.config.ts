import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  // Кабинет владельца живёт на том же адресе, что и сайт, в своей папке: в проде сборка лежит в wwwroot/owner.
  base: '/owner/',
  plugins: [react()],
  server: {
    port: 5175,
    strictPort: true,
    // Запросы к API идут через тот же адрес, что и кабинет: не нужен CORS.
    proxy: { '/api': 'http://localhost:5078' },
  },
});
