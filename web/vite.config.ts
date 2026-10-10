import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  server: {
    // Сайт разработки слушает только этот компьютер. Чтобы открыть его с телефона в своей сети: npm run dev -- --host
    // Запросы к API идут через тот же адрес, что и сайт: не нужен CORS.
    proxy: { '/api': 'http://localhost:5078' },
  },
});
