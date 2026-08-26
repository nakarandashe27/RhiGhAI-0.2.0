using Rhino;
using Rhino.Geometry;

namespace RhiGhAI.Rhino.Execution;

public sealed record SelectedObjectSnapshot(
    string ReferenceId,
    Guid ObjectId,
    string ObjectType,
    string Layer,
    uint GeometryCrc,
    string AttributesFingerprint,
    BoundingBox Bounds);

public sealed record RhinoContextSnapshot(
    uint DocumentRuntimeSerial,
    string DocumentName,
    string UnitSystem,
    string ActiveLayer,
    double AbsoluteTolerance,
    double AngleToleranceRadians,
    IReadOnlyList<string> Layers,
    IReadOnlyList<SelectedObjectSnapshot> Selection)
{
    public IReadOnlySet<string> AllowedReferences => Selection.Select(item => item.ReferenceId).ToHashSet(StringComparer.Ordinal);
}
