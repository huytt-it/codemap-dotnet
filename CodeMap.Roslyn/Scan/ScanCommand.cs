using CodeMap.Query.ArgParsing;

namespace CodeMap.Roslyn.Scan;

internal static class ScanCommand
{
    public static int Run(string[] rawArgs)
    {
        var args = Args.Parse(rawArgs, options: new[] { "solution", "out" }, flags: new[] { "syntax-only", "include-external" });
        var solutionPath = Path.GetFullPath(args.Require("solution"));
        var outDir = args.Require("out");
        var syntaxOnly = args.HasFlag("syntax-only");
        var includeExternal = args.HasFlag("include-external");

        if (!File.Exists(solutionPath))
        {
            Console.Error.WriteLine($"Solution not found: {solutionPath}");
            return 1;
        }

        if (syntaxOnly)
        {
            // L1 uses only CSharpSyntaxTree/CSharpCompilation (Roslyn compiler API) — never touches
            // MSBuildWorkspace, so MSBuildLocator registration is neither needed nor performed here.
            new SyntaxOnlyScanner(includeExternal).Scan(solutionPath, outDir);
        }
        else
        {
            // Lazy MSBuild init (spec section 2): only this branch ever needs Build Tools installed.
            MsBuildBootstrap.Register();
            new SemanticScanner(includeExternal).Scan(solutionPath, outDir);
        }

        return 0;
    }
}
