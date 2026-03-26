import { defineConfig } from 'vite';

export default defineConfig({
  server: {
    proxy: {
      '/api': 'https://localhost:5002',
      '/conduit': {
        target: 'https://localhost:5002',
        ws: true,
      },
    },
  },
});
