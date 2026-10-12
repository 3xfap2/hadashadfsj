import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  // CRM живёт на том же адресе, что и сайт, в своей папке: в проде сборка лежит в wwwroot/crm.
  base: '/crm/',
  plugins: [react()],
  server: {
    port: 5174,
    strictPort: true,
    // Запросы к API идут через тот же адрес, что и CRM: не нужен CORS.
    proxy: { '/api': 'http://localhost:5078' },
  },
});
