import { build } from 'esbuild';
import { readFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';

const result = await build({
  entryPoints: ['src/main.js'], bundle: true, minify: true, format: 'iife', legalComments: 'none',
  target: 'es2020', write: false, metafile: true, outfile: 'out.js',
});
const packages = new Map();
for (const input of Object.keys(result.metafile.inputs)) {
  const m = input.match(/^(.*node_modules\/(?:@[^/]+\/)?[^/]+)\//);
  if (!m || packages.has(m[1])) continue;
  const manifest = JSON.parse(readFileSync(join(m[1], 'package.json'), 'utf8'));
  packages.set(m[1], `${manifest.name}@${manifest.version} (${manifest.license})`);
}
const header = `/*! Bundled by samples/Ycs.Server/demo (npm run build). Packages and licenses:\n${[...packages.values()].sort().map((p) => ` * ${p}`).join('\n')}\n */\n`;
const { writeFileSync, mkdirSync } = await import('node:fs');
mkdirSync('../wwwroot/js', { recursive: true });
writeFileSync('../wwwroot/js/demo.js', header + result.outputFiles[0].text);
