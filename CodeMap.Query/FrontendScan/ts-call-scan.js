// Invoked by TypeScriptCallScanner.cs: node ts-call-scan.js <rootDir> <typescriptModuleDir>
// Walks <rootDir> for .ts files (skipping node_modules, .spec.ts, .test.ts, .d.ts) and finds Angular HttpClient
// call sites: `<something with "http" in its name>.get/post/put/patch/delete/head(url, ...)`. Also does ONE
// level of constructor-injection resolution: if the call lives inside a service class (anything WITHOUT an
// @Component decorator), which OTHER classes' constructors take that service as a parameter (Angular DI)
// become the call's `injectedBy` — this is what actually renders as a screen to a user, not the service file
// itself. A call already inside an @Component class needs no resolution at all (the component itself IS the
// screen) - `isComponentItself` tells the C# side not to log a diagnostic for those. Deliberately only one
// hop: a service injected into another service (not a component) is not traced further; the C# side logs
// that as a diagnostic instead of guessing. Emits a single JSON array of
// {file, line, httpMethod, rawUrl, injectedBy, isComponentItself} to stdout — normalization and feature
// extraction happen on the C# side (FrontendUrlNormalizer / FeatureExtractor) so this script stays a thin,
// testable-by-inspection shim.
const fs = require('fs');
const path = require('path');
const ts = require(process.argv[3]);

const root = process.argv[2];
const callSites = [];
// {injectorClass: string, injectedTypeNames: string[]} for every class with a constructor parameter typed as
// another class - the raw material for resolving "who injects this service".
const constructorInjections = [];
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

// TS 4.8+ moved decorator access from node.decorators to ts.getDecorators(node); support both so this works
// against whatever `typescript` version the scanned project itself has installed (spec: no vendoring).
function decoratorsOf(node) {
  if (typeof ts.getDecorators === 'function') return ts.getDecorators(node) || [];
  return node.decorators || [];
}

function hasDecoratorNamed(node, name) {
  return decoratorsOf(node).some(d => {
    const expr = ts.isCallExpression(d.expression) ? d.expression.expression : d.expression;
    return ts.isIdentifier(expr) && expr.text === name;
  });
}

function enclosingClass(node) {
  for (let cur = node.parent; cur; cur = cur.parent) {
    if (ts.isClassDeclaration(cur) && cur.name) return cur;
  }
  return null;
}

function scanFile(filePath) {
  const text = fs.readFileSync(filePath, 'utf8');
  const source = ts.createSourceFile(filePath, text, ts.ScriptTarget.Latest, true);

  function visit(node) {
    if (ts.isClassDeclaration(node) && node.name) {
      const ctor = node.members.find(ts.isConstructorDeclaration);
      if (ctor) {
        const injectedTypeNames = ctor.parameters
          .map(p => (p.type && ts.isTypeReferenceNode(p.type) && ts.isIdentifier(p.type.typeName)) ? p.type.typeName.text : null)
          .filter(Boolean);
        if (injectedTypeNames.length > 0) {
          constructorInjections.push({ injectorClass: node.name.text, injectedTypeNames });
        }
      }
    }

    if (ts.isCallExpression(node) && ts.isPropertyAccessExpression(node.expression)) {
      const methodName = node.expression.name.text;
      if (HTTP_METHODS.has(methodName) && node.arguments.length > 0) {
        const receiverText = node.expression.expression.getText(source);
        if (/http/i.test(receiverText)) {
          const urlArg = node.arguments[0];
          const { line } = source.getLineAndCharacterOfPosition(node.getStart(source));
          const cls = enclosingClass(node);
          callSites.push({
            file: path.relative(root, filePath).replace(/\\/g, '/'),
            line: line + 1,
            httpMethod: methodName.toUpperCase(),
            rawUrl: urlArg.getText(source),
            serviceClass: cls ? cls.name.text : null,
            isComponentItself: cls ? hasDecoratorNamed(cls, 'Component') : false,
          });
        }
      }
    }
    ts.forEachChild(node, visit);
  }

  visit(source);
}

walk(root);

for (const call of callSites) {
  call.injectedBy = (call.serviceClass && !call.isComponentItself)
    ? constructorInjections
        .filter(c => c.injectedTypeNames.includes(call.serviceClass))
        .map(c => c.injectorClass)
    : [];
  delete call.serviceClass; // internal-only, not part of the C# RawCall contract
}

process.stdout.write(JSON.stringify(callSites));
