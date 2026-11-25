const fs = require('node:fs');
const path = require('node:path');
const root = path.resolve(__dirname, '..');
let count = 0;
function visit(directory) {
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (['bin', 'obj', '.local', 'runs', '.git'].includes(entry.name)) continue;
    const file = path.join(directory, entry.name);
    if (entry.isDirectory()) visit(file);
    else if (entry.name.endsWith('.md')) {
      for (const match of fs.readFileSync(file, 'utf8').matchAll(/\[[^\]]*\]\(([^)]+)\)/g)) {
        const target = match[1];
        if (/^(https?:|#)/.test(target)) continue;
        if (!fs.existsSync(path.resolve(path.dirname(file), target.split('#')[0]))) throw new Error(`Broken link in ${file}: ${target}`);
        count++;
      }
    }
  }
}
visit(root);
console.log(`Local documentation links checked: ${count}`);
