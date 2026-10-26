using CodeMap.Query.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CodeMap.Roslyn.Scan;

/// <summary>
/// Walks one syntax tree, producing a SymbolRecord for each type/member, edges for inherits/implements and
/// call/new/read/write, entry points, and the interface/DI/MediatR mapping data the solution-wide passes in
/// ScanResults consume. Same output at every level: an L1 compilation binds calls between the solution's own
/// types just as well as L2 does; it only misses what needs a NuGet type to resolve.
///
/// L1 gets its semantic model from a compilation shared across the WHOLE solution (all projects merged, see
/// SyntaxOnlyScanner), so a base type declared in another project still resolves correctly — no name matching
/// needed. L2 gets its semantic model from the real per-project MSBuildWorkspace compilation.
/// Only when a symbol genuinely can't be resolved (outside the solution: BCL, or a NuGet package with no real
/// reference at L1) do we decide whether to emit it (include-external) or log it to diagnostics.
/// </summary>
internal sealed class SyntaxSymbolWalker : CSharpSyntaxWalker
{
    private static readonly SymbolDisplayFormat TypeNameFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

    private readonly SemanticModel _model;
    private readonly string _project;
    private readonly string _file;
    private readonly bool _includeExternal;
    private readonly string? _diAttributeName;
    private readonly Stack<string> _typeStack = new();
    private readonly Stack<string> _typeIdStack = new();
    private readonly Stack<string> _memberIdStack = new();
    private readonly Stack<ControllerInfo?> _controllerStack = new();

    public List<SymbolRecord> Symbols { get; } = new();
    public List<EdgeRecord> DirectEdges { get; } = new();
    public List<UnresolvedBaseRef> UnresolvedBaseRefs { get; } = new();

    /// <summary>(interfaceTypeDocId, implementingTypeDocId) — structural source for di.json, from INamedTypeSymbol.AllInterfaces.</summary>
    public List<(string InterfaceId, string ImplId)> InterfaceImplementations { get; } = new();

    /// <summary>(interfaceMemberDocId, implementingMemberDocId) — from INamedTypeSymbol.FindImplementationForInterfaceMember, drives the interface-expand edge pass.</summary>
    public List<(string InterfaceMemberId, string ImplMemberId)> InterfaceMemberMappings { get; } = new();

    /// <summary>(serviceTypeDocId, implementationTypeDocId) — DI-registration source for di.json (services.AddScoped&lt;IFoo, Foo&gt;(), etc.).</summary>
    public List<(string ServiceId, string ImplId)> DiRegistrations { get; } = new();

    /// <summary>A recognized AddScoped/AddSingleton/AddTransient call whose service or implementation type could not be statically resolved.</summary>
    public List<(string File, int Line, string Reason)> UnresolvedDiRegistrations { get; } = new();

    /// <summary>(serviceOrSelfTypeDocId, implementationTypeDocId) — attribute-convention DI source (spec section 5, P10): a type marked with the configured attribute, bound to the ONE real interface it implements, or to itself if it implements none.</summary>
    public List<(string ServiceId, string ImplId)> AttributeDiBindings { get; } = new();

    /// <summary>A type marked with the configured DI attribute but implementing 2+ real (non-empty-marker) interfaces — can't pick one without guessing.</summary>
    public List<(string TypeId, List<string> CandidateInterfaceIds)> AmbiguousDiTypes { get; } = new();

    /// <summary>entrypoints.json (spec section 5, "Entry point"): http (Controller action), job (BackgroundService/IHostedService), handler (MediatR).</summary>
    public List<EntryPoint> EntryPoints { get; } = new();

    /// <summary>(requestOrNotificationTypeDocId, handleMethodDocId) — from a type implementing IRequestHandler&lt;,&gt;/INotificationHandler&lt;&gt;, drives the mediator.Send/.Publish virtual-edge pass.</summary>
    public List<(string RequestTypeId, string HandleMethodId)> MediatrHandlerMappings { get; } = new();

    /// <summary>(callSiteMemberDocId, constructedRequestOrNotificationTypeDocId, file, line) — a `mediator.Send(new FooCommand())` / `.Publish(new FooEvent())` call site.</summary>
    public List<(string FromId, string ConstructedTypeId, string File, int Line)> MediatrSendSites { get; } = new();

    public SyntaxSymbolWalker(SemanticModel model, string project, string file, bool includeExternal, string? diAttributeName = null)
    {
        _model = model;
        _project = project;
        _file = file;
        _includeExternal = includeExternal;
        _diAttributeName = diAttributeName;
    }

    public override void VisitClassDeclaration(ClassDeclarationSyntax node)
        => VisitTypeDecl(node, "Class", node.BaseList, node.AttributeLists, () => base.VisitClassDeclaration(node));

    public override void VisitInterfaceDeclaration(InterfaceDeclarationSyntax node)
        => VisitTypeDecl(node, "Interface", node.BaseList, node.AttributeLists, () => base.VisitInterfaceDeclaration(node));

    public override void VisitStructDeclaration(StructDeclarationSyntax node)
        => VisitTypeDecl(node, "Struct", node.BaseList, node.AttributeLists, () => base.VisitStructDeclaration(node));

    public override void VisitRecordDeclaration(RecordDeclarationSyntax node)
        => VisitTypeDecl(node, "Record", node.BaseList, node.AttributeLists, () => base.VisitRecordDeclaration(node));

