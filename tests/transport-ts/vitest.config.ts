import { createRequire } from 'node:module';
import { defineConfig } from 'vitest/config';

const require = createRequire(import.meta.url);

export default defineConfig({
  resolve: {
    // The transport's source lives in src/Conduit.TypeGen/Transport, where no node_modules is reachable by walking up.
    // Point its import AND these tests' at this project's one copy, so a spy on HubConnectionBuilder.prototype is a
    // spy on the class the transport builds with.
    alias: [{ find: /^@microsoft\/signalr$/, replacement: require.resolve('@microsoft/signalr') }],
  },
  test: {
    include: ['*.spec.ts'],
    environment: 'node',
  },
});
