import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

// Component and browser-API tests (*.spec.*). The pure-logic tests (*.test.ts) run on Node's own runner.
export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    setupFiles: ['tests/setup.ts'],
    include: ['tests/**/*.spec.{ts,tsx}'],
    restoreMocks: true,
  },
});