    public override void VisitEnumDeclaration(EnumDeclarationSyntax node)
    {
        EmitTypeSymbol(node, "Enum", node.AttributeLists);
        base.VisitEnumDeclaration(node);
    }

    public override void VisitDelegateDeclaration(DelegateDeclarationSyntax node)
    {
        EmitTypeSymbol(node, "Delegate", node.AttributeLists);
        base.VisitDelegateDeclaration(node);
    }

    public override void VisitEventDeclaration(EventDeclarationSyntax node)
    {
        var id = EmitMemberSymbol(node, "Event", node.AttributeLists);
        WithMember(id, () => base.VisitEventDeclaration(node));
    }

    public override void VisitEventFieldDeclaration(EventFieldDeclarationSyntax node)
        => VisitVariableDeclarators(node.Declaration.Variables, "Event", node.AttributeLists);

    public override void VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var id = EmitMemberSymbol(node, "Method", node.AttributeLists);
        if (id != null)
            TryRecordHttpActionEntryPoint(id, node);
        WithMember(id, () => base.VisitMethodDeclaration(node));
    }

    public override void VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var id = EmitMemberSymbol(node, "Constructor", node.AttributeLists);
        WithMember(id, () => base.VisitConstructorDeclaration(node));
    }

    public override void VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        var id = EmitMemberSymbol(node, "Property", node.AttributeLists);
        WithMember(id, () => base.VisitPropertyDeclaration(node));
    }

    public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        => VisitVariableDeclarators(node.Declaration.Variables, "Field", node.AttributeLists);

    public override void VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        HandleInvocation(node);
        TryRecordDiRegistration(node);
        TryRecordMediatrSend(node);
        base.VisitInvocationExpression(node);
    }

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        HandleObjectCreation(node);
        base.VisitObjectCreationExpression(node);
    }

    // Target-typed `new()` (C# 9+, e.g. `OrderRepository _repository = new();`) is a SEPARATE syntax node type
    // from `new Foo()` — both derive from BaseObjectCreationExpressionSyntax, so HandleObjectCreation handles either.
    public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
        HandleObjectCreation(node);
        base.VisitImplicitObjectCreationExpression(node);
    }

    public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        HandleMemberAccess(node);
        HandleMethodGroup(node);
        base.VisitMemberAccessExpression(node);
    }

    public override void VisitIdentifierName(IdentifierNameSyntax node)
    {
        HandleMethodGroup(node);
        base.VisitIdentifierName(node);
    }

    public override void VisitGenericName(GenericNameSyntax node)
    {
        HandleMethodGroup(node);
        base.VisitGenericName(node);
    }

    /// <summary>Emits a symbol per declarator (Field/EventField), then visits ITS OWN initializer with that
    /// declarator's own docId as the current "from" member — matters for `private int a = Foo(), b = Bar();`
    /// where each initializer must attribute edges to its own field, not to the statement as a whole.</summary>
    private void VisitVariableDeclarators(SeparatedSyntaxList<VariableDeclaratorSyntax> variables, string kind, SyntaxList<AttributeListSyntax> attrLists)
    {
        foreach (var variable in variables)
        {
            var symbol = _model.GetDeclaredSymbol(variable);
            var id = symbol?.GetDocumentationCommentId();
            if (symbol != null && id != null)
            {
                Symbols.Add(new SymbolRecord
                {
                    Id = id,
                    Kind = kind,
                    Name = symbol.Name,
                    ContainingType = _typeStack.Count > 0 ? _typeStack.Peek() : null,
                    Project = _project,
                    File = _file,
                    Line = LineOf(variable),
                    Accessibility = symbol.DeclaredAccessibility.ToString(),
                    Attributes = ExtractAttributeNames(attrLists),
                });
            }

            if (variable.Initializer != null)
                WithMember(id, () => Visit(variable.Initializer.Value));
        }
    }

    private void WithMember(string? memberId, Action visitChildren)
    {
        if (memberId != null) _memberIdStack.Push(memberId);
        visitChildren();
        if (memberId != null) _memberIdStack.Pop();
    }

    private void VisitTypeDecl(
        TypeDeclarationSyntax node,
        string kind,
        BaseListSyntax? baseList,
        SyntaxList<AttributeListSyntax> attrLists,
        Action visitChildren)
    {
        var symbol = EmitTypeSymbol(node, kind, attrLists);

        if (symbol != null && baseList != null)
        {
            var fromId = symbol.GetDocumentationCommentId();
            if (fromId != null)
                foreach (var baseType in baseList.Types)
                    HandleBaseType(fromId, baseType.Type);
        }

        ControllerInfo? controller = null;
        if (symbol != null && kind is "Class" or "Struct" or "Record")
        {
            RecordInterfaceMappings(symbol);
            TryRecordAttributeDiBinding(symbol);
            if (kind == "Class")
                controller = RecordTypeLevelEntryPoint(symbol, attrLists);
        }

        if (symbol != null)
        {
            _typeStack.Push(DisplayName(symbol));
            _typeIdStack.Push(symbol.GetDocumentationCommentId() ?? "");
            _controllerStack.Push(controller);
        }

        visitChildren();

        if (symbol != null)
        {
            _typeStack.Pop();
            _typeIdStack.Pop();
            _controllerStack.Pop();
        }
    }

    /// <summary>
    /// For every interface this type implements (including transitively via a base class), records the
    /// structural (interfaceId, implId) pair for di.json, and maps each interface member to its concrete
    /// implementation via FindImplementationForInterfaceMember — the data the expand-via-interface edge pass needs.
    /// </summary>
    private void RecordInterfaceMappings(INamedTypeSymbol type)
    {
        var typeId = type.OriginalDefinition.GetDocumentationCommentId();
        if (typeId == null) return;

        foreach (var iface in type.AllInterfaces)
        {
            var ifaceId = iface.OriginalDefinition.GetDocumentationCommentId();
            if (ifaceId == null) continue;

            var ifaceDeclaredInSolution = iface.Locations.Any(l => l.IsInSource);
            if (ifaceDeclaredInSolution || _includeExternal)
                InterfaceImplementations.Add((ifaceId, typeId));

            foreach (var member in iface.GetMembers())
            {
                if (member.IsImplicitlyDeclared) continue;
                var implMember = type.FindImplementationForInterfaceMember(member);
                if (implMember == null) continue;

                var ifaceMemberId = member.OriginalDefinition.GetDocumentationCommentId();
                var implMemberId = implMember.OriginalDefinition.GetDocumentationCommentId();
                if (ifaceMemberId != null && implMemberId != null)
                    InterfaceMemberMappings.Add((ifaceMemberId, implMemberId));
            }
        }
    }

    /// <summary>
    /// Attribute-convention DI source (spec section 5, P10). A type marked with the configured attribute
    /// (e.g. [Injectable]) binds to the ONE real interface it implements; implementing zero real interfaces is
    /// self-registration; implementing 2+ is ambiguous — record it for diagnostics instead of guessing which one.
    /// "Real" excludes empty marker interfaces (spec: "không phải marker interface trống").
    /// </summary>
    private void TryRecordAttributeDiBinding(INamedTypeSymbol type)
    {
        if (string.IsNullOrEmpty(_diAttributeName)) return;
        if (!HasConfiguredDiAttribute(type)) return;

        var typeId = type.OriginalDefinition.GetDocumentationCommentId();
        if (typeId == null) return;

        var realInterfaceIds = type.AllInterfaces
            .Where(i => i.GetMembers().Length > 0)
            .Select(i => i.OriginalDefinition.GetDocumentationCommentId())
            .Where(id => id != null)
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        switch (realInterfaceIds.Count)
        {
            case 1:
                AttributeDiBindings.Add((realInterfaceIds[0], typeId));
                break;
            case 0:
                AttributeDiBindings.Add((typeId, typeId)); // self-registration
                break;
            default:
                AmbiguousDiTypes.Add((typeId, realInterfaceIds));
                break;
        }
    }

    private bool HasConfiguredDiAttribute(INamedTypeSymbol type)
    {
        var configured = NormalizeAttributeName(_diAttributeName!);
        foreach (var attr in type.GetAttributes())
        {
            var attrClassName = attr.AttributeClass?.Name;
            if (attrClassName != null && NormalizeAttributeName(attrClassName) == configured)
                return true;
        }

        return false;
    }

    private static string NormalizeAttributeName(string name)
        => name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;

    /// <summary>
    /// Entry point detection (spec section 5, "Entry point"). Matches base types/interfaces BY NAME, not by
    /// resolving the real ASP.NET Core/Hosting/MediatR package symbol — the same pragmatic, name-based approach
    /// already used for base-type resolution elsewhere, and it keeps working even when those packages aren't
    /// fully resolvable (a degraded project, or a project that only references them transitively).
    /// Returns what the controller's action methods need to compute their routes, or null if not a controller.
    /// </summary>
    private ControllerInfo? RecordTypeLevelEntryPoint(INamedTypeSymbol type, SyntaxList<AttributeListSyntax> classAttrLists)
    {
        if (IsBackgroundServiceType(type))
        {
            var execId = type.GetMembers().OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.Name == "ExecuteAsync")?.OriginalDefinition.GetDocumentationCommentId();
            if (execId != null) EntryPoints.Add(new EntryPoint(execId, "job"));
        }
        else if (ImplementsIHostedService(type))
        {
            var startId = type.GetMembers().OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.Name == "StartAsync")?.OriginalDefinition.GetDocumentationCommentId();
            if (startId != null) EntryPoints.Add(new EntryPoint(startId, "job"));
        }

        var handleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (requestTypeId, handleId) in MediatrHandlersOf(type))
        {
            MediatrHandlerMappings.Add((requestTypeId, handleId));
            if (handleIds.Add(handleId))
                EntryPoints.Add(new EntryPoint(handleId, "handler"));
        }

        if (ControllerStyleOf(type, classAttrLists) is not { } style) return null;

        Template? area = null;
        Template route = default, routePrefix = default;
        foreach (var attr in classAttrLists.SelectMany(l => l.Attributes))
        {
            switch (SimpleAttributeName(attr))
            {
                case "Route" when !route.Declared: route = TemplateOf(attr); break;
                case "RoutePrefix" when !routePrefix.Declared: routePrefix = TemplateOf(attr); break;
                case "Area": area = TemplateOf(attr); break;
            }
        }

        var name = type.Name.EndsWith("Controller", StringComparison.Ordinal) ? type.Name[..^"Controller".Length] : type.Name;
        return new ControllerInfo(name, style, route, routePrefix, area?.Value, HasAttribute(classAttrLists, "ApiController"));
    }

    private enum ControllerStyle { AspNetCore, AspNetMvc5, WebApi2 }

    /// <summary>What an action method needs from its controller to compute its route.</summary>
    private sealed record ControllerInfo(
        string Name, ControllerStyle Style, Template Route, Template RoutePrefix, string? Area, bool RequiresAttributeRouting);

    /// <summary>A routing attribute's template argument. Declared with a null Value = it has one, but it isn't a
    /// constant this compilation can fold (at L1: a const from a NuGet package) — the route is unknown, which is
    /// not the same as "no template, inherit the controller's".</summary>
    private readonly record struct Template(bool Declared, string? Value)
    {
        public bool Unresolved => Declared && Value == null;
    }

    /// <summary>
    /// Whether this class is a controller, and of which framework — base types BY NAME, as above. At L1 a framework
    /// base never resolves, but its error type keeps the name written in the base list, so `: ControllerBase` still
    /// matches. A vendor base (`: AbpController`, a company BaseApiController shipped as a package) is just an
    /// unknown name there; then [ApiController]/[Controller]/[RoutePrefix] decide, or ASP.NET's own discovery
    /// convention, the "Controller" suffix — but only over a base that didn't resolve: a suffix over a base that
    /// did resolve to something else (a game's PlayerController) is not a controller.
    /// "ApiController" means Web API 2 only when it is System.Web.Http's or unresolved: ASP.NET Core projects
    /// commonly declare their own `ApiController : ControllerBase`, which must keep ASP.NET Core's conventions.
    /// </summary>
    private static ControllerStyle? ControllerStyleOf(INamedTypeSymbol type, SyntaxList<AttributeListSyntax> attrLists)
    {
        var sawUnresolvedBase = false;
        for (var t = type.BaseType; t != null; t = t.BaseType)
        {
            var unresolved = t.TypeKind == TypeKind.Error;
            sawUnresolvedBase |= unresolved;
            if (t.Name == "ApiController" && (unresolved || NamespaceOf(t) == "System.Web.Http")) return ControllerStyle.WebApi2;
            if (t.Name is "ControllerBase" or "Controller")
                return NamespaceOf(t) == "System.Web.Mvc" ? ControllerStyle.AspNetMvc5 : ControllerStyle.AspNetCore;
        }

        if (HasAttribute(attrLists, "ApiController") || HasAttribute(attrLists, "Controller")) return ControllerStyle.AspNetCore;
        if (HasAttribute(attrLists, "RoutePrefix")) return ControllerStyle.WebApi2;
        if (sawUnresolvedBase && type.Name.EndsWith("Controller", StringComparison.Ordinal)) return ControllerStyle.AspNetCore;
        return null;
    }

    private static string? NamespaceOf(ITypeSymbol type)
        => type.ContainingNamespace is { IsGlobalNamespace: false } ns ? ns.ToDisplayString() : null;

    private static bool IsBackgroundServiceType(INamedTypeSymbol type)
    {
        for (var t = type.BaseType; t != null; t = t.BaseType)
            if (t.Name == "BackgroundService") return true;
        return false;
    }

    private static bool ImplementsIHostedService(INamedTypeSymbol type)
        => type.AllInterfaces.Any(i => i.Name == "IHostedService");

    /// <summary>
    /// Every MediatR IRequestHandler&lt;,&gt;/IRequestHandler&lt;&gt;/INotificationHandler&lt;&gt; this type implements,
    /// as (docId of the request/notification type argument, docId of the Handle method that handles it). One class
    /// may handle several requests, one Handle overload each; before, only the first interface counted, mapped to
    /// the first method named Handle.
    /// Without a MediatR reference (L1, an unrestored L2) `class H : IRequestHandler&lt;GetX, int&gt;` doesn't resolve,
    /// and Roslyn puts an unresolved type written FIRST in a class's base list in BaseType (it might be a class),
    /// not in AllInterfaces — so those are looked at too, the same by-name match as for a resolved interface.
    /// </summary>
    private static IEnumerable<(string RequestTypeId, string HandleId)> MediatrHandlersOf(INamedTypeSymbol type)
    {
        var unresolvedBases = new List<INamedTypeSymbol>();
        for (var t = type.BaseType; t != null; t = t.BaseType)
            if (t.TypeKind == TypeKind.Error) unresolvedBases.Add(t);

        foreach (var iface in type.AllInterfaces.Concat(unresolvedBases))
        {
            var isRequestHandler = iface.Name == "IRequestHandler" && iface.TypeArguments.Length is 1 or 2;
            var isNotificationHandler = iface.Name == "INotificationHandler" && iface.TypeArguments.Length == 1;
            if (!isRequestHandler && !isNotificationHandler) continue;

            var request = iface.TypeArguments[0];
            var requestId = request.OriginalDefinition.GetDocumentationCommentId();
            var handleId = HandleMethodFor(type, iface, request)?.OriginalDefinition.GetDocumentationCommentId();
            if (requestId != null && handleId != null) yield return (requestId, handleId);
        }
    }

    /// <summary>The method implementing the handler interface's Handle when the interface resolved (L2, or MediatR's
    /// shapes declared in source); else — an unresolved interface has no members — the type's own Handle whose
    /// first parameter is the request.</summary>
    private static IMethodSymbol? HandleMethodFor(INamedTypeSymbol type, INamedTypeSymbol handlerInterface, ITypeSymbol request)
    {
        var declared = handlerInterface.GetMembers("Handle").OfType<IMethodSymbol>().FirstOrDefault();
        if (declared != null && type.FindImplementationForInterfaceMember(declared) is IMethodSymbol implementation)
            return implementation;

        return type.GetMembers("Handle").OfType<IMethodSymbol>()
            .FirstOrDefault(m => m.Parameters.Length > 0 && SymbolEqualityComparer.Default.Equals(m.Parameters[0].Type, request));
    }

    private static readonly Dictionary<string, string> HttpVerbAttributeNames = new(StringComparer.Ordinal)
    {
        ["HttpGet"] = "GET",
        ["HttpPost"] = "POST",
        ["HttpPut"] = "PUT",
        ["HttpDelete"] = "DELETE",
        ["HttpPatch"] = "PATCH",
        ["HttpHead"] = "HEAD",
        ["HttpOptions"] = "OPTIONS",
    };

    /// <summary>Web API 2 picks an un-attributed action's verb from its name prefix, and POST when none matches.</summary>
    private static readonly string[] WebApiVerbPrefixes = { "GET", "POST", "PUT", "DELETE", "PATCH", "HEAD", "OPTIONS" };

    /// <summary>
    /// Records an http entry point for an action method. Where its route comes from, in ASP.NET's own order:
    /// the action's template ([HttpGet("x")] or [Route("x")]), composed with the controller's [Route] (or Web API
    /// 2's [RoutePrefix]); else the controller's [Route] alone; else the framework's default conventional route,
    /// marked RouteInferred. Before, only an action carrying a verb attribute counted, and only a literal template
    /// was read — so a conventionally routed MVC/Web API app, a method-level [Route], or a const template produced
    /// no entry point or a wrong route, silently.
    /// </summary>
    private void TryRecordHttpActionEntryPoint(string methodId, MethodDeclarationSyntax node)
    {
        if (_controllerStack.Count == 0 || _controllerStack.Peek() is not { } controller) return; // not inside a recognized controller type
        if (_model.GetDeclaredSymbol(node) is not IMethodSymbol method) return;

        string? verb = null;
        Template verbTemplate = default, routeTemplate = default, actionNameArg = default;
        foreach (var attr in node.AttributeLists.SelectMany(l => l.Attributes))
        {
            var name = SimpleAttributeName(attr);
            if (verb == null && HttpVerbAttributeNames.TryGetValue(name, out var v))
            {
                verb = v; // one HTTP verb per action method is the common case
                verbTemplate = TemplateOf(attr);
            }
            else if (name == "Route" && !routeTemplate.Declared) routeTemplate = TemplateOf(attr);
            else if (name == "ActionName") actionNameArg = TemplateOf(attr);
        }

        if (!IsAction(method, node.AttributeLists, hasRoutingAttribute: verb != null || routeTemplate.Declared)) return;

        var template = verbTemplate.Declared ? verbTemplate : routeTemplate;
        var prefix = controller.Route.Declared ? controller.Route : controller.RoutePrefix;
        string? route;
        var inferred = false;
        if (template.Declared)
            route = template.Unresolved || prefix.Unresolved ? null : ComposeRoute(prefix.Value ?? "", template.Value);
        else if (controller.Route.Declared)
            route = controller.Route.Unresolved ? null : ComposeRoute(controller.Route.Value!, null);
        else if (controller.RequiresAttributeRouting)
            return; // [ApiController] with no route anywhere: ASP.NET Core refuses to start, there is no route to record
        else
        {
            route = controller.Style == ControllerStyle.WebApi2
                ? $"api/{controller.Name.ToLowerInvariant()}/{{id?}}"
                : $"{(controller.Area != null ? controller.Area.ToLowerInvariant() + "/" : "")}[controller]/[action]/{{id?}}";
            inferred = true;
        }

        var actionName = actionNameArg.Value ?? method.Name;
        if (actionNameArg.Value == null && controller.Style == ControllerStyle.AspNetCore &&
            actionName.Length > "Async".Length && actionName.EndsWith("Async", StringComparison.Ordinal))
            actionName = actionName[..^"Async".Length]; // ASP.NET Core 3.0+ default: SuppressAsyncSuffixInActionNames

        if (route != null)
        {
            route = route.Replace("[controller]", controller.Name.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase)
                .Replace("[action]", actionNameArg.Value ?? actionName.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
            if (controller.Area != null)
                route = route.Replace("[area]", controller.Area.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
        }

        var httpMethod = verb
            ?? (controller.Style == ControllerStyle.WebApi2
                ? WebApiVerbPrefixes.FirstOrDefault(p => method.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ?? "POST"
                : "ANY"); // ASP.NET Core and MVC 5 accept every verb on an action with no verb constraint

        EntryPoints.Add(new EntryPoint(methodId, "http", httpMethod, route, inferred ? true : null));
    }

    /// <summary>
    /// ASP.NET's action rules (DefaultApplicationModelProvider.IsAction) that a symbol can answer: an ordinary,
    /// non-static method not marked [NonAction]. A method carrying a verb or [Route] attribute is taken at its
    /// word, as before; one without must also be public, concrete, non-generic and not an override — in a
    /// controller an override is a framework hook (OnActionExecuting, Dispose(bool)), not an action.
    /// </summary>
    private static bool IsAction(IMethodSymbol method, SyntaxList<AttributeListSyntax> attrLists, bool hasRoutingAttribute)
    {
        if (method.MethodKind != MethodKind.Ordinary || method.IsStatic || HasAttribute(attrLists, "NonAction")) return false;
        if (hasRoutingAttribute) return true;
        return method is { DeclaredAccessibility: Accessibility.Public, IsAbstract: false, IsGenericMethod: false, IsOverride: false }
            && method.Name != "Dispose";
    }

    /// <summary>A template starting with "/" or "~/" is absolute (ASP.NET Core, Web API 2 and MVC 5 alike): it replaces the controller's route instead of extending it.</summary>
    private static string ComposeRoute(string controllerRoute, string? methodRoute)
    {
        if (methodRoute != null && (methodRoute.StartsWith('/') || methodRoute.StartsWith("~/", StringComparison.Ordinal)))
            return methodRoute.TrimStart('~').Trim('/');

        var combined = string.IsNullOrEmpty(methodRoute)
            ? controllerRoute
            : $"{controllerRoute.TrimEnd('/')}/{methodRoute.TrimStart('/')}";
        return combined.TrimStart('~').Trim('/');
    }

    /// <summary>The attribute's first positional argument as a constant: a literal, a const (Routes.ById) or nameof(...) all fold.</summary>
    private Template TemplateOf(AttributeSyntax attr)
    {
        var arg = attr.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals == null);
        if (arg == null) return default;
        if (_model.GetConstantValue(arg.Expression) is { HasValue: true, Value: string s }) return new Template(true, s);
        return new Template(true, arg.Expression is LiteralExpressionSyntax { Token.Value: string literal } ? literal : null);
    }

    private static bool HasAttribute(SyntaxList<AttributeListSyntax> attrLists, string simpleName)
        => attrLists.SelectMany(l => l.Attributes).Any(a => SimpleAttributeName(a) == simpleName);

    /// <summary>`[Microsoft.AspNetCore.Mvc.HttpGetAttribute]` -> "HttpGet": the name as matched, whatever the spelling.</summary>
    private static string SimpleAttributeName(AttributeSyntax attr)
        => NormalizeAttributeName(attr.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.Text,
            AliasQualifiedNameSyntax a => a.Name.Identifier.Text,
            SimpleNameSyntax s => s.Identifier.Text,
            _ => attr.Name.ToString(),
        });

    /// <summary>
    /// Syntactic pattern match for `mediator.Send(new FooCommand())` / `.Publish(new FooEvent())` — same
    /// philosophy as TryRecordDiRegistration: matches the well-known method names, doesn't require resolving
    /// the real MediatR IMediator interface. Doesn't try to trace calls through a variable holding the command
    /// (`var cmd = new FooCommand(); mediator.Send(cmd);`) — only the inline-construction form, which covers the
    /// overwhelming majority of real MediatR usage.
    /// </summary>
    private void TryRecordMediatrSend(InvocationExpressionSyntax node)
    {
        var methodName = (node.Expression as MemberAccessExpressionSyntax)?.Name.Identifier.Text;
        if (methodName is not ("Send" or "Publish")) return;
        if (node.ArgumentList.Arguments.Count == 0) return;

        var constructedTypeId = node.ArgumentList.Arguments[0].Expression is BaseObjectCreationExpressionSyntax creation &&
                                BoundMethod(creation) is { MethodKind: MethodKind.Constructor } ctor
            ? ctor.ContainingType.OriginalDefinition.GetDocumentationCommentId()
            : null;
        if (constructedTypeId == null) return;

        var fromId = CurrentFromId();
        if (fromId == null) return;

        MediatrSendSites.Add((fromId, constructedTypeId, _file, LineOf(node)));
    }

    private INamedTypeSymbol? EmitTypeSymbol(SyntaxNode node, string kind, SyntaxList<AttributeListSyntax> attrLists)
    {
        if (_model.GetDeclaredSymbol(node) is not INamedTypeSymbol symbol) return null;
        var id = symbol.GetDocumentationCommentId();
        if (id == null) return null;

        Symbols.Add(new SymbolRecord
        {
            Id = id,
            Kind = kind,
            Name = symbol.Name,
            ContainingType = symbol.ContainingType != null ? DisplayName(symbol.ContainingType) : null,
            Project = _project,
            File = _file,
            Line = LineOf(node),
            Accessibility = symbol.DeclaredAccessibility.ToString(),
            Attributes = ExtractAttributeNames(attrLists),
        });

        return symbol;
    }

    private string? EmitMemberSymbol(SyntaxNode node, string kind, SyntaxList<AttributeListSyntax> attrLists)
    {
        var symbol = _model.GetDeclaredSymbol(node);
        if (symbol == null) return null;
        var id = symbol.GetDocumentationCommentId();
        if (id == null) return null;

        Symbols.Add(new SymbolRecord
        {
            Id = id,
            Kind = kind,
            Name = symbol.Name,
            ContainingType = _typeStack.Count > 0 ? _typeStack.Peek() : null,
            Project = _project,
            File = _file,
            Line = LineOf(node),
            Accessibility = symbol.DeclaredAccessibility.ToString(),
            Attributes = ExtractAttributeNames(attrLists),
        });

        return id;
    }

    private void HandleBaseType(string fromDocId, TypeSyntax typeSyntax)
    {
        var symbol = _model.GetSymbolInfo(typeSyntax).Symbol as INamedTypeSymbol;

        if (symbol != null && symbol.TypeKind != TypeKind.Error)
        {
            // Solution-wide compilation (L1: merged; L2: real project references): IsInSource = declared
            // somewhere in the solution (any project).
            var declaredInSolution = symbol.Locations.Any(l => l.IsInSource);
            if (declaredInSolution || _includeExternal)
                EmitEdge(fromDocId, symbol, symbol.TypeKind == TypeKind.Interface ? "implements" : "inherits", LineOf(typeSyntax));
            return;
        }

        // Still unresolved after merging the whole solution -> genuinely out of reach at L1 (NuGet package, dynamic registration, ...).
        var simpleName = GetSimpleName(typeSyntax);
        if (!string.IsNullOrEmpty(simpleName))
            UnresolvedBaseRefs.Add(new UnresolvedBaseRef(fromDocId, simpleName, _project, _file, LineOf(typeSyntax)));
    }

    private string? CurrentFromId()
    {
        if (_memberIdStack.Count > 0) return _memberIdStack.Peek();
        if (_typeIdStack.Count > 0)
        {
            var typeId = _typeIdStack.Peek();
            return typeId.Length > 0 ? typeId : null;
        }

        return null;
    }

    private void HandleInvocation(InvocationExpressionSyntax node)
    {
        if (BoundMethod(node) is not { } method) return;
        EmitMemberEdge(method, "call", LineOf(node));
    }

    /// <summary>
    /// The method a call or `new` binds to. When an argument's type doesn't resolve (at L1: anything built from a
    /// NuGet type, like `User.Identity.Name` over an unresolved ControllerBase), Roslyn reports an overload
    /// resolution failure with no Symbol even if the method has one overload and the target is not in doubt; that
    /// sole candidate is taken. Several candidates would be a guess and stay unbound, as does dynamic.
    /// </summary>
    private IMethodSymbol? BoundMethod(ExpressionSyntax node)
    {
        var info = _model.GetSymbolInfo(node);
        var symbol = info.Symbol
            ?? (info.CandidateReason == CandidateReason.OverloadResolutionFailure && info.CandidateSymbols.Length == 1
                ? info.CandidateSymbols[0]
                : null);
        return symbol as IMethodSymbol;
    }

    /// <summary>
    /// A method named without being invoked — `xs.Select(MapToDto)`, `Clicked += OnClick`, `new Func&lt;int&gt;(M)`,
    /// `Run(Work)` — is converted to a delegate that something else invokes later, so the code handing it over is
    /// the closest static caller there is. Emitted as a `call` edge marked via:"delegate": the method may run
    /// later, more than once, or never. Roslyn models exactly this conversion as an IMethodReferenceOperation;
    /// the callee of an ordinary `M()` never is one, so a plain call is not counted twice. `Clicked -= OnClick`
    /// is skipped: it takes the handler out, nothing runs because of it.
    /// </summary>
    private void HandleMethodGroup(ExpressionSyntax node)
    {
        // Cheap syntactic exclusions before asking for the operation: the callee of an invocation, and the Name
        // half of `x.M` (the member access as a whole is the method group, and is visited on its own).
        if (node.Parent is InvocationExpressionSyntax invocation && invocation.Expression == node) return;
        if (node.Parent is MemberAccessExpressionSyntax access && access.Name == node) return;
        if (node.Parent is AssignmentExpressionSyntax assignment && assignment.Right == node &&
            assignment.IsKind(SyntaxKind.SubtractAssignmentExpression)) return;
        // Every identifier comes through here; only one that binds to a method is worth building operations for.
        if (_model.GetSymbolInfo(node).Symbol is not IMethodSymbol) return;

        var reference = _model.GetOperation(node) switch
        {
            IMethodReferenceOperation r => r,
            IDelegateCreationOperation { Target: IMethodReferenceOperation r } => r,
            _ => null,
        };
        if (reference == null) return;

        EmitMemberEdge(reference.Method, "call", LineOf(node), via: "delegate");
    }

    private void HandleObjectCreation(BaseObjectCreationExpressionSyntax node)
    {
        if (BoundMethod(node) is not { MethodKind: MethodKind.Constructor } ctor) return;
        EmitMemberEdge(ctor, "new", LineOf(node));
    }

    private void HandleMemberAccess(MemberAccessExpressionSyntax node)
    {
        var symbol = _model.GetSymbolInfo(node).Symbol;
        if (symbol is not (IPropertySymbol or IFieldSymbol)) return; // spec: only property/field member accesses

        var kind = node.Parent is AssignmentExpressionSyntax assign && assign.Left == node ? "write" : "read";
        EmitMemberEdge(symbol, kind, LineOf(node));
    }

    private void EmitMemberEdge(ISymbol target, string kind, int line, string? via = null)
    {
        var declaredInSolution = target.Locations.Any(l => l.IsInSource);
        if (!declaredInSolution && !_includeExternal) return;

        var fromId = CurrentFromId();
        if (fromId == null) return;

        // `"a".Ext(1)` binds to the REDUCED extension method — a synthesized symbol whose docId drops the `this`
        // parameter (M:N.E.Ext(System.Int32), not the declared M:N.E.Ext(System.String,System.Int32); for a
        // generic one M:N.E.Gen``1 instead of M:N.E.Gen``1(``0)). That id matches no symbol in symbols.jsonl, so
        // impact silently lost every caller using extension syntax. ReducedFrom is the declared method (null for
        // the static-call form and for every non-extension call, which were already right).
        if (target is IMethodSymbol { ReducedFrom: { } declared }) target = declared;

        var toId = target.OriginalDefinition.GetDocumentationCommentId();
        if (toId == null) return;

        DirectEdges.Add(new EdgeRecord { From = fromId, To = toId, Kind = kind, File = _file, Line = line, Via = via });
    }

    private void EmitEdge(string fromDocId, INamedTypeSymbol target, string kind, int line)
    {
        var id = target.OriginalDefinition.GetDocumentationCommentId();
        if (id == null) return;

        DirectEdges.Add(new EdgeRecord { From = fromDocId, To = id, Kind = kind, File = _file, Line = line });
    }

    /// <summary>
    /// Syntactic pattern match for `services.AddScoped&lt;IFoo, Foo&gt;()` / AddSingleton / AddTransient, including
    /// the single-type-arg factory-lambda overload. Doesn't validate this is really
    /// Microsoft.Extensions.DependencyInjection's extension method — matching the well-known method names is what
    /// the spec asks for. Assembly-scanning style registration (Scrutor, reflection) can't be resolved statically;
    /// we simply don't record anything for it rather than guessing.
    /// </summary>
    private void TryRecordDiRegistration(InvocationExpressionSyntax node)
    {
        var genericName = (node.Expression as MemberAccessExpressionSyntax)?.Name as GenericNameSyntax
            ?? node.Expression as GenericNameSyntax;
        if (genericName == null) return;
        if (genericName.Identifier.Text is not ("AddScoped" or "AddSingleton" or "AddTransient")) return;

        var typeArgs = genericName.TypeArgumentList.Arguments;
        if (typeArgs.Count == 2)
        {
            var serviceId = ResolveTypeArgId(typeArgs[0]);
            var implId = ResolveTypeArgId(typeArgs[1]);
            if (serviceId != null && implId != null)
                DiRegistrations.Add((serviceId, implId));
            else
                UnresolvedDiRegistrations.Add((_file, LineOf(node), $"Could not resolve the type argument(s) of {genericName.Identifier.Text}<...>()."));
        }
        else if (typeArgs.Count == 1 && node.ArgumentList.Arguments.Count > 0)
        {
            var serviceId = ResolveTypeArgId(typeArgs[0]);
            var implId = FindConstructedTypeIdInFactory(node.ArgumentList.Arguments[0].Expression);
            if (serviceId != null && implId != null)
                DiRegistrations.Add((serviceId, implId));
            else
                UnresolvedDiRegistrations.Add((_file, LineOf(node), $"Could not statically resolve the implementation type constructed by the {genericName.Identifier.Text}<...>(factory) lambda."));
        }
    }

    private string? ResolveTypeArgId(TypeSyntax typeSyntax)
        => (_model.GetSymbolInfo(typeSyntax).Symbol as ITypeSymbol)?.OriginalDefinition.GetDocumentationCommentId();

    /// <summary>Finds the type constructed in a DI factory lambda's tail position (expression body or a `return` statement) — no deeper data-flow analysis.</summary>
    private string? FindConstructedTypeIdInFactory(ExpressionSyntax factoryExpression)
    {
        ExpressionSyntax? tail = factoryExpression switch
        {
            SimpleLambdaExpressionSyntax { ExpressionBody: { } e } => e,
            ParenthesizedLambdaExpressionSyntax { ExpressionBody: { } e } => e,
            SimpleLambdaExpressionSyntax { Block: { } b } => FindReturnExpression(b),
            ParenthesizedLambdaExpressionSyntax { Block: { } b } => FindReturnExpression(b),
            _ => null,
        };

        if (tail is ObjectCreationExpressionSyntax creation &&
            _model.GetSymbolInfo(creation).Symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
        {
            return ctor.ContainingType.OriginalDefinition.GetDocumentationCommentId();
        }

        return null;
    }

    private static ExpressionSyntax? FindReturnExpression(BlockSyntax block)
        => block.Statements.OfType<ReturnStatementSyntax>().FirstOrDefault()?.Expression;

    private static string GetSimpleName(TypeSyntax typeSyntax) => typeSyntax switch
    {
        GenericNameSyntax g => g.Identifier.Text,
        QualifiedNameSyntax q => GetSimpleName(q.Right),
        AliasQualifiedNameSyntax a => GetSimpleName(a.Name),
        SimpleNameSyntax s => s.Identifier.Text,
        _ => typeSyntax.ToString(),
    };

    private static string DisplayName(INamedTypeSymbol symbol) => symbol.ToDisplayString(TypeNameFormat);

    private static int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static List<string> ExtractAttributeNames(SyntaxList<AttributeListSyntax> lists)
    {
        var result = new List<string>();
        foreach (var list in lists)
        foreach (var attr in list.Attributes)
            result.Add(attr.Name.ToString());
        return result;
    }
}
