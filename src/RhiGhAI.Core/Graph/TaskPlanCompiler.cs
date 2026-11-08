using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RhiGhAI.Core.Contracts;

namespace RhiGhAI.Core.Graph;

public static class TaskPlanCompiler
{
    public static OperationGraph Compile(TaskPlanEnvelope plan, ValidationContext context)
    {
        if (plan.SchemaVersion != ProductInfo.TaskPlanSchemaVersion)
        {
            throw new TaskPlanValidationException("UnsupportedSchemaVersion", "Неподдерживаемая версия TaskPlan.");
        }

        if (string.IsNullOrWhiteSpace(plan.Summary) || plan.Summary.Length > 500 ||
            plan.Assumptions is null || plan.Assumptions.Count > 10 ||
            plan.Assumptions.Any(item => item is null || item.Length > 300))
        {
            throw new TaskPlanValidationException("InvalidEnvelope", "Summary или assumptions выходят за границы контракта.");
        }

        if (plan.Operations is not { Count: > 0 and <= 64 })
        {
            throw new TaskPlanValidationException("OperationLimit", "TaskPlan должен содержать от 1 до 64 операций.");
        }

        if (plan.TargetHost != TargetHost.Rhino)
        {
            // Grasshopper never travels as a TaskPlan; it has its own graph contract.
            throw new TaskPlanValidationException("WrongTargetHost", "TaskPlan описывает только операции Rhino.");
        }

        Dictionary<string, TaskOperation> operations = new(StringComparer.Ordinal);
        foreach (TaskOperation operation in plan.Operations)
        {
            if (operation is null)
            {
                throw new TaskPlanValidationException("UnknownOperation", "TaskPlan содержит null operation.");
            }

            if (!IsValidId(operation.Id) || !operations.TryAdd(operation.Id, operation))
            {
                throw new TaskPlanValidationException("InvalidOperationId", $"Некорректный или повторный id: {operation.Id}.");
            }
        }

        List<OperationNode> nodes = [];
        foreach (TaskOperation operation in plan.Operations)
        {
            ValidateOperation(operation);
            ImmutableArray<string> dependencies = Dependencies(operation).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
            foreach (string dependency in dependencies)
            {
                if (!operations.ContainsKey(dependency) && !context.AllowedReferences.Contains(dependency))
                {
                    throw new TaskPlanValidationException("UnknownReference", $"Ссылка {dependency} не разрешена snapshot.");
                }
            }

            nodes.Add(new OperationNode(
                operation.Id,
                Kind(operation),
                1,
                plan.TargetHost,
                operation,
                dependencies,
                ResultType(operation)));
        }


        ValidateDependencyTypes(nodes, operations, context.AllowedReferences);

        ImmutableArray<OperationNode> ordered = TopologicalSort(nodes, operations.Keys.ToHashSet(StringComparer.Ordinal));
        string canonical = JsonSerializer.Serialize(new { plan.SchemaVersion, plan.TargetHost, nodes = ordered }, TaskPlanJson.Options);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new OperationGraph(plan.SchemaVersion, plan.TargetHost, ordered, hash, plan.Summary);
    }

