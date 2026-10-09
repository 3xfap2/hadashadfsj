import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  server: {
    host: true,
    // Запросы к API идут через тот же адрес, что и сайт: не нужен CORS и работает с телефона в сети.
    proxy: { '/api': 'http://localhost:5078' },
  },
});
