using RhiGhAI.Core.Contracts;
using RhiGhAI.Core.Graph;
using Xunit;

namespace RhiGhAI.Tests;

public sealed class GhCatalogRankingTests
{
    // A stock-sized catalogue: ~900 core components that sort alphabetically before the ones that
    // matter, so nothing but relevance can lift Loft/Sweep1/Pipe into the top 240.
    private static readonly GhComponentSpec[] Catalog =
    [
        .. Enumerable.Range(0, 900).Select(index => Spec($"Aaa Filler {index:D3}", "Curve", "Generic helper component.")),
        Spec("Loft", "Surface", "Create a lofted surface through a set of section curves."),
        Spec("Sweep1", "Surface", "Create a sweep surface with one rail curve."),
        Spec("Pipe", "Surface", "Create a pipe surface around a rail curve."),
        Spec("Graph Mapper", "Params", "Represents a numeric mapping function."),
        Spec("Sine", "Maths", "Compute the sine of a value."),
        Spec("Solver", "Kangaroo2", "Iteratively solves goals."),
        Spec("Anchor", "Kangaroo2", "Fix a point in place.")
    ];

    [Theory]
    [InlineData("Сделай лофт через три кривые")]
    [InlineData("Протяни трубу по волне")]
    [InlineData("Параметрическая башня в стиле Захи Хадид")]
    public void RussianPromptKeepsFormMakingComponents(string prompt)
    {
        HashSet<string> names = GhCatalogRanking.Rank(Catalog, prompt, 240).Select(spec => spec.Name).ToHashSet();

        Assert.Contains("Loft", names);
        Assert.Contains("Sweep1", names);
        Assert.Contains("Pipe", names);
        Assert.Contains("Graph Mapper", names);
    }

    [Fact]
    public void RussianTermsMapToTheirComponentsFirst()
    {
        List<string> top = GhCatalogRanking.Rank(Catalog, "труба по волне", 240).Select(spec => spec.Name).Take(3).ToList();

        Assert.Contains("Pipe", top);
        Assert.Contains("Sine", top);
    }

    [Fact]
    public void KangarooIsReachableFromRussianPrompt()
    {
        HashSet<string> names = GhCatalogRanking.Rank(Catalog, "натяжная мембрана", 240).Select(spec => spec.Name).ToHashSet();

        Assert.Contains("Solver", names);
        Assert.Contains("Anchor", names);
    }

    private static GhComponentSpec Spec(string name, string category, string description) =>
        new(Guid.NewGuid(), name, name, category, "Sub", description, [], []);
}