    private static void ValidateOperation(TaskOperation operation)
    {
        switch (operation)
        {
            case CreateBoxOperation box when box.Origin is null || box.Size is null:
            case CreatePolylineOperation polyline when polyline.Points is null:
            case CreatePlanarSurfaceOperation surface when string.IsNullOrWhiteSpace(surface.BoundaryId):
            case ExtrudeOperation extrusion when string.IsNullOrWhiteSpace(extrusion.ProfileId) || extrusion.Direction is null:
            case BooleanOperation boolean when boolean.InputIds is null:
            case TransformOperation transform when transform.References is null || transform.Translation is null:
            case CopyOperation copy when copy.References is null || copy.Translation is null:
            case SetAttributesOperation attributes when attributes.References is null:
            case CreateCurveOperation curve when curve.Points is null:
            case CreateCircleOperation circle when circle.Center is null || circle.Normal is null:
            case CreateArcOperation arc when arc.Start is null || arc.Mid is null || arc.End is null:
            case LoftOperation loft when loft.CurveIds is null:
            case Sweep1Operation sweep when string.IsNullOrWhiteSpace(sweep.RailId) || sweep.ProfileIds is null:
            case PipeOperation pipe when string.IsNullOrWhiteSpace(pipe.RailId):
            case RevolveOperation revolve when string.IsNullOrWhiteSpace(revolve.ProfileId) || revolve.AxisStart is null || revolve.AxisEnd is null:
            case FilletEdgesOperation fillet when string.IsNullOrWhiteSpace(fillet.BrepId):
            case TransformOperation { Rotation: { Axis: null } or { Center: null } } or TransformOperation { Scale.Center: null }:
            case CopyOperation { Rotation: { Axis: null } or { Center: null } } or CopyOperation { Scale.Center: null }:
                throw new TaskPlanValidationException("MissingRequiredValue", "Operation содержит null вместо обязательного значения.");
        }

        switch (operation)
        {
            case EnsureLayerOperation layer when !ValidLayer(layer.Name):
                throw new TaskPlanValidationException("InvalidLayer", "Некорректное имя слоя.");
            case CreateBoxOperation box when !PositiveFinite(box.Size.X) || !PositiveFinite(box.Size.Y) || !PositiveFinite(box.Size.Z) || !Finite(box.Origin) || !ValidLayer(box.Layer):
                throw new TaskPlanValidationException("InvalidDimension", "Некорректные размеры box.");
            case CreatePolylineOperation polyline when polyline.Points.Count is < 2 or > 256 || polyline.Points.Any(point => !Finite(point)) || !ValidLayer(polyline.Layer):
                throw new TaskPlanValidationException("InvalidCurve", "Polyline содержит некорректные точки.");
            case CreatePlanarSurfaceOperation surface when !ValidLayer(surface.Layer):
                throw new TaskPlanValidationException("InvalidLayer", "Некорректное имя слоя.");
            case ExtrudeOperation extrusion when !Finite(extrusion.Direction) || Length(extrusion.Direction) <= 1e-9 || !ValidLayer(extrusion.Layer):
                throw new TaskPlanValidationException("InvalidDirection", "Extrusion direction должна быть ненулевой.");
            case BooleanOperation boolean when boolean.InputIds.Count is < 2 or > 8 || !ValidLayer(boolean.Layer):
                throw new TaskPlanValidationException("BooleanLimit", "Boolean принимает от 2 до 8 Brep.");
            case TransformOperation transform when transform.References.Count is < 1 or > 64 || !Finite(transform.Translation) || !ValidRotation(transform.Rotation) || !ValidScale(transform.Scale) || !ValidOptionalLayer(transform.Layer):
                throw new TaskPlanValidationException("InvalidTransform", "Некорректный transform: vector, rotation (ненулевая ось) или scale (0.001..1000).");
            case CopyOperation copy when copy.References.Count is < 1 or > 64 || !Finite(copy.Translation) || !ValidRotation(copy.Rotation) || !ValidScale(copy.Scale) || !ValidOptionalLayer(copy.Layer):
                throw new TaskPlanValidationException("InvalidTransform", "Некорректный copy: vector, rotation (ненулевая ось) или scale (0.001..1000).");
            case SetAttributesOperation attributes when attributes.References.Count is < 1 or > 64 || !ValidLayer(attributes.Layer):
                throw new TaskPlanValidationException("InvalidAttributes", "Некорректные references или слой attributes.");
            case CreateCurveOperation curve when curve.Points.Count is < 2 or > 256 || (curve.Closed && curve.Points.Count < 3) || curve.Points.Any(point => !Finite(point)) || !ValidLayer(curve.Layer):
                throw new TaskPlanValidationException("InvalidCurve", "Curve требует 2..256 точек (замкнутая — от 3).");
            case CreateCircleOperation circle when !Finite(circle.Center) || !Finite(circle.Normal) || Length(circle.Normal) <= 1e-9 || !PositiveFinite(circle.Radius) || !ValidLayer(circle.Layer):
                throw new TaskPlanValidationException("InvalidCurve", "Circle требует положительный radius и ненулевую normal.");
            case CreateArcOperation arc when !Finite(arc.Start) || !Finite(arc.Mid) || !Finite(arc.End) || Collinear(arc.Start, arc.Mid, arc.End) || !ValidLayer(arc.Layer):
                throw new TaskPlanValidationException("InvalidCurve", "Arc требует три различные неколлинеарные точки.");
            case LoftOperation loft when loft.CurveIds.Count is < 2 or > 32 || !ValidLayer(loft.Layer):
                throw new TaskPlanValidationException("LoftLimit", "Loft принимает от 2 до 32 кривых.");
            case Sweep1Operation sweep when sweep.ProfileIds.Count is < 1 or > 16 || !ValidLayer(sweep.Layer):
                throw new TaskPlanValidationException("SweepLimit", "Sweep1 принимает от 1 до 16 профилей.");
            case PipeOperation pipe when !PositiveFinite(pipe.Radius) || !ValidLayer(pipe.Layer):
                throw new TaskPlanValidationException("InvalidDimension", "Pipe требует положительный radius.");
            case RevolveOperation revolve when !Finite(revolve.AxisStart) || !Finite(revolve.AxisEnd) || Distance(revolve.AxisStart, revolve.AxisEnd) <= 1e-9 || !double.IsFinite(revolve.AngleDegrees) || revolve.AngleDegrees is <= 0 or > 360 || !ValidLayer(revolve.Layer):
                throw new TaskPlanValidationException("InvalidRevolve", "Revolve требует ось ненулевой длины и угол в (0, 360].");
            case FilletEdgesOperation fillet when !PositiveFinite(fillet.Radius) || !ValidLayer(fillet.Layer):
                throw new TaskPlanValidationException("InvalidDimension", "FilletEdges требует положительный radius.");
        }
    }

