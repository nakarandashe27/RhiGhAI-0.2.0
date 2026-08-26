using System.Text.Json.Serialization;

namespace RhiGhAI.Core.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<TargetHost>))]
public enum TargetHost
{
    Rhino,
    Grasshopper
}

public sealed record Point3(double X, double Y, double Z);
public sealed record Vector3(double X, double Y, double Z);
public sealed record Size3(double X, double Y, double Z);

public sealed record TaskPlanEnvelope(
    int SchemaVersion,
    TargetHost TargetHost,
    string Summary,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<TaskOperation> Operations);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(EnsureLayerOperation), "ensureLayer")]
[JsonDerivedType(typeof(CreateBoxOperation), "createBox")]
[JsonDerivedType(typeof(CreatePolylineOperation), "createPolyline")]
[JsonDerivedType(typeof(CreatePlanarSurfaceOperation), "createPlanarSurface")]
[JsonDerivedType(typeof(ExtrudeOperation), "extrude")]
[JsonDerivedType(typeof(BooleanOperation), "boolean")]
[JsonDerivedType(typeof(TransformOperation), "transform")]
[JsonDerivedType(typeof(CopyOperation), "copy")]
[JsonDerivedType(typeof(SetAttributesOperation), "setAttributes")]
public abstract record TaskOperation(string Id);

public sealed record EnsureLayerOperation(string Id, string Name) : TaskOperation(Id);
public sealed record CreateBoxOperation(string Id, Point3 Origin, Size3 Size, string Layer) : TaskOperation(Id);
public sealed record CreatePolylineOperation(string Id, IReadOnlyList<Point3> Points, bool Closed, string Layer) : TaskOperation(Id);
public sealed record CreatePlanarSurfaceOperation(string Id, string BoundaryId, string Layer) : TaskOperation(Id);
public sealed record ExtrudeOperation(string Id, string ProfileId, Vector3 Direction, bool Cap, string Layer) : TaskOperation(Id);

[JsonConverter(typeof(JsonStringEnumConverter<BooleanMode>))]
public enum BooleanMode
{
    Union,
    Difference,
    Intersection
}

public sealed record BooleanOperation(string Id, BooleanMode Mode, IReadOnlyList<string> InputIds, string Layer) : TaskOperation(Id);
public sealed record TransformOperation(string Id, IReadOnlyList<string> References, Vector3 Translation, string? Layer) : TaskOperation(Id);
public sealed record CopyOperation(string Id, IReadOnlyList<string> References, Vector3 Translation, string? Layer) : TaskOperation(Id);
public sealed record SetAttributesOperation(string Id, IReadOnlyList<string> References, string Layer) : TaskOperation(Id);
