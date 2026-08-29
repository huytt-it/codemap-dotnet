// Invoked by TypeScriptCallScanner.cs: node ts-call-scan.js <rootDir> <typescriptModuleDir>
// Walks <rootDir> for .ts files (skipping node_modules, .spec.ts, .test.ts, .d.ts) and finds Angular HttpClient
// call sites: `<something with "http" in its name>.get/post/put/patch/delete/head(url, ...)`. Emits a single
// JSON array of {file, line, httpMethod, rawUrl} to stdout — normalization and feature extraction happen on the
// C# side (FrontendUrlNormalizer / FeatureExtractor) so this script stays a thin, testable-by-inspection shim.
const fs = require('fs');
const path = require('path');
const ts = require(process.argv[3]);

const root = process.argv[2];
const results = [];
const HTTP_METHODS = new Set(['get', 'post', 'put', 'patch', 'delete', 'head']);

function walk(dir) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === 'node_modules' || entry.name.startsWith('.')) continue;
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) { walk(full); continue; }
    if (!entry.name.endsWith('.ts')) continue;
    if (entry.name.endsWith('.spec.ts') || entry.name.endsWith('.test.ts') || entry.name.endsWith('.d.ts')) continue;
    scanFile(full);
  }
}

function scanFile(filePath) {
  const text = fs.readFileSync(filePath, 'utf8');
  const source = ts.createSourceFile(filePath, text, ts.ScriptTarget.Latest, true);

  function visit(node) {
    if (ts.isCallExpression(node) && ts.isPropertyAccessExpression(node.expression)) {
      const methodName = node.expression.name.text;
      if (HTTP_METHODS.has(methodName) && node.arguments.length > 0) {
        const receiverText = node.expression.expression.getText(source);
        if (/http/i.test(receiverText)) {
          const urlArg = node.arguments[0];
          const { line } = source.getLineAndCharacterOfPosition(node.getStart(source));
          results.push({
            file: path.relative(root, filePath).replace(/\\/g, '/'),
            line: line + 1,
            httpMethod: methodName.toUpperCase(),
            rawUrl: urlArg.getText(source),
          });
        }
      }
    }
    ts.forEachChild(node, visit);
  }

  visit(source);
}

walk(root);
process.stdout.write(JSON.stringify(results));