    private static void ValidateDependencyTypes(
        IReadOnlyList<OperationNode> nodes,
        IReadOnlyDictionary<string, TaskOperation> operations,
        IReadOnlySet<string> selectedReferences)
    {
        foreach (OperationNode node in nodes)
        {
            switch (node.TypedOperation)
            {
                case CreatePlanarSurfaceOperation surface when !IsCurve(operations, surface.BoundaryId):
                    throw new TaskPlanValidationException("WrongReferenceType", "Planar surface boundary должна ссылаться на curve operation (polyline, curve, circle, arc).");
                case ExtrudeOperation extrusion when !IsCurve(operations, extrusion.ProfileId):
                    throw new TaskPlanValidationException("WrongReferenceType", "Extrude profile должна ссылаться на curve operation (polyline, curve, circle, arc).");
                case LoftOperation loft when loft.CurveIds.Any(id => !IsCurveOrCurveCopy(operations, id)):
                    throw new TaskPlanValidationException("WrongReferenceType", "Loft curves должны ссылаться на curve operations или copy кривых этого плана.");
                case Sweep1Operation sweep when !IsCurve(operations, sweep.RailId) || sweep.ProfileIds.Any(id => !IsCurve(operations, id)):
                    throw new TaskPlanValidationException("WrongReferenceType", "Sweep1 rail и profiles должны ссылаться на curve operations.");
                case PipeOperation pipe when !IsCurve(operations, pipe.RailId):
                    throw new TaskPlanValidationException("WrongReferenceType", "Pipe rail должна ссылаться на curve operation.");
                case RevolveOperation revolve when !IsCurve(operations, revolve.ProfileId):
                    throw new TaskPlanValidationException("WrongReferenceType", "Revolve profile должна ссылаться на curve operation.");
                case FilletEdgesOperation fillet when !operations.TryGetValue(fillet.BrepId, out TaskOperation? solid) || !ProducesBrep(solid):
                    throw new TaskPlanValidationException("WrongReferenceType", "FilletEdges должна ссылаться на Brep operation этого плана.");
                case BooleanOperation boolean when boolean.InputIds.Any(id => !operations.TryGetValue(id, out TaskOperation? input) || !ProducesBrep(input)):
                    throw new TaskPlanValidationException("WrongReferenceType", "Boolean inputs должны ссылаться на Brep operations этого плана.");
                case TransformOperation transform when transform.References.Any(reference => !selectedReferences.Contains(reference)):
                    throw new TaskPlanValidationException("UnsupportedReference", "Transform существующей модели разрешён только для текущего выделения.");
                case SetAttributesOperation attributes when attributes.References.Any(reference => !selectedReferences.Contains(reference)):
                    throw new TaskPlanValidationException("UnsupportedReference", "Attributes существующей модели разрешены только для текущего выделения.");
            }
        }
    }

