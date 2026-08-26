using RhiGhAI.Core.Contracts;
using RhiGhAI.Core.Graph;
using Xunit;

namespace RhiGhAI.Tests;

public sealed class GhGraphTests
{
    private static readonly FakeCatalog Catalog = new(
        Spec("Number Slider", []),
        Spec("Circle", ["Plane", "Radius"], ["Circle"]),
        Spec("Extrude", ["Base", "Direction"], ["Extrusion"]),
        Spec("Unit Z", ["Factor"], ["Unit vector"]));

    [Fact]
    public void ValidGraphCompilesLaysOutAndRenders()
    {
        GhGraphEnvelope graph = new(
            1,
            "Цилиндр из окружности",
            ["Радиус управляется слайдером."],
            [
                new GhNodeSpec("radius", "Number Slider", [new GhValueSpec("min", "1"), new GhValueSpec("max", "500"), new GhValueSpec("value", "200")]),
                new GhNodeSpec("circle", "Circle", [new GhValueSpec("Plane", "xy")]),
                new GhNodeSpec("height", "Unit Z", [new GhValueSpec("Factor", "3000")]),
                new GhNodeSpec("solid", "Extrude", [])
            ],
            [
                new GhWireSpec("radius", "Number Slider", "circle", "Radius"),
                new GhWireSpec("circle", "Circle", "solid", "Base"),
                new GhWireSpec("height", "Unit vector", "solid", "Direction")
            ]);

        GhGraphPlan plan = GhGraphCompiler.Compile(graph, Catalog);

        Assert.Equal(4, plan.Nodes.Count);
        Assert.Equal(3, plan.Wires.Count);
        // Columns follow the longest path, so Extrude sits behind both of its sources.
        Assert.Equal(0, Node(plan, "radius").Column);
        Assert.Equal(1, Node(plan, "circle").Column);
        Assert.Equal(2, Node(plan, "solid").Column);
        Assert.Equal(0, Node(plan, "circle").Values[0].PortIndex);
        Assert.Contains("solid = Extrude", GhGraphCompiler.Render(plan));
    }

    [Fact]
    public void LiteralOnAWiredPortIsDropped()
    {
        GhGraphEnvelope graph = new(
            1,
            "Радиус приходит по проводу",
            [],
            [
                new GhNodeSpec("radius", "Number Slider", [new GhValueSpec("min", "1"), new GhValueSpec("max", "10"), new GhValueSpec("value", "5")]),
                new GhNodeSpec("circle", "Circle", [new GhValueSpec("Radius", "12")])
            ],
            [new GhWireSpec("radius", "Number Slider", "circle", "Radius")]);

        GhGraphPlan plan = GhGraphCompiler.Compile(graph, Catalog);

        Assert.Empty(Node(plan, "circle").Values);
    }

