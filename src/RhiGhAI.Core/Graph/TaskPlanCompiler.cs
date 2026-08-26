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
            case TransformOperation transform when transform.References.Count is < 1 or > 64 || !Finite(transform.Translation) || !ValidOptionalLayer(transform.Layer):
                throw new TaskPlanValidationException("InvalidTransform", "Некорректный transform vector.");
            case CopyOperation copy when copy.References.Count is < 1 or > 64 || !Finite(copy.Translation) || !ValidOptionalLayer(copy.Layer):
                throw new TaskPlanValidationException("InvalidTransform", "Некорректный copy vector.");
            case SetAttributesOperation attributes when attributes.References.Count is < 1 or > 64 || !ValidLayer(attributes.Layer):
                throw new TaskPlanValidationException("InvalidAttributes", "Некорректные references или слой attributes.");
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
                case CreatePlanarSurfaceOperation surface when !operations.TryGetValue(surface.BoundaryId, out TaskOperation? boundary) || boundary is not CreatePolylineOperation:
                    throw new TaskPlanValidationException("WrongReferenceType", "Planar surface boundary должна ссылаться на polyline operation.");
                case ExtrudeOperation extrusion when !operations.TryGetValue(extrusion.ProfileId, out TaskOperation? profile) || profile is not CreatePolylineOperation:
                    throw new TaskPlanValidationException("WrongReferenceType", "Extrude profile должна ссылаться на polyline operation.");
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
        CreateBoxOperation or CreatePlanarSurfaceOperation or ExtrudeOperation or BooleanOperation;

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
        _ => throw new TaskPlanValidationException("UnknownOperation", "Unknown operation type.")
    };

    private static string ResultType(TaskOperation operation) => operation switch
    {
        EnsureLayerOperation => "Layer",
        CreatePolylineOperation => "Curve",
        CreatePlanarSurfaceOperation => "Brep",
        CreateBoxOperation or ExtrudeOperation or BooleanOperation => "Brep",
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
    private static double Length(Vector3 vector) => Math.Sqrt((vector.X * vector.X) + (vector.Y * vector.Y) + (vector.Z * vector.Z));
}

public sealed class TaskPlanValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