    private static bool ProducesBrep(TaskOperation operation) => operation is
        CreateBoxOperation or CreatePlanarSurfaceOperation or ExtrudeOperation or BooleanOperation or
        LoftOperation or Sweep1Operation or PipeOperation or RevolveOperation or FilletEdgesOperation;

    private static bool IsCurve(IReadOnlyDictionary<string, TaskOperation> operations, string id) =>
        operations.TryGetValue(id, out TaskOperation? operation) &&
        operation is CreatePolylineOperation or CreateCurveOperation or CreateCircleOperation or CreateArcOperation;

    // A copy of plan curves yields one curve per reference, which is how a loft gets rotated or
    // scaled sections without the model computing every point itself.
    private static bool IsCurveOrCurveCopy(IReadOnlyDictionary<string, TaskOperation> operations, string id) =>
        IsCurve(operations, id) ||
        (operations.TryGetValue(id, out TaskOperation? operation) && operation is CopyOperation copy && copy.References.All(reference => IsCurve(operations, reference)));

    private static ImmutableArray<OperationNode> TopologicalSort(IReadOnlyList<OperationNode> nodes, IReadOnlySet<string> operationIds)
    {
        Dictionary<string, int> degree = nodes.ToDictionary(node => node.NodeId, node => node.Dependencies.Count(operationIds.Contains), StringComparer.Ordinal);
        SortedSet<string> ready = new(degree.Where(pair => pair.Value == 0).Select(pair => pair.Key), StringComparer.Ordinal);
        Dictionary<string, OperationNode> byId = nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        List<OperationNode> ordered = [];
        while (ready.Count > 0)
        {
            string id = ready.Min!;
            ready.Remove(id);
            ordered.Add(byId[id]);
            foreach (OperationNode candidate in nodes.Where(node => node.Dependencies.Contains(id, StringComparer.Ordinal)))
            {
                degree[candidate.NodeId]--;
                if (degree[candidate.NodeId] == 0)
                {
                    ready.Add(candidate.NodeId);
                }
            }
        }

        if (ordered.Count != nodes.Count)
        {
            throw new TaskPlanValidationException("CyclicGraph", "Operation graph содержит цикл.");
        }

        return ordered.ToImmutableArray();
    }

    private static IEnumerable<string> Dependencies(TaskOperation operation) => operation switch
    {
        CreatePlanarSurfaceOperation surface => [surface.BoundaryId],
        ExtrudeOperation extrusion => [extrusion.ProfileId],
        BooleanOperation boolean => boolean.InputIds,
        TransformOperation transform => transform.References,
        CopyOperation copy => copy.References,
        SetAttributesOperation attributes => attributes.References,
        LoftOperation loft => loft.CurveIds,
        Sweep1Operation sweep => [sweep.RailId, .. sweep.ProfileIds],
        PipeOperation pipe => [pipe.RailId],
        RevolveOperation revolve => [revolve.ProfileId],
        FilletEdgesOperation fillet => [fillet.BrepId],
        _ => []
    };