    [Theory]
    [InlineData("Cirlce", "Radius", "UnknownComponent")]
    [InlineData("Circle", "Diameter", "UnknownInputPort")]
    public void UnknownNamesAreRejectedWithTheirCode(string component, string port, string expectedCode)
    {
        GhGraphEnvelope graph = new(
            1,
            "Ошибка",
            [],
            [new GhNodeSpec("a", component, [new GhValueSpec(port, "1")])],
            []);

        TaskPlanValidationException error = Assert.Throws<TaskPlanValidationException>(() => GhGraphCompiler.Compile(graph, Catalog));
        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public void CyclesAreRejectedBeforeAnythingIsEmitted()
    {
        GhGraphEnvelope graph = new(
            1,
            "Цикл",
            [],
            [
                new GhNodeSpec("a", "Circle", []),
                new GhNodeSpec("b", "Extrude", [])
            ],
            [
                new GhWireSpec("a", "Circle", "b", "Base"),
                new GhWireSpec("b", "Extrusion", "a", "Plane")
            ]);

        TaskPlanValidationException error = Assert.Throws<TaskPlanValidationException>(() => GhGraphCompiler.Compile(graph, Catalog));
        Assert.Equal("CyclicGraph", error.Code);
    }

    [Theory]
    [InlineData("10", "1", "5")]
    [InlineData("1", "10", "50")]
    [InlineData("1", "10", "abc")]
    public void ImpossibleSlidersAreRejected(string minimum, string maximum, string value)
    {
        GhGraphEnvelope graph = new(
            1,
            "Слайдер",
            [],
            [
                new GhNodeSpec(
                    "s",
                    "Number Slider",
                    [new GhValueSpec("min", minimum), new GhValueSpec("max", maximum), new GhValueSpec("value", value)])
            ],
            []);

        TaskPlanValidationException error = Assert.Throws<TaskPlanValidationException>(() => GhGraphCompiler.Compile(graph, Catalog));
        Assert.Equal("InvalidSlider", error.Code);
    }

    [Fact]
    public void SchemaMatchesTheContractShape()
    {
        string schema = GhGraphJson.OutputSchema.GetRawText();

        Assert.Contains("\"schemaVersion\"", schema);
        Assert.DoesNotContain("oneOf", schema);
        GhGraphEnvelope parsed = GhGraphJson.ParseGraph(
            """
            {"schemaVersion":1,"summary":"s","assumptions":[],"nodes":[{"id":"a","component":"Circle","values":[]}],"wires":[]}
            """);
        Assert.Equal("Circle", Assert.Single(parsed.Nodes).Component);
    }

    [Theory]
    [InlineData("два")]
    [InlineData("1.5")]
    [InlineData("9")]
    public void SliderDecimalsMustBeASmallWholeNumber(string decimals)
    {
        // Never parsed by the compiler, yet the emitter ran decimal.Parse over it: a FormatException
        // there is not a TaskPlanValidationException, so no repair attempt was ever spent on it.
        GhGraphEnvelope graph = Slider([
            new GhValueSpec("min", "0"),
            new GhValueSpec("max", "10"),
            new GhValueSpec("value", "5"),
            new GhValueSpec("decimals", decimals)
        ]);

        TaskPlanValidationException error = Assert.Throws<TaskPlanValidationException>(() => GhGraphCompiler.Compile(graph, Catalog));
        Assert.Equal("InvalidSlider", error.Code);
    }

    [Fact]
    public void SliderBoundsBeyondDecimalRangeAreRejected()
    {
        // 1e30 is a finite double and passes min < max, then overflows decimal inside Grasshopper.
        GhGraphEnvelope graph = Slider([new GhValueSpec("min", "0"), new GhValueSpec("max", "1e30")]);

        TaskPlanValidationException error = Assert.Throws<TaskPlanValidationException>(() => GhGraphCompiler.Compile(graph, Catalog));
        Assert.Equal("InvalidSlider", error.Code);
    }

    [Fact]
    public void RepeatedSliderSettingsAreRejected()
    {
        // The checks read the first "min" and the emitter keeps the last, so this used to validate
        // min=0/max=100 and then emit Minimum=200 with Maximum=100.
        GhGraphEnvelope graph = Slider([
            new GhValueSpec("min", "0"),
            new GhValueSpec("min", "200"),
            new GhValueSpec("max", "100"),
            new GhValueSpec("value", "50")
        ]);

        TaskPlanValidationException error = Assert.Throws<TaskPlanValidationException>(() => GhGraphCompiler.Compile(graph, Catalog));
        Assert.Equal("DuplicateValue", error.Code);
    }

    [Fact]
    public void MoreValuesThanTheComponentHasInputsAreRejected()
    {
        GhGraphEnvelope graph = new(
            1,
            "Слишком много значений",
            [],
            [new GhNodeSpec("c", "Circle", [
                new GhValueSpec("Plane", "xy"),
                new GhValueSpec("Radius", "5"),
                new GhValueSpec("Plane", "xz")
            ])],
            []);

        TaskPlanValidationException error = Assert.Throws<TaskPlanValidationException>(() => GhGraphCompiler.Compile(graph, Catalog));
        Assert.Equal("ValueLimit", error.Code);
    }

    [Fact]
    public void ASinglePortComponentShowsWhichPortTheModelAskedFor()
    {
        // "Unit Z" has one input, so any name resolves to it. The render is the only place the user
        // can see that the model meant something else.
        GhGraphEnvelope graph = new(
            1,
            "Опечатка в имени порта",
            [],
            [new GhNodeSpec("height", "Unit Z", [new GhValueSpec("Length", "3000")])],
            []);

        GhGraphPlan plan = GhGraphCompiler.Compile(graph, Catalog);

        Assert.Equal("Factor", Node(plan, "height").Values[0].PortName);
        Assert.Contains("Factor (запрошен «Length»)", GhGraphCompiler.Render(plan), StringComparison.Ordinal);
    }

    private static GhGraphEnvelope Slider(GhValueSpec[] values) =>
        new(1, "Слайдер", [], [new GhNodeSpec("s", "Number Slider", values)], []);

    private static GhResolvedNode Node(GhGraphPlan plan, string id) => plan.Nodes.Single(node => node.Id == id);

    private static GhComponentSpec Spec(string name, string[] inputs, string[]? outputs = null) => new(
        Guid.NewGuid(),
        name,
        name,
        "Test",
        "Test",
        name,
        [.. inputs.Select(port => new GhPortSpec(port, port[..1]))],
        [.. (outputs ?? [name]).Select(port => new GhPortSpec(port, port[..1]))]);

    private sealed class FakeCatalog(params GhComponentSpec[] specs) : IGhCatalog
    {
        public bool TryFind(string name, out GhComponentSpec spec)
        {
            spec = specs.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase))!;
            return spec is not null;
        }

        public IReadOnlyList<string> Suggest(string name, int count) => [.. specs.Select(spec => spec.Name).Take(count)];
    }
}
