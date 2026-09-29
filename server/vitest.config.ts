import { cloudflareTest } from '@cloudflare/vitest-pool-workers';
import { defineConfig } from 'vitest/config';
import { TEST_VAULT_ID } from './test/constants.ts';

export default defineConfig({
  plugins: [
    cloudflareTest({
      wrangler: { configPath: './wrangler.toml' },
      miniflare: { bindings: { VAULT_ID: TEST_VAULT_ID } },
    }),
  ],
});
