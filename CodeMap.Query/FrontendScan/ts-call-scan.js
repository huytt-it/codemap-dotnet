// Invoked by TypeScriptCallScanner.cs: node ts-call-scan.js <rootDir> <typescriptModuleDir>
// Walks <rootDir> for .ts files (skipping node_modules, .spec.ts, .test.ts, .d.ts) and finds Angular HttpClient
// call sites: `<receiver>.get/post/put/patch/delete/head(url, ...)`. A receiver counts as an HttpClient when its
// text has "http" in it (`this.http`, `httpClient`, `this.httpService`), OR when it names a constructor
// parameter / field / local variable / function parameter that is declared as `HttpClient` (type annotation, or
// initialised with `inject(HttpClient)`) — `this.api.get(...)` is an HTTP call if `api` is one. There is no type
// checker here (files are parsed one by one, nothing is resolved), so "declared as HttpClient" is read off the
// declaration itself, including through an `extends` chain across files and an `import { HttpClient as X }`
// rename. Also does ONE level of injection resolution: if the call lives inside a service class (anything WITHOUT
// an @Component decorator), which OTHER classes take that service — as a constructor parameter, or as a field
// initialised with `inject(Service)` (Angular 14+) — become the call's `injectedBy`: this is what actually renders
// as a screen to a user, not the service file itself. A call already inside an @Component class needs no
// resolution at all (the component itself IS the screen) - `isComponentItself` tells the C# side not to log a
// diagnostic for those. Deliberately only one hop: a service injected into another service (not a component) is
// not traced further; the C# side logs that as a diagnostic instead of guessing. Emits a single JSON array of
// {file, line, httpMethod, rawUrl, injectedBy, isComponentItself} to stdout — normalization and feature
// extraction happen on the C# side (FrontendUrlNormalizer / FeatureExtractor) so this script stays a thin,
// testable-by-inspection shim.
const fs = require('fs');
const path = require('path');
const ts = require(process.argv[3]);

const root = process.argv[2];
const callSites = [];
// {injectorClass: string, injectedTypeNames: string[]} for every class that takes another class as a
// constructor parameter or an inject() field - the raw material for resolving "who injects this service".
const constructorInjections = [];
// className -> {baseName, clientMembers}: the members of that class declared as HttpClient, and the class it extends.
const classInfo = new Map();
// Calls whose receiver is not named like an HTTP client. Whether it IS one depends on a declaration that may sit in
// another class (a base class) or another file that has not been read yet, so they are settled after the walk.
// Entries hold plain strings only — keeping AST nodes would keep every parsed file in memory until the end.
const undecided = [];
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

// `inject(Service)` -> "Service", looking through `!`, `as T` and parentheses; anything else (inject(getToken()),
// inject('literal'), some other function) names no class and returns null.
function injectedTokenName(expr) {
  let e = expr;
  while (e && (ts.isNonNullExpression(e) || ts.isAsExpression(e) || ts.isParenthesizedExpression(e))) e = e.expression;
  if (!e || !ts.isCallExpression(e) || !ts.isIdentifier(e.expression) || e.expression.text !== 'inject') return null;
  const token = e.arguments[0];
  return token && ts.isIdentifier(token) ? token.text : null;
}

function typeReferenceName(typeNode) {
  return (typeNode && ts.isTypeReferenceNode(typeNode) && ts.isIdentifier(typeNode.typeName)) ? typeNode.typeName.text : null;
}

function memberName(member) {
  return member.name && (ts.isIdentifier(member.name) || ts.isPrivateIdentifier(member.name)) ? member.name.text : null;
}

function baseClassName(classNode) {
  const extendsClause = (classNode.heritageClauses || []).find(h => h.token === ts.SyntaxKind.ExtendsKeyword);
  const base = extendsClause && extendsClause.types[0] && extendsClause.types[0].expression;
  return base && ts.isIdentifier(base) ? base.text : null;
}

// A constructor parameter with an accessibility modifier or `readonly` also declares a field of the same name.
function isParameterProperty(param) {
  return (param.modifiers || []).some(m =>
    m.kind === ts.SyntaxKind.PublicKeyword || m.kind === ts.SyntaxKind.PrivateKeyword ||
    m.kind === ts.SyntaxKind.ProtectedKeyword || m.kind === ts.SyntaxKind.ReadonlyKeyword);
}

function classInfoOf(name) {
  let info = classInfo.get(name);
  if (!info) { info = { baseName: null, clientMembers: new Set() }; classInfo.set(name, info); }
  return info;
}

