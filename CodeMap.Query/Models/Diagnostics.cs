namespace CodeMap.Query.Models;

/// <summary>
/// diagnostics.json — records everything static analysis couldn't resolve, instead of guessing or silently
/// dropping it. More lists get added phase by phase (unresolvedAssemblyScanning, unmatchedFrontendCalls, ...).
/// </summary>
public sealed class DiagnosticsModel
{
    public List<DegradedProject> DegradedProjects { get; init; } = new();
    public List<UnresolvedInheritance> UnresolvedInheritance { get; init; } = new();
    public List<DuplicateDocId> DuplicateDocIdsAcrossProjects { get; init; } = new();
    public List<UnresolvedDiRegistration> UnresolvedDiRegistrations { get; init; } = new();
    public List<AmbiguousDiType> AmbiguousDiTypes { get; init; } = new();
    public List<DiRegistrationConflict> DiRegistrationConflicts { get; init; } = new();
    public List<UnparsedFrontendUrl> UnparsedFrontendUrls { get; init; } = new();
    public List<UnmatchedFrontendCall> UnmatchedFrontendCalls { get; init; } = new();
    public List<string> UnreferencedEndpoints { get; init; } = new();
}

public sealed record DegradedProject(string Project, string Reason);

public sealed record UnresolvedInheritance(string Project, string File, int Line, string FromTypeDocId, string BaseTypeName, string Reason);

/// <summary>A recognized AddScoped/AddSingleton/AddTransient call whose service/implementation type could not be statically resolved (e.g. assembly-scanning style registration via Scrutor/reflection).</summary>
public sealed record UnresolvedDiRegistration(string Project, string File, int Line, string Reason);

/// <summary>
/// Two different types (in two different projects/assemblies) whose ISymbol.GetDocumentationCommentId() produces
/// the SAME string — an inherent limitation of docId (it doesn't encode the assembly name), not an extraction
/// bug. Recorded transparently here instead of letting find/impact silently conflate the two types.
/// </summary>
public sealed record DuplicateDocId(string Id, List<string> Projects, List<string> Files);

/// <summary>A type marked with the configured DI attribute (spec section 5, P10) but implementing 2+ real interfaces — the "implement exactly 1" rule can't pick one. Resolve via `diManualOverrides` in codemap.config.json.</summary>
public sealed record AmbiguousDiType(string TypeDocId, List<string> CandidateInterfaces);

/// <summary>The attribute-convention DI source and the fluent AddScoped/AddSingleton/AddTransient source disagree about which interface a type binds to.</summary>
public sealed record DiRegistrationConflict(string TypeDocId, string AttributeBoundInterface, List<string> FluentBoundInterfaces);

/// <summary>`scan-fe` found an HTTP call (Angular or jQuery) but couldn't resolve its URL to a normalizable path — spec section 6: "Ghi những chỗ không parse được URL vào diagnostics.json chứ đừng bỏ im lặng".</summary>
public sealed record UnparsedFrontendUrl(string File, int Line, string HttpMethod, string RawUrl, string Reason);

/// <summary>`link` found no backend endpoint whose (httpMethod, normalized route) matches this frontend call — spec section 6: "Danh sách này tự nó có giá trị: nó là endpoint chết hoặc là chỗ tool parse sai".</summary>
public sealed record UnmatchedFrontendCall(string FrontendId, string HttpMethod, string NormalizedRoute);
