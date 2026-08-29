using CodeMap.Query.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CodeMap.Roslyn.Scan;

/// <summary>
/// Walks one syntax tree, producing a SymbolRecord for each type/member and edges for inherits/implements
/// (always) plus call/new/read/write and interface/DI mapping data (only when <paramref name="emitDataFlowEdges"/>
/// is true — L2 mode, a real compilation with real references).
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
    private readonly bool _emitDataFlowEdges;
    private readonly string? _diAttributeName;
    private readonly Stack<string> _typeStack = new();
    private readonly Stack<string> _typeIdStack = new();
    private readonly Stack<string> _memberIdStack = new();
    private readonly Stack<string?> _controllerRouteStack = new();

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

    public SyntaxSymbolWalker(SemanticModel model, string project, string file, bool includeExternal, bool emitDataFlowEdges, string? diAttributeName = null)
    {
        _model = model;
        _project = project;
        _file = file;
        _includeExternal = includeExternal;
        _emitDataFlowEdges = emitDataFlowEdges;
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
        if (id != null && _emitDataFlowEdges)
            TryRecordHttpActionEntryPoint(id, node.AttributeLists);
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
        if (_emitDataFlowEdges)
        {
            HandleInvocation(node);
            TryRecordDiRegistration(node);
            TryRecordMediatrSend(node);
        }

        base.VisitInvocationExpression(node);
    }

    public override void VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        if (_emitDataFlowEdges) HandleObjectCreation(node);
        base.VisitObjectCreationExpression(node);
    }

    // Target-typed `new()` (C# 9+, e.g. `OrderRepository _repository = new();`) is a SEPARATE syntax node type
    // from `new Foo()` — both derive from BaseObjectCreationExpressionSyntax, so HandleObjectCreation handles either.
    public override void VisitImplicitObjectCreationExpression(ImplicitObjectCreationExpressionSyntax node)
    {
        if (_emitDataFlowEdges) HandleObjectCreation(node);
        base.VisitImplicitObjectCreationExpression(node);
    }

    public override void VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        if (_emitDataFlowEdges) HandleMemberAccess(node);
        base.VisitMemberAccessExpression(node);
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

        string? controllerRoute = null;
        if (symbol != null && _emitDataFlowEdges && kind is "Class" or "Struct" or "Record")
        {
            RecordInterfaceMappings(symbol);
            TryRecordAttributeDiBinding(symbol);
            if (kind == "Class")
                controllerRoute = RecordTypeLevelEntryPoint(symbol, attrLists);
        }

        if (symbol != null)
        {
            _typeStack.Push(DisplayName(symbol));
            _typeIdStack.Push(symbol.GetDocumentationCommentId() ?? "");
            _controllerRouteStack.Push(controllerRoute);
        }

        visitChildren();

        if (symbol != null)
        {
            _typeStack.Pop();
            _typeIdStack.Pop();
            _controllerRouteStack.Pop();
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
    /// Returns the controller's composed route prefix (for method-level composition), or null if not a controller.
    /// </summary>
    private string? RecordTypeLevelEntryPoint(INamedTypeSymbol type, SyntaxList<AttributeListSyntax> classAttrLists)
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

        var requestTypeId = TryGetMediatrRequestTypeId(type);
        if (requestTypeId != null)
        {
            var handleId = type.GetMembers().OfType<IMethodSymbol>()
                .FirstOrDefault(m => m.Name == "Handle")?.OriginalDefinition.GetDocumentationCommentId();
            if (handleId != null)
            {
                EntryPoints.Add(new EntryPoint(handleId, "handler"));
                MediatrHandlerMappings.Add((requestTypeId, handleId));
            }
        }

        if (!IsControllerType(type)) return null;

        var routeTemplate = ExtractRouteTemplate(classAttrLists) ?? "";
        var controllerName = type.Name.EndsWith("Controller", StringComparison.Ordinal) ? type.Name[..^"Controller".Length] : type.Name;
        return routeTemplate.Replace("[controller]", controllerName.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsControllerType(INamedTypeSymbol type)
    {
        for (var t = type.BaseType; t != null; t = t.BaseType)
            if (t.Name is "ControllerBase" or "Controller") return true;
        return false;
    }

    private static bool IsBackgroundServiceType(INamedTypeSymbol type)
    {
        for (var t = type.BaseType; t != null; t = t.BaseType)
            if (t.Name == "BackgroundService") return true;
        return false;
    }

    private static bool ImplementsIHostedService(INamedTypeSymbol type)
        => type.AllInterfaces.Any(i => i.Name == "IHostedService");

    /// <summary>If <paramref name="type"/> implements MediatR's IRequestHandler&lt;,&gt;/IRequestHandler&lt;&gt;/INotificationHandler&lt;&gt;, returns the docId of the request/notification type argument.</summary>
    private static string? TryGetMediatrRequestTypeId(INamedTypeSymbol type)
    {
        foreach (var iface in type.AllInterfaces)
        {
            var isRequestHandler = iface.Name == "IRequestHandler" && iface.TypeArguments.Length is 1 or 2;
            var isNotificationHandler = iface.Name == "INotificationHandler" && iface.TypeArguments.Length == 1;
            if (!isRequestHandler && !isNotificationHandler) continue;

            var id = iface.TypeArguments[0].OriginalDefinition.GetDocumentationCommentId();
            if (id != null) return id;
        }

        return null;
    }

    private static string? ExtractRouteTemplate(SyntaxList<AttributeListSyntax> attrLists)
    {
        foreach (var list in attrLists)
        foreach (var attr in list.Attributes)
        {
            if (attr.Name.ToString() is not ("Route" or "RouteAttribute")) continue;
            if (attr.ArgumentList?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax { Token.Value: string s })
                return s;
        }

        return null;
    }

    private static readonly Dictionary<string, string> HttpVerbAttributeNames = new(StringComparer.Ordinal)
    {
        ["HttpGet"] = "GET", ["HttpGetAttribute"] = "GET",
        ["HttpPost"] = "POST", ["HttpPostAttribute"] = "POST",
        ["HttpPut"] = "PUT", ["HttpPutAttribute"] = "PUT",
        ["HttpDelete"] = "DELETE", ["HttpDeleteAttribute"] = "DELETE",
        ["HttpPatch"] = "PATCH", ["HttpPatchAttribute"] = "PATCH",
        ["HttpHead"] = "HEAD", ["HttpHeadAttribute"] = "HEAD",
        ["HttpOptions"] = "OPTIONS", ["HttpOptionsAttribute"] = "OPTIONS",
    };

    private void TryRecordHttpActionEntryPoint(string methodId, SyntaxList<AttributeListSyntax> attrLists)
    {
        if (_controllerRouteStack.Count == 0) return;
        var controllerRoute = _controllerRouteStack.Peek();
        if (controllerRoute == null) return; // not inside a recognized controller type

        foreach (var list in attrLists)
        foreach (var attr in list.Attributes)
        {
            if (!HttpVerbAttributeNames.TryGetValue(attr.Name.ToString(), out var httpMethod)) continue;

            var methodRoute = attr.ArgumentList?.Arguments.FirstOrDefault()?.Expression is LiteralExpressionSyntax { Token.Value: string s } ? s : null;
            var route = ComposeRoute(controllerRoute, methodRoute);
            EntryPoints.Add(new EntryPoint(methodId, "http", httpMethod, route));
            return; // one HTTP verb per action method is the common case
        }
    }

    private static string ComposeRoute(string controllerRoute, string? methodRoute)
    {
        var combined = string.IsNullOrEmpty(methodRoute)
            ? controllerRoute
            : $"{controllerRoute.TrimEnd('/')}/{methodRoute.TrimStart('/')}";
        return combined.Trim('/');
    }

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

        var constructedTypeId = node.ArgumentList.Arguments[0].Expression switch
        {
            ObjectCreationExpressionSyntax oce when _model.GetSymbolInfo(oce).Symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor
                => ctor.ContainingType.OriginalDefinition.GetDocumentationCommentId(),
            ImplicitObjectCreationExpressionSyntax ioce when _model.GetSymbolInfo(ioce).Symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor
                => ctor.ContainingType.OriginalDefinition.GetDocumentationCommentId(),
            _ => null,
        };
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
        if (_model.GetSymbolInfo(node).Symbol is not IMethodSymbol method) return; // unresolved overload/dynamic — don't guess
        EmitMemberEdge(method, "call", LineOf(node));
    }

    private void HandleObjectCreation(BaseObjectCreationExpressionSyntax node)
    {
        if (_model.GetSymbolInfo(node).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor) return;
        EmitMemberEdge(ctor, "new", LineOf(node));
    }

    private void HandleMemberAccess(MemberAccessExpressionSyntax node)
    {
        var symbol = _model.GetSymbolInfo(node).Symbol;
        if (symbol is not (IPropertySymbol or IFieldSymbol)) return; // spec: only property/field member accesses

        var kind = node.Parent is AssignmentExpressionSyntax assign && assign.Left == node ? "write" : "read";
        EmitMemberEdge(symbol, kind, LineOf(node));
    }

    private void EmitMemberEdge(ISymbol target, string kind, int line)
    {
        var declaredInSolution = target.Locations.Any(l => l.IsInSource);
        if (!declaredInSolution && !_includeExternal) return;

        var fromId = CurrentFromId();
        if (fromId == null) return;

        var toId = target.OriginalDefinition.GetDocumentationCommentId();
        if (toId == null) return;

        DirectEdges.Add(new EdgeRecord { From = fromId, To = toId, Kind = kind, File = _file, Line = line });
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
