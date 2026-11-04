using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace NodARCH.AcadMcp.Tests;

/// <summary>
/// The add-in is one DLL for vanilla AutoCAD and AutoCAD Architecture, so no class in it may need
/// an ACA assembly just to load. Most ACA references are safe: the runtime resolves them only
/// when the method naming them runs, and the ACA tools never run without ACA. A generic
/// constraint or a base type is different, it is resolved when the class loads. One generic
/// constraint naming an ACA type once made vanilla AutoCAD drop every tool, the commands and the
/// ribbon. This reads the add-in's source with ACA defined, so it runs anywhere, CI included.
/// </summary>
public class AcaLoadSafetyTests
{
    [Fact]
    public void No_class_needs_an_aca_assembly_to_load()
    {
        var files = AddinSources();
        Assert.NotEmpty(files);

        var problems = new List<string>();
        foreach (var file in files)
        {
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file),
                new CSharpParseOptions(preprocessorSymbols: ["ACA", "ROSLYN"]), file).GetCompilationUnitRoot();

            // The add-in names ACA types only through aliases (the files explain why), so an
            // alias of an Autodesk.Aec type is what an ACA type looks like here.
            var aliases = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
                .Where(u => u.Alias is not null && u.NamespaceOrType.ToString().StartsWith("Autodesk.Aec", StringComparison.Ordinal))
                .Select(u => u.Alias!.Name.Identifier.Text)
                .ToHashSet(StringComparer.Ordinal);

            bool NamesAca(TypeSyntax type) =>
                type.ToString().Contains("Autodesk.Aec", StringComparison.Ordinal) ||
                type.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(n => aliases.Contains(n.Identifier.Text));

            void Check(TypeSyntax type, string what)
            {
                if (NamesAca(type))
                    problems.Add($"{Path.GetFileName(file)}:{type.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {what} {type}");
            }

            foreach (var clause in root.DescendantNodes().OfType<TypeParameterConstraintClauseSyntax>())
                foreach (var constraint in clause.Constraints.OfType<TypeConstraintSyntax>())
                    Check(constraint.Type, "generic constraint names");

            foreach (var baseList in root.DescendantNodes().OfType<BaseListSyntax>())
                foreach (var baseType in baseList.Types)
                    Check(baseType.Type, "base type is");
        }

        Assert.True(problems.Count == 0,
            "These make their class fail to load on vanilla AutoCAD, and its tools with it:\n" + string.Join("\n", problems));
    }

    private static List<string> AddinSources()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "NodARCH.AcadMcp.Addin")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src", "NodARCH.AcadMcp.Addin"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"))
            .ToList();
    }
}
