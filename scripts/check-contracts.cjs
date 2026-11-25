const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const root = path.resolve(__dirname, '..');
const services = {
  authentication: ['Authentication', 'authentication'],
  'user-management': ['UserManagement', 'user-management'],
  'school-management': ['SchoolManagement', 'school-management'],
  scheduling: ['Scheduling', 'scheduling'],
  'vehicle-management': ['VehicleManagement', 'vehicle-management'],
  'learning-tracking': ['LearningTracking', 'learning-tracking'],
  notifications: ['Notifications', 'notifications'],
  'document-management': ['DocumentManagement', 'document-management'],
  chat: ['ChatBackend', 'chat'],
};
const anonymous = new Set([
  'POST /api/v1/auth/registrations', 'POST /api/v1/auth/login', 'POST /api/v1/auth/refresh',
  'POST /api/v1/auth/challenges', 'POST /api/v1/auth/challenges/completion',
  'POST /api/v1/auth/oidc/requests', 'POST /api/v1/auth/oidc/callback',
  'GET /.well-known/openid-configuration', 'GET /.well-known/jwks.json',
]);
const routes = [], sources = [], counts = {};
for (const [service, [directory, name]] of Object.entries(services)) {
  const relative = `../Vairas.${directory}/api/${name}.openapi.json`;
  const raw = fs.readFileSync(path.resolve(root, relative));
  const doc = JSON.parse(raw);
  sources.push({ service, path: relative, sha256: crypto.createHash('sha256').update(raw).digest('hex') });
  counts[service] = 0;
  for (const [url, operations] of Object.entries(doc.paths)) {
    if (!url.startsWith('/api/v1/') && !(service === 'authentication' && url.startsWith('/.well-known/'))) continue;
    for (const [verb, operation] of Object.entries(operations)) {
      if (!['get', 'post', 'put', 'patch', 'delete'].includes(verb)) continue;
      const method = verb.toUpperCase(), key = method + ' ' + url;
      const operationId = operation.operationId || crypto.createHash('sha256').update(key).digest('hex').slice(0, 16);
      const upload = service === 'document-management' && key === 'POST /api/v1/schools/{schoolId}/document-files';
      routes.push({ Id: `${service}.${operationId}.${verb}`, Service: service, Method: method, Path: url,
        Anonymous: service === 'authentication' && anonymous.has(key),
        FileTransfer: upload || service === 'document-management' && key === 'GET /api/v1/document-downloads/{grantId}', Upload: upload });
      counts[service]++;
    }
  }
}
routes.sort((a,b) => a.Id.localeCompare(b.Id, 'en'));
const keys = routes.map(r => r.Method + ' ' + r.Path.replace(/\{[^}]+\}/g, '{}').toLowerCase());
if (new Set(keys).size !== routes.length || new Set(routes.map(r => r.Id)).size !== routes.length) throw new Error('Ambiguous public operations');
const catalog = path.join(root, 'Vairas.Gateway', 'routes.json');
if (process.argv.includes('--write')) {
  fs.writeFileSync(catalog, JSON.stringify({ PublicRoutes: routes }, null, 2) + '\n');
  fs.writeFileSync(path.join(root, 'source-manifest.json'), JSON.stringify({ sources }, null, 2) + '\n');
} else {
  const actual = JSON.parse(fs.readFileSync(catalog, 'utf8')).PublicRoutes;
  if (JSON.stringify(actual) !== JSON.stringify(routes)) throw new Error('Public route catalogue differs from owner contracts. Review changes, then run --write.');
  const manifest = JSON.parse(fs.readFileSync(path.join(root, 'source-manifest.json'), 'utf8'));
  if (JSON.stringify(manifest.sources) !== JSON.stringify(sources)) throw new Error('Owner contract hashes changed; review and refresh source-manifest.json.');
}
console.log(JSON.stringify({ publicOperations: routes.length, counts, internalRoutesPublished: false, ambiguousOperations: 0 }, null, 2));
