import { writeFileSync } from 'node:fs';
import { buildVectors } from './vectors.ts';

const out = new URL('../../test-vectors.json', import.meta.url);
writeFileSync(out, JSON.stringify(buildVectors(), null, 2) + '\n');
console.log(`wrote ${out.pathname}`);
