using System.Globalization;
using System.Text.Json;
using RhiGhAI.Core.Contracts;
using RhiGhAI.Core.Graph;
using Xunit;

namespace RhiGhAI.Tests;

public sealed class TaskPlanTests
{
    [Fact]
    public void StrictParserAndCompilerProduceStableGraphAndCode()
    {
        const string json = """
        {
          "schemaVersion":1,
          "targetHost":"rhino",
          "summary":"Панель",
          "assumptions":[],
          "operations":[
            {"kind":"createBox","id":"panel","origin":{"x":0,"y":0,"z":0},"size":{"x":2400,"y":1200,"z":18},"layer":"Panels"},
            {"kind":"ensureLayer","id":"layer","name":"Panels"}
          ]
        }
        """;

        TaskPlanEnvelope plan = TaskPlanJson.Parse(json);
        OperationGraph graph = TaskPlanCompiler.Compile(plan, new ValidationContext(new HashSet<string>()));
        string invariantCode = CSharpPlanRenderer.Render(graph);
        using CultureScope _ = new("ru-RU");
        OperationGraph repeated = TaskPlanCompiler.Compile(plan, new ValidationContext(new HashSet<string>()));

        Assert.Equal(graph.GraphHash, repeated.GraphHash);
        Assert.Contains("2400", invariantCode, StringComparison.Ordinal);
        Assert.DoesNotContain("2400,0", invariantCode, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownFieldFailsClosed()
    {
        const string json = """
        {"schemaVersion":1,"targetHost":"rhino","summary":"x","assumptions":[],"operations":[{"kind":"ensureLayer","id":"layer","name":"A","extra":true}]}
        """;

        Assert.Throws<JsonException>(() => TaskPlanJson.Parse(json));
    }

    [Fact]
    public void ExistingObjectMustBeInAllowedSnapshotReferences()
    {
        TaskPlanEnvelope plan = new(
            1,
            TargetHost.Rhino,
            "move",
            [],
            [new TransformOperation("move", ["selection:missing"], new Vector3(0, 0, 500), null)]);

        TaskPlanValidationException error = Assert.Throws<TaskPlanValidationException>(() =>
            TaskPlanCompiler.Compile(plan, new ValidationContext(new HashSet<string>())));
        Assert.Equal("UnknownReference", error.Code);
    }

    [Fact]
    public void TaskPlanRejectsGrasshopperTarget()
    {
        TaskPlanEnvelope plan = new(
            1,
            TargetHost.Grasshopper,
            "columns",
            [],
            [new EnsureLayerOperation("a", "Columns")]);

        TaskPlanValidationException error = Assert.Throws<TaskPlanValidationException>(() =>
            TaskPlanCompiler.Compile(plan, new ValidationContext(new HashSet<string>())));
        Assert.Equal("WrongTargetHost", error.Code);
    }

    [Fact]
    public void TransformAcceptsOnlyCurrentSelection()
    {
        const string reference = "selection:11111111-1111-1111-1111-111111111111";
        TaskPlanEnvelope plan = new(
            1,
            TargetHost.Rhino,
            "move",
            [],
            [new TransformOperation("move", [reference], new Vector3(0, 0, 500), "Raised")]);

        OperationGraph graph = TaskPlanCompiler.Compile(plan, new ValidationContext(new HashSet<string> { reference }));

        Assert.Single(graph.Nodes);
        Assert.Equal("transform", graph.Nodes[0].OperationKind);
    }

    [Fact]
    public void InvalidLayerIsRejectedLocally()
    {
        TaskPlanEnvelope plan = new(
            1,
            TargetHost.Rhino,
            "box",
            [],
            [new CreateBoxOperation("box", new Point3(0, 0, 0), new Size3(1, 1, 1), "Bad/Layer")]);

        TaskPlanValidationException error = Assert.Throws<TaskPlanValidationException>(() =>
            TaskPlanCompiler.Compile(plan, new ValidationContext(new HashSet<string>())));
        Assert.Equal("InvalidDimension", error.Code);
    }

    [Fact]
    public void DependencyGeometryTypeIsCheckedBeforeExecution()
    {
        TaskPlanEnvelope plan = new(
            1,
            TargetHost.Rhino,
            "invalid extrusion",
            [],
            [
                new CreateBoxOperation("solid", new Point3(0, 0, 0), new Size3(1, 1, 1), "A"),
                new ExtrudeOperation("extrude", "solid", new Vector3(0, 0, 10), true, "A")
            ]);

        TaskPlanValidationException error = Assert.Throws<TaskPlanValidationException>(() =>
            TaskPlanCompiler.Compile(plan, new ValidationContext(new HashSet<string>())));
        Assert.Equal("WrongReferenceType", error.Code);
    }

    [Fact]
    public void OutOfBoundsCoordinateIsRejectedLocally()
    {
        TaskPlanEnvelope plan = new(
            1,
            TargetHost.Rhino,
            "far box",
            [],
            [new CreateBoxOperation("box", new Point3(1_000_001, 0, 0), new Size3(1, 1, 1), "A")]);

        Assert.Throws<TaskPlanValidationException>(() =>
            TaskPlanCompiler.Compile(plan, new ValidationContext(new HashSet<string>())));
    }

    [Fact]
    public void CyclicGraphIsRejected()
    {
        TaskPlanEnvelope plan = new(
            1,
            TargetHost.Rhino,
            "cycle",
            [],
            [
                new BooleanOperation("a", BooleanMode.Union, ["b", "c"], "A"),
                new BooleanOperation("b", BooleanMode.Union, ["a", "c"], "A"),
                new CreateBoxOperation("c", new Point3(0, 0, 0), new Size3(1, 1, 1), "A")
            ]);

        TaskPlanValidationException error = Assert.Throws<TaskPlanValidationException>(() =>
            TaskPlanCompiler.Compile(plan, new ValidationContext(new HashSet<string>())));
        Assert.Equal("CyclicGraph", error.Code);
    }

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _previous = CultureInfo.CurrentCulture;
        private readonly CultureInfo _previousUi = CultureInfo.CurrentUICulture;

        public CultureScope(string name)
        {
            CultureInfo culture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _previous;
            CultureInfo.CurrentUICulture = _previousUi;
        }
    }
}
