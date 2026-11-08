using RhiGhAI.Core.Contracts;
using RhiGhAI.Core.Graph;
using Xunit;

namespace RhiGhAI.Tests;

public sealed class FreeFormPlanTests
{
    private static readonly ValidationContext NoSelection = new(new HashSet<string>());

    [Fact]
    public void FreeFormPlanParsesCompilesAndRenders()
    {
        const string json = """
        {"schemaVersion":1,"targetHost":"rhino","summary":"Twisted tower","assumptions":[],"operations":[
        {"kind":"createCircle","id":"base","center":{"x":0,"y":0,"z":0},"normal":{"x":0,"y":0,"z":1},"radius":10,"layer":"Form"},
        {"kind":"copy","id":"top","references":["base"],"translation":{"x":0,"y":0,"z":30},"rotation":{"axis":{"x":0,"y":0,"z":1},"angleDegrees":45,"center":{"x":0,"y":0,"z":0}},"scale":{"factor":0.6,"center":{"x":0,"y":0,"z":0}},"layer":"Form"},
        {"kind":"loft","id":"tower","curveIds":["base","top"],"closed":false,"cap":true,"layer":"Form"},
        {"kind":"filletEdges","id":"soft","brepId":"tower","radius":0.5,"layer":"Form"},
        {"kind":"createCurve","id":"rail","points":[{"x":40,"y":0,"z":0},{"x":40,"y":0,"z":20},{"x":50,"y":0,"z":40}],"closed":false,"layer":"Form"},
        {"kind":"sweep1","id":"swept","railId":"rail","profileIds":["base"],"closed":false,"cap":false,"layer":"Form"},
        {"kind":"pipe","id":"tube","railId":"rail","radius":1.5,"cap":true,"layer":"Form"},
        {"kind":"createArc","id":"arc","start":{"x":100,"y":0,"z":0},"mid":{"x":105,"y":0,"z":10},"end":{"x":100,"y":0,"z":20},"layer":"Form"},
        {"kind":"revolve","id":"vase","profileId":"arc","axisStart":{"x":95,"y":0,"z":0},"axisEnd":{"x":95,"y":0,"z":20},"angleDegrees":360,"layer":"Form"}]}
        """;

        OperationGraph graph = TaskPlanCompiler.Compile(TaskPlanJson.Parse(json), NoSelection);
        string code = CSharpPlanRenderer.Render(graph);

        Assert.Equal(9, graph.Nodes.Length);
        Assert.Equal("Brep", graph.Nodes.Single(node => node.NodeId == "soft").ExpectedResultType);
        Assert.Equal("Curve", graph.Nodes.Single(node => node.NodeId == "arc").ExpectedResultType);
        Assert.True(Index(graph, "top") < Index(graph, "tower") && Index(graph, "tower") < Index(graph, "soft"));
        Assert.Contains("Rotation(45°", code, StringComparison.Ordinal);
        Assert.Contains("Loft(new[] { base, top }", code, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyTransformWithoutRotationOrScaleStillParses()
    {
        const string json = """
        {"schemaVersion":1,"targetHost":"rhino","summary":"x","assumptions":[],"operations":[{"kind":"copy","id":"c","references":["b"],"translation":{"x":1,"y":0,"z":0},"layer":null},{"kind":"createBox","id":"b","origin":{"x":0,"y":0,"z":0},"size":{"x":1,"y":1,"z":1},"layer":"A"}]}
        """;

        CopyOperation copy = Assert.IsType<CopyOperation>(TaskPlanJson.Parse(json).Operations[0]);
        Assert.Null(copy.Rotation);
        Assert.Null(copy.Scale);
        Assert.Equal(2, TaskPlanCompiler.Compile(TaskPlanJson.Parse(json), NoSelection).Nodes.Length);
    }

    public static TheoryData<string, TaskOperation[]> Rejected => new()
    {
        { "WrongReferenceType", [Box("b"), new LoftOperation("l", ["b", "b2"], false, false, "A"), Box("b2")] },
        { "WrongReferenceType", [Circle("c"), new FilletEdgesOperation("f", "c", 1, "A")] },
        { "WrongReferenceType", [Box("b"), new PipeOperation("p", "b", 1, false, "A")] },
        { "WrongReferenceType", [Circle("c"), new Sweep1Operation("s", "c", ["b"], false, false, "A"), Box("b")] },
        { "LoftLimit", [Circle("c"), new LoftOperation("l", ["c"], false, false, "A")] },
        { "SweepLimit", [Circle("c"), new Sweep1Operation("s", "c", [], false, false, "A")] },
        { "InvalidRevolve", [Circle("c"), new RevolveOperation("r", "c", P(0, 0, 0), P(0, 0, 1), 0, "A")] },
        { "InvalidRevolve", [Circle("c"), new RevolveOperation("r", "c", P(0, 0, 0), P(0, 0, 0), 90, "A")] },
        { "InvalidDimension", [Circle("c"), new PipeOperation("p", "c", 0, false, "A")] },
        { "InvalidDimension", [Box("b"), new FilletEdgesOperation("f", "b", -1, "A")] },
        { "InvalidCurve", [new CreateArcOperation("a", P(0, 0, 0), P(1, 1, 1), P(2, 2, 2), "A")] },
        { "InvalidCurve", [new CreateCurveOperation("k", [P(0, 0, 0), P(1, 0, 0)], true, "A")] },
        { "InvalidCurve", [new CreateCircleOperation("c", P(0, 0, 0), new Vector3(0, 0, 0), 5, "A")] },
        { "InvalidTransform", [Box("b"), new CopyOperation("c", ["b"], new Vector3(0, 0, 0), null, null, new ScaleSpec(0, P(0, 0, 0)))] },
        { "InvalidTransform", [Box("b"), new CopyOperation("c", ["b"], new Vector3(0, 0, 0), null, new RotationSpec(new Vector3(0, 0, 0), 30, P(0, 0, 0)))] },
        { "MissingRequiredValue", [Box("b"), new CopyOperation("c", ["b"], new Vector3(0, 0, 0), null, new RotationSpec(null!, 30, P(0, 0, 0)))] },
    };

    [Theory]
    [MemberData(nameof(Rejected))]
    public void InvalidFreeFormOperationsAreRejectedLocally(string code, TaskOperation[] operations)
    {
        TaskPlanEnvelope plan = new(1, TargetHost.Rhino, "bad", [], operations);

        TaskPlanValidationException error = Assert.Throws<TaskPlanValidationException>(() => TaskPlanCompiler.Compile(plan, NoSelection));
        Assert.Equal(code, error.Code);
    }

    private static int Index(OperationGraph graph, string id) => graph.Nodes.Select(node => node.NodeId).ToList().IndexOf(id);
    private static Point3 P(double x, double y, double z) => new(x, y, z);
    private static CreateBoxOperation Box(string id) => new(id, P(0, 0, 0), new Size3(1, 1, 1), "A");
    private static CreateCircleOperation Circle(string id) => new(id, P(0, 0, 0), new Vector3(0, 0, 1), 5, "A");
}