// Does <className> (or a class it extends) declare a field/parameter property called <member> as an HttpClient?
function hasClientMember(className, member, seen = new Set()) {
  if (!className || seen.has(className)) return false;
  seen.add(className);
  const info = classInfo.get(className);
  return !!info && (info.clientMembers.has(member) || hasClientMember(info.baseName, member, seen));
}

function scanFile(filePath) {
  const text = fs.readFileSync(filePath, 'utf8');
  const source = ts.createSourceFile(filePath, text, ts.ScriptTarget.Latest, true);

  // Local names the HttpClient type goes by in this file: `import { HttpClient as Backend }` renames it.
  const clientTypeNames = new Set(['HttpClient']);
  for (const statement of source.statements) {
    const bindings = ts.isImportDeclaration(statement) && statement.importClause && statement.importClause.namedBindings;
    if (!bindings || !ts.isNamedImports(bindings)) continue;
    for (const element of bindings.elements) {
      if ((element.propertyName || element.name).text === 'HttpClient') clientTypeNames.add(element.name.text);
    }
  }

  // Names (parameters, locals) declared as HttpClient anywhere in this file; used for receivers that are a bare identifier.
  const fileClientNames = new Set();

  const isClientDeclaration = decl => clientTypeNames.has(typeReferenceName(decl.type)) || clientTypeNames.has(injectedTokenName(decl.initializer));

  function noteClass(node) {
    const info = classInfoOf(node.name.text);
    info.baseName = info.baseName || baseClassName(node);
    const injectedTypeNames = new Set();

    for (const member of node.members) {
      if (ts.isConstructorDeclaration(member)) {
        for (const p of member.parameters) {
          const typeName = typeReferenceName(p.type);
          if (typeName) injectedTypeNames.add(typeName);
          if (isParameterProperty(p) && ts.isIdentifier(p.name) && isClientDeclaration(p)) info.clientMembers.add(p.name.text);
        }
      } else if (ts.isPropertyDeclaration(member)) {
        const token = injectedTokenName(member.initializer);
        if (token) injectedTypeNames.add(token);
        const name = memberName(member);
        if (name && isClientDeclaration(member)) info.clientMembers.add(name);
      }
    }

    if (injectedTypeNames.size > 0) {
      constructorInjections.push({ injectorClass: node.name.text, injectedTypeNames: [...injectedTypeNames] });
    }
  }

  function visit(node) {
    if (ts.isClassDeclaration(node) && node.name) noteClass(node);

    if ((ts.isParameter(node) || ts.isVariableDeclaration(node)) && ts.isIdentifier(node.name) && isClientDeclaration(node)) {
      fileClientNames.add(node.name.text);
    }

    if (ts.isCallExpression(node) && ts.isPropertyAccessExpression(node.expression)) {
      const methodName = node.expression.name.text;
      if (HTTP_METHODS.has(methodName) && node.arguments.length > 0) {
        const receiver = node.expression.expression;
        const urlArg = node.arguments[0];
        const { line } = source.getLineAndCharacterOfPosition(node.getStart(source));
        const cls = enclosingClass(node);
        const site = {
          file: path.relative(root, filePath).replace(/\\/g, '/'),
          line: line + 1,
          httpMethod: methodName.toUpperCase(),
          rawUrl: urlArg.getText(source),
          serviceClass: cls ? cls.name.text : null,
          isComponentItself: cls ? hasDecoratorNamed(cls, 'Component') : false,
        };

        if (/http/i.test(receiver.getText(source))) {
          callSites.push(site);
        } else if (ts.isPropertyAccessExpression(receiver) && receiver.expression.kind === ts.SyntaxKind.ThisKeyword && cls) {
          undecided.push({ site, className: cls.name.text, member: receiver.name.text });
        } else if (ts.isIdentifier(receiver)) {
          undecided.push({ site, clientNames: fileClientNames, identifier: receiver.text });
        }
      }
    }
    ts.forEachChild(node, visit);
  }

  visit(source);
}

walk(root);

for (const { site, className, member, clientNames, identifier } of undecided) {
  const isClient = className ? hasClientMember(className, member) : clientNames.has(identifier);
  if (isClient) callSites.push(site);
}

for (const call of callSites) {
  call.injectedBy = (call.serviceClass && !call.isComponentItself)
    ? [...new Set(constructorInjections
        .filter(c => c.injectedTypeNames.includes(call.serviceClass))
        .map(c => c.injectorClass))]
    : [];
  delete call.serviceClass; // internal-only, not part of the C# RawCall contract
}

process.stdout.write(JSON.stringify(callSites));
