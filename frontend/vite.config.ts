import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { execSync } from 'node:child_process';
import { version } from './package.json';

// package.json's version only moves when someone bumps it, which in practice
// is never — every build reported itself as 1.0.0. Stamping the commit and
// build time makes the version identify the actual build
// (1.0.0+a1b2c3d.20260730-1042), which is what a support ticket needs it for.
//
// The sha comes from GIT_SHA when the build runs where git is absent (the
// Docker image build — deploy/Dockerfile.web passes it through), from git
// directly otherwise. The timestamp is always there, so even a build with no
// sha at all is still distinguishable from yesterday's.
function buildVersion(): string {
  let sha = (process.env.GIT_SHA ?? '').trim();
  if (!sha) {
    try {
      sha = execSync('git rev-parse --short HEAD', { stdio: ['ignore', 'pipe', 'ignore'] })
        .toString().trim();
    } catch {
      sha = '';
    }
  }
  const now = new Date();
  const stamp = now.toISOString().slice(0, 16).replace(/-|:/g, '').replace('T', '-');
  return `${version}+${sha ? `${sha}.` : ''}${stamp}`;
}

export default defineConfig({
  plugins: [react()],
  define: {
    __APP_VERSION__: JSON.stringify(buildVersion()),
  },
  server: {
    port: Number(process.env.PORT) || 5173,
    proxy: {
      '/api': {
        target: process.env.API_TARGET || 'http://localhost:5090',
        changeOrigin: true,
      },
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: false,
    rollupOptions: {
      output: {
        manualChunks: {
          react: ['react', 'react-dom', 'react-router-dom'],
          charts: ['recharts'],
        },
      },
    },
  },
});
