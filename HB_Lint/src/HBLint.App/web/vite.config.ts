import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// 빌드 결과는 서버가 주는 ../wwwroot (git 밖).
// 개발: hblint-app --port 5317 --dev-origin http://localhost:5173 을 띄우고 npm run dev — /api는 서버로 넘긴다.
export default defineConfig({
  plugins: [react()],
  base: './',
  build: { outDir: '../wwwroot', emptyOutDir: true },
  server: { proxy: { '/api': 'http://127.0.0.1:5317' } },
});
