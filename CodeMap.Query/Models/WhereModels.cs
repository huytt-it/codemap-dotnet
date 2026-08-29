namespace CodeMap.Query.Models;

/// <summary>A `where` result — spec section 3: "Output là danh sách ứng viên kèm docId và lý do được chọn".</summary>
public sealed record WhereCandidate(string SymbolId, string DisplayName, double Score, List<string> Reasons);