    private static string Kind(TaskOperation operation) => operation switch
    {
        EnsureLayerOperation => "ensureLayer",
        CreateBoxOperation => "createBox",
        CreatePolylineOperation => "createPolyline",
        CreatePlanarSurfaceOperation => "createPlanarSurface",
        ExtrudeOperation => "extrude",
        BooleanOperation => "boolean",
        TransformOperation => "transform",
        CopyOperation => "copy",
        SetAttributesOperation => "setAttributes",
        CreateCurveOperation => "createCurve",
        CreateCircleOperation => "createCircle",
        CreateArcOperation => "createArc",
        LoftOperation => "loft",
        Sweep1Operation => "sweep1",
        PipeOperation => "pipe",
        RevolveOperation => "revolve",
        FilletEdgesOperation => "filletEdges",
        _ => throw new TaskPlanValidationException("UnknownOperation", "Unknown operation type.")
    };

    private static string ResultType(TaskOperation operation) => operation switch
    {
        EnsureLayerOperation => "Layer",
        CreatePolylineOperation or CreateCurveOperation or CreateCircleOperation or CreateArcOperation => "Curve",
        _ when ProducesBrep(operation) => "Brep",
        TransformOperation or CopyOperation or SetAttributesOperation => "ModelObjectList",
        _ => "Unknown"
    };

    private static bool IsValidId(string? value) => value is { Length: > 0 and <= 64 } && char.IsLetter(value[0]) && value.All(character => char.IsLetterOrDigit(character) || character is '_' or '-');
    private static bool PositiveFinite(double value) => double.IsFinite(value) && value > 0 && value <= 1_000_000;
    private static bool Bounded(double value) => double.IsFinite(value) && Math.Abs(value) <= 1_000_000;
    private static bool Finite(Point3 point) => Bounded(point.X) && Bounded(point.Y) && Bounded(point.Z);
    private static bool Finite(Vector3 vector) => Bounded(vector.X) && Bounded(vector.Y) && Bounded(vector.Z);
    private static bool ValidLayer(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 120 &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Contains("::", StringComparison.Ordinal) &&
        value.IndexOfAny(['\\', '/', '?', '*', '"', '<', '>', '|', '\0', '\r', '\n']) < 0;
    private static bool ValidOptionalLayer(string? value) => value is null || ValidLayer(value);
    private static bool ValidRotation(RotationSpec? rotation) => rotation is null ||
        (Finite(rotation.Axis) && Length(rotation.Axis) > 1e-9 && Bounded(rotation.AngleDegrees) && Math.Abs(rotation.AngleDegrees) <= 3600 && Finite(rotation.Center));
    private static bool ValidScale(ScaleSpec? scale) => scale is null ||
        (double.IsFinite(scale.Factor) && scale.Factor is >= 0.001 and <= 1000 && Finite(scale.Center));
    private static double Distance(Point3 a, Point3 b) => Length(new Vector3(b.X - a.X, b.Y - a.Y, b.Z - a.Z));
    private static bool Collinear(Point3 a, Point3 b, Point3 c)
    {
        Vector3 u = new(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
        Vector3 v = new(c.X - a.X, c.Y - a.Y, c.Z - a.Z);
        Vector3 cross = new((u.Y * v.Z) - (u.Z * v.Y), (u.Z * v.X) - (u.X * v.Z), (u.X * v.Y) - (u.Y * v.X));
        return Length(cross) <= 1e-9 * Math.Max(1, Length(u) * Length(v));
    }
    private static double Length(Vector3 vector) => Math.Sqrt((vector.X * vector.X) + (vector.Y * vector.Y) + (vector.Z * vector.Z));
}

public sealed class TaskPlanValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
