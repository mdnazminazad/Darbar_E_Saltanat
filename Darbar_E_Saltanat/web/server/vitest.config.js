import { defineConfig } from 'vitest/config';
export default defineConfig({ test: { setupFiles: ['./tests/setup-env.js'], pool: 'forks', poolOptions: { forks: { singleFork: true } }, testTimeout: 20000 } });
