import type { Vault } from './vault.ts';

export interface Env {
  VAULT: DurableObjectNamespace<Vault>;
  BODIES: R2Bucket;
  AUTH_LIMITER: RateLimit;
  VAULT_ID: string;
}
