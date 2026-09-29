import type { Env as ServerEnv } from '../src/env.ts';

declare global {
  namespace Cloudflare {
    interface Env extends ServerEnv {}
    interface GlobalProps {
      mainModule: typeof import('../src/index.ts');
      durableNamespaces: 'Vault';
    }
  }
}
