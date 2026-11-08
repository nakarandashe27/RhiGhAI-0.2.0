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
[JsonDerivedType(typeof(CreateCurveOperation), "createCurve")]
[JsonDerivedType(typeof(CreateCircleOperation), "createCircle")]
[JsonDerivedType(typeof(CreateArcOperation), "createArc")]
[JsonDerivedType(typeof(LoftOperation), "loft")]
[JsonDerivedType(typeof(Sweep1Operation), "sweep1")]
[JsonDerivedType(typeof(PipeOperation), "pipe")]
[JsonDerivedType(typeof(RevolveOperation), "revolve")]
[JsonDerivedType(typeof(FilletEdgesOperation), "filletEdges")]
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

/// <summary>Rotation about an axis through <paramref name="Center"/>; positive angles follow the right-hand rule.</summary>
public sealed record RotationSpec(Vector3 Axis, double AngleDegrees, Point3 Center);
public sealed record ScaleSpec(double Factor, Point3 Center);

// Applied as scale, then rotation, then translation. Rotation and scale are optional so plans
// written before they existed keep their meaning.
public sealed record TransformOperation(string Id, IReadOnlyList<string> References, Vector3 Translation, string? Layer, RotationSpec? Rotation = null, ScaleSpec? Scale = null) : TaskOperation(Id);
public sealed record CopyOperation(string Id, IReadOnlyList<string> References, Vector3 Translation, string? Layer, RotationSpec? Rotation = null, ScaleSpec? Scale = null) : TaskOperation(Id);
public sealed record SetAttributesOperation(string Id, IReadOnlyList<string> References, string Layer) : TaskOperation(Id);

/// <summary>Smooth degree-3 curve through the points; closed curves are periodic.</summary>
public sealed record CreateCurveOperation(string Id, IReadOnlyList<Point3> Points, bool Closed, string Layer) : TaskOperation(Id);
public sealed record CreateCircleOperation(string Id, Point3 Center, Vector3 Normal, double Radius, string Layer) : TaskOperation(Id);
public sealed record CreateArcOperation(string Id, Point3 Start, Point3 Mid, Point3 End, string Layer) : TaskOperation(Id);
public sealed record LoftOperation(string Id, IReadOnlyList<string> CurveIds, bool Closed, bool Cap, string Layer) : TaskOperation(Id);
public sealed record Sweep1Operation(string Id, string RailId, IReadOnlyList<string> ProfileIds, bool Closed, bool Cap, string Layer) : TaskOperation(Id);
public sealed record PipeOperation(string Id, string RailId, double Radius, bool Cap, string Layer) : TaskOperation(Id);
public sealed record RevolveOperation(string Id, string ProfileId, Point3 AxisStart, Point3 AxisEnd, double AngleDegrees, string Layer) : TaskOperation(Id);
/// <summary>Fillets every edge of one Brep produced earlier in the same plan.</summary>
public sealed record FilletEdgesOperation(string Id, string BrepId, double Radius, string Layer) : TaskOperation(Id);
