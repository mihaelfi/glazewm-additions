import { defineConfig } from 'vite';
import solid from 'vite-plugin-solid';

export default defineConfig({
  base: './',
  plugins: [solid()],
  build: {
    target: 'es2022',
    rollupOptions: {
      input: ['bar.html', 'panel.html', 'ram.html', 'gpu.html', 'storage.html', 'network.html', 'settings.html', 'clock.html'],
    },
  },
});
