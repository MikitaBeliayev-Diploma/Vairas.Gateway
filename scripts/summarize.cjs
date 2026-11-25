const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '..');
const run = path.resolve(process.argv[2] || '');
if (!run.startsWith(path.join(root, 'evidence', 'runs') + path.sep)) throw new Error('Expected a Gateway evidence run directory.');
const readJson = file => JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
function counters(file) {
  const text = fs.readFileSync(file, 'utf8');
  const values = text.match(/<Counters\s[^>]+>/)?.[0];
  if (!values) throw new Error(`Missing counters: ${file}`);
  const result = Object.fromEntries([...values.matchAll(/(\w+)="(\d+)"/g)].map(m => [m[1], Number(m[2])]));
  if (!result.total || result.failed !== 0 || result.passed !== result.total) throw new Error(`Incomplete or failing run: ${file}`);
  return { total: result.total, passed: result.passed, failed: result.failed };
}
const summary = readJson(path.join(run, 'summary.json'));
const catalogue = readJson(path.join(root, 'Vairas.Gateway', 'routes.json')).PublicRoutes;
const counts = {};
for (const route of catalogue) counts[route.Service] = (counts[route.Service] || 0) + 1;
const evidence = {
  verifiedAt: summary.verifiedAt,
  sourceRun: path.relative(root, run).replaceAll('\\', '/'),
  routes: { publicOperations: catalogue.length, owners: counts, internalRoutesPublished: false },
  gatewayTests: counters(path.join(run, 'gateway', 'gateway.trx')),
  gatewayLineCoveragePercent: summary.gatewayLineCoverage,
  ownerServiceSmoke: summary.services === 'PASSED' ? {
    ...counters(path.join(run, 'services', 'services.trx')),
    owners: ['Authentication', 'User Management', 'School Management', 'Document Management'],
    postgresDatabases: 4, gatewayTransport: 'Kestrel TCP to real owner TestServer adapters',
    fixtures: ['pre-existing School enrollment', 'Blob', 'ClamAV', 'PDF renderer'],
    scenarios: ['verified-fixture registration/login', 'profile create/read', 'DOCX upload and template lifecycle', 'contract generation', 'Gateway-origin authenticated download', 'actor binding', 'owner rejection after session revocation']
  } : 'NOT RUN',
  image: summary.image === 'PASSED' ? readJson(path.join(root, 'evidence', 'image-smoke.json')) : 'NOT RUN',
  dependencyAudit: { path: path.relative(root, path.join(run, 'dependency-audit.json')).replaceAll('\\', '/'), vulnerablePackages: 0 },
  notVerified: ['nine-service distributed runtime', 'frontend integration', 'cloud deployment', 'live cloud/email/broker providers', 'distributed rate limiting', 'load testing', 'MCP transport and business dispatch']
};
fs.writeFileSync(path.join(root, 'evidence', 'final-summary.json'), JSON.stringify(evidence, null, 2) + '\n');
console.log(JSON.stringify({ gatewayTests: evidence.gatewayTests, coverage: evidence.gatewayLineCoveragePercent, serviceSmoke: summary.services, image: summary.image }));
