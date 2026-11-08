using RhiGhAI.Core.Contracts;
using RhiGhAI.Core.Graph;
using Rhino;
using Rhino.DocObjects;
using Rhino.Geometry;

namespace RhiGhAI.Rhino.Execution;

public sealed record RhinoExecutionResult(string Summary, IReadOnlyList<Guid> CreatedOrChangedIds, uint UndoSerial);

public static class RhinoPlanExecutor
{
    public static RhinoExecutionResult Execute(RhinoDoc document, RhinoContextSnapshot snapshot, OperationGraph graph, CancellationToken cancellationToken)
    {
        if (graph.TargetHost != TargetHost.Rhino)
        {
            throw new RhinoExecutionException("WrongTargetHost", "Этот executor принимает только Rhino plans.");
        }

        RhinoSnapshotBuilder.EnsureFresh(document, snapshot);
        Dictionary<string, List<GeometryBase>> prepared = PrepareGeometry(document, snapshot, graph, cancellationToken);
        HashSet<string> consumed = ConsumedIntermediateIds(graph);
        int outputCount = prepared.Where(pair => !consumed.Contains(pair.Key)).Sum(pair => pair.Value.Count);
        if (outputCount > 100)
        {
            throw new RhinoExecutionException("OutputLimit", "План создаёт больше 100 объектов; разбейте задачу на части.");
        }

        RhinoSnapshotBuilder.EnsureFresh(document, snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        uint undoSerial = document.BeginUndoRecord("RhiGhAI result");
        if (undoSerial == 0)
        {
            throw new RhinoExecutionException("UndoUnavailable", "Rhino не смог открыть Undo record.");
        }

        List<Guid> changed = [];
        List<int> createdLayers = [];
        uint nextUndoSerial = document.NextUndoRecordSerialNumber;
        bool ended = false;
        try
        {
            foreach (OperationNode node in graph.Nodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (node.TypedOperation)
                {
                    case EnsureLayerOperation layer:
                        _ = EnsureLayer(document, layer.Name, createdLayers);
                        break;
                    case TransformOperation transform:
                        ApplyTransform(document, snapshot, transform, changed, createdLayers);
                        break;
                    case SetAttributesOperation attributes:
                        ApplyAttributes(document, snapshot, attributes, changed, createdLayers);
                        break;
                    case CopyOperation:
                        AddPrepared(document, node, prepared, consumed, changed, createdLayers);
                        break;
                    default:
                        AddPrepared(document, node, prepared, consumed, changed, createdLayers);
                        break;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            ended = document.EndUndoRecord(undoSerial);
            if (!ended)
            {
                throw new RhinoExecutionException("UndoCloseFailed", "Rhino не закрыл Undo record.");
            }

            document.Views.Redraw();
            return new RhinoExecutionResult(graph.Summary, changed, undoSerial);
        }
        catch
        {
            if (!ended)
            {
                _ = document.EndUndoRecord(undoSerial);
            }

            // Rhino discards an empty undo record, so an unconditional Undo() here would roll back
            // whatever the user did before RhiGhAI ran. Only undo a record that actually wrote.
            if ((changed.Count > 0 || createdLayers.Count > 0) &&
                (document.NextUndoRecordSerialNumber != nextUndoSerial || document.UndoRecordingIsActive || !document.Undo()))
            {
                throw new RhinoExecutionException("RollbackFailed", "Не удалось откатить собственный Undo record; новые операции заблокированы.");
            }

            document.Views.Redraw();
            throw;
        }
    }

    private static Dictionary<string, List<GeometryBase>> PrepareGeometry(
        RhinoDoc document,
        RhinoContextSnapshot snapshot,
        OperationGraph graph,
        CancellationToken cancellationToken)
    {
        Dictionary<string, List<GeometryBase>> outputs = new(StringComparer.Ordinal);
        Dictionary<string, SelectedObjectSnapshot> selected = snapshot.Selection.ToDictionary(item => item.ReferenceId, StringComparer.Ordinal);
        foreach (OperationNode node in graph.Nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<GeometryBase> geometry = node.TypedOperation switch
            {
                CreateBoxOperation box => [CreateBox(box)],
                CreatePolylineOperation polyline => [CreatePolyline(polyline)],
                CreatePlanarSurfaceOperation surface => CreatePlanarSurface(outputs, surface, document.ModelAbsoluteTolerance),
                ExtrudeOperation extrusion => [CreateExtrusion(outputs, extrusion, document.ModelAbsoluteTolerance)],
                BooleanOperation boolean => CreateBoolean(outputs, boolean, document.ModelAbsoluteTolerance),
                CopyOperation copy => CopyGeometry(document, outputs, selected, copy),
                CreateCurveOperation curve => [CreateCurve(curve)],
                CreateCircleOperation circle => [CreateCircle(circle)],
                CreateArcOperation arc => [CreateArc(arc)],
                LoftOperation loft => CreateLoft(outputs, loft, document.ModelAbsoluteTolerance),
                Sweep1Operation sweep => CreateSweep(outputs, sweep, document.ModelAbsoluteTolerance),
                PipeOperation pipe => CreatePipe(outputs, pipe, document.ModelAbsoluteTolerance, document.ModelAngleToleranceRadians),
                RevolveOperation revolve => [CreateRevolve(outputs, revolve)],
                FilletEdgesOperation fillet => CreateFillet(outputs, fillet, document.ModelAbsoluteTolerance),
                _ => []
            };
            outputs[node.NodeId] = geometry;
        }

        return outputs;
    }

    private static Brep CreateBox(CreateBoxOperation box)
    {
        Point3d min = new(box.Origin.X, box.Origin.Y, box.Origin.Z);
        Point3d max = new(box.Origin.X + box.Size.X, box.Origin.Y + box.Size.Y, box.Origin.Z + box.Size.Z);
        return Brep.CreateFromBox(new BoundingBox(min, max))
            ?? throw new RhinoExecutionException("BoxFailed", "Rhino не создал box из заданных размеров.");
    }

    private static PolylineCurve CreatePolyline(CreatePolylineOperation operation)
    {
        List<Point3d> points = operation.Points.Select(point => new Point3d(point.X, point.Y, point.Z)).ToList();
        if (operation.Closed && points[0].DistanceTo(points[^1]) > 1e-9)
        {
            points.Add(points[0]);
        }

        return new PolylineCurve(points);
    }

    private static NurbsCurve CreateCurve(CreateCurveOperation operation)
    {
        List<Point3d> points = operation.Points.Select(point => new Point3d(point.X, point.Y, point.Z)).ToList();
        if (operation.Closed && points.Count > 1 && points[0].DistanceTo(points[^1]) <= 1e-9)
        {
            points.RemoveAt(points.Count - 1);
        }

        return Curve.CreateInterpolatedCurve(points, 3, operation.Closed ? CurveKnotStyle.ChordPeriodic : CurveKnotStyle.Chord) as NurbsCurve
            ?? throw new RhinoExecutionException("CurveFailed", "Rhino не построил кривую через заданные точки.");
    }

    private static ArcCurve CreateCircle(CreateCircleOperation operation)
    {
        Plane plane = new(new Point3d(operation.Center.X, operation.Center.Y, operation.Center.Z), new Vector3d(operation.Normal.X, operation.Normal.Y, operation.Normal.Z));
        Circle circle = new(plane, operation.Radius);
        return circle.IsValid ? new ArcCurve(circle) : throw new RhinoExecutionException("CurveFailed", "Rhino не построил circle.");
    }

    private static ArcCurve CreateArc(CreateArcOperation operation)
    {
        Arc arc = new(ToPoint(operation.Start), ToPoint(operation.Mid), ToPoint(operation.End));
        return arc.IsValid ? new ArcCurve(arc) : throw new RhinoExecutionException("CurveFailed", "Rhino не построил arc через три точки.");
    }

    private static List<GeometryBase> CreateLoft(Dictionary<string, List<GeometryBase>> outputs, LoftOperation operation, double tolerance)
    {
        // A referenced copy contributes every curve it made, in reference order.
        List<Curve> curves = operation.CurveIds.SelectMany(id => outputs[id])
            .Select(item => item as Curve ?? throw new RhinoExecutionException("WrongGeometryType", "Loft принимает только кривые."))
            .ToList();
        if (curves.Count is < 2 or > 64)
        {
            throw new RhinoExecutionException("LoftFailed", "Loft требует от 2 до 64 кривых.");
        }

        Brep[]? breps = Brep.CreateFromLoft(curves, Point3d.Unset, Point3d.Unset, LoftType.Normal, operation.Closed);
        return Capped(breps, operation.Cap, tolerance, "LoftFailed", "Loft не дал допустимый результат.");
    }

    private static List<GeometryBase> CreateSweep(Dictionary<string, List<GeometryBase>> outputs, Sweep1Operation operation, double tolerance)
    {
        Curve rail = RequireSingle<Curve>(outputs, operation.RailId);
        List<Curve> profiles = operation.ProfileIds.Select(id => RequireSingle<Curve>(outputs, id)).ToList();
        Brep[]? breps = Brep.CreateFromSweep(rail, profiles, operation.Closed, tolerance);
        return Capped(breps, operation.Cap, tolerance, "SweepFailed", "Sweep1 не дал допустимый результат.");
    }

    private static List<GeometryBase> CreatePipe(Dictionary<string, List<GeometryBase>> outputs, PipeOperation operation, double tolerance, double angleTolerance)
    {
        Curve rail = RequireSingle<Curve>(outputs, operation.RailId);
        PipeCapMode cap = operation.Cap && !rail.IsClosed ? PipeCapMode.Flat : PipeCapMode.None;
        Brep[]? breps = Brep.CreatePipe(rail, operation.Radius, false, cap, true, tolerance, angleTolerance);
        return Capped(breps, false, tolerance, "PipeFailed", "Pipe не дал допустимый результат.");
    }

    private static Brep CreateRevolve(Dictionary<string, List<GeometryBase>> outputs, RevolveOperation operation)
    {
        Curve profile = RequireSingle<Curve>(outputs, operation.ProfileId);
        Line axis = new(ToPoint(operation.AxisStart), ToPoint(operation.AxisEnd));
        RevSurface surface = RevSurface.Create(profile, axis, 0, RhinoMath.ToRadians(operation.AngleDegrees))
            ?? throw new RhinoExecutionException("RevolveFailed", "Rhino не создал revolve.");
        return Brep.CreateFromRevSurface(surface, false, false)
            ?? throw new RhinoExecutionException("RevolveFailed", "Rhino не создал revolve.");
    }

    private static List<GeometryBase> CreateFillet(Dictionary<string, List<GeometryBase>> outputs, FilletEdgesOperation operation, double tolerance)
    {
        Brep brep = RequireSingle<Brep>(outputs, operation.BrepId);
        int[] edges = Enumerable.Range(0, brep.Edges.Count).ToArray();
        double[] radii = edges.Select(_ => operation.Radius).ToArray();
        Brep[]? breps = Brep.CreateFilletEdges(brep, edges, radii, radii, BlendType.Fillet, RailType.RollingBall, tolerance);
        return Capped(breps, false, tolerance, "FilletFailed", "FilletEdges не дал результат: радиус, вероятно, больше граней.");
    }

    private static List<GeometryBase> Capped(Brep[]? breps, bool cap, double tolerance, string code, string message)
    {
        if (breps is not { Length: > 0 and <= 16 } || breps.Any(brep => brep is null || !brep.IsValid))
        {
            throw new RhinoExecutionException(code, message);
        }

        // CapPlanarHoles returns null when there is nothing planar to close; keep the open Brep then.
        return breps.Select(brep => cap ? brep.CapPlanarHoles(tolerance) ?? brep : brep).Cast<GeometryBase>().ToList();
    }

    private static Point3d ToPoint(Point3 point) => new(point.X, point.Y, point.Z);

    /// <summary>Scale, then rotate, then translate — the order the contract documents.</summary>
    private static Transform BuildTransform(Vector3 translation, RotationSpec? rotation, ScaleSpec? scale)
    {
        Transform result = Transform.Translation(translation.X, translation.Y, translation.Z);
        if (rotation is not null)
        {
            result *= Transform.Rotation(RhinoMath.ToRadians(rotation.AngleDegrees), new Vector3d(rotation.Axis.X, rotation.Axis.Y, rotation.Axis.Z), ToPoint(rotation.Center));
        }

        if (scale is not null)
        {
            result *= Transform.Scale(ToPoint(scale.Center), scale.Factor);
        }

        return result;
    }

    private static List<GeometryBase> CreatePlanarSurface(Dictionary<string, List<GeometryBase>> outputs, CreatePlanarSurfaceOperation operation, double tolerance)
    {
        Curve curve = RequireSingle<Curve>(outputs, operation.BoundaryId);
        Brep[] breps = Brep.CreatePlanarBreps(curve, tolerance);
        if (breps.Length is < 1 or > 8)
        {
            throw new RhinoExecutionException("PlanarSurfaceFailed", "Planar surface не дала допустимый результат.");
        }

        return breps.Cast<GeometryBase>().ToList();
    }

    private static Brep CreateExtrusion(Dictionary<string, List<GeometryBase>> outputs, ExtrudeOperation operation, double tolerance)
    {
        Curve profile = RequireSingle<Curve>(outputs, operation.ProfileId);
        Vector3d direction = new(operation.Direction.X, operation.Direction.Y, operation.Direction.Z);
        Surface surface = Surface.CreateExtrusion(profile, direction) ?? throw new RhinoExecutionException("ExtrusionFailed", "Rhino не создал extrusion.");
        Brep brep = surface.ToBrep();
        if (operation.Cap && profile.IsClosed)
        {
            brep = brep.CapPlanarHoles(tolerance) ?? throw new RhinoExecutionException("CapFailed", "Не удалось закрыть extrusion.");
        }

        return brep;
    }

    private static List<GeometryBase> CreateBoolean(Dictionary<string, List<GeometryBase>> outputs, BooleanOperation operation, double tolerance)
    {
        List<Brep> inputs = operation.InputIds.SelectMany(id => outputs[id]).Select(item => item as Brep ?? throw new RhinoExecutionException("WrongGeometryType", "Boolean принимает только Brep.")).ToList();
        Brep[]? result = operation.Mode switch
        {
            BooleanMode.Union => Brep.CreateBooleanUnion(inputs, tolerance),
            BooleanMode.Difference => Brep.CreateBooleanDifference([inputs[0]], inputs.Skip(1), tolerance),
            BooleanMode.Intersection => IntersectMany(inputs, tolerance),
            _ => null
        };
        if (result is not { Length: > 0 and <= 16 })
        {
            throw new RhinoExecutionException("BooleanFailed", "Boolean не дал допустимый результат.");
        }

        return result.Cast<GeometryBase>().ToList();
    }

    private static Brep[]? IntersectMany(IReadOnlyList<Brep> inputs, double tolerance)
    {
        Brep[] current = [inputs[0]];
        for (int index = 1; index < inputs.Count; index++)
        {
            current = Brep.CreateBooleanIntersection(current, [inputs[index]], tolerance);
            if (current.Length == 0)
            {
                return null;
            }
        }

        return current;
    }

    private static List<GeometryBase> CopyGeometry(
        RhinoDoc document,
        IReadOnlyDictionary<string, List<GeometryBase>> outputs,
        IReadOnlyDictionary<string, SelectedObjectSnapshot> selected,
        CopyOperation operation)
    {
        Transform translation = BuildTransform(operation.Translation, operation.Rotation, operation.Scale);
        List<GeometryBase> copies = [];
        foreach (string reference in operation.References)
        {
            IEnumerable<GeometryBase> source = outputs.TryGetValue(reference, out List<GeometryBase>? generated)
                ? generated
                : [document.Objects.FindId(selected[reference].ObjectId)?.Geometry ?? throw new RhinoExecutionException("MissingObject", "Выбранный объект исчез.")];
            foreach (GeometryBase geometry in source)
            {
                GeometryBase copy = geometry.Duplicate();
                if (!copy.Transform(translation))
                {
                    throw new RhinoExecutionException("TransformFailed", "Не удалось подготовить copy.");
                }

                copies.Add(copy);
            }
        }

        return copies;
    }

    private static void AddPrepared(
        RhinoDoc document,
        OperationNode node,
        IReadOnlyDictionary<string, List<GeometryBase>> prepared,
        IReadOnlySet<string> consumed,
        ICollection<Guid> changed, ICollection<int> createdLayers)
    {
        if (consumed.Contains(node.NodeId) || !prepared.TryGetValue(node.NodeId, out List<GeometryBase>? geometry))
        {
            return;
        }

        string? layerName = LayerFor(node.TypedOperation);
        int? layerIndex = layerName is null ? null : EnsureLayer(document, layerName, createdLayers);
        foreach (GeometryBase item in geometry)
        {
            ObjectAttributes attributes = document.CreateDefaultAttributes();
            if (layerIndex.HasValue)
            {
                attributes.LayerIndex = layerIndex.Value;
            }

            if (item is null)
            {
                throw new RhinoExecutionException("EmptyResult", $"Operation {node.NodeId} не дала геометрию.");
            }

            Guid id = document.Objects.Add(item, attributes);
            if (id == Guid.Empty)
            {
                throw new RhinoExecutionException("AddObjectFailed", $"Rhino не добавил результат {node.NodeId}.");
            }

            changed.Add(id);
        }
    }

    private static void ApplyTransform(RhinoDoc document, RhinoContextSnapshot snapshot, TransformOperation operation, ICollection<Guid> changed, ICollection<int> createdLayers)
    {
        Transform translation = BuildTransform(operation.Translation, operation.Rotation, operation.Scale);
        Dictionary<string, SelectedObjectSnapshot> selected = snapshot.Selection.ToDictionary(item => item.ReferenceId, StringComparer.Ordinal);
        int? targetLayer = operation.Layer is null ? null : EnsureLayer(document, operation.Layer, createdLayers);
        foreach (string reference in operation.References)
        {
            if (!selected.TryGetValue(reference, out SelectedObjectSnapshot? item))
            {
                throw new RhinoExecutionException("UnsupportedReference", "Transform существующей модели разрешён только для текущего выделения.");
            }

            Guid transformedId = document.Objects.Transform(item.ObjectId, translation, true);
            if (transformedId == Guid.Empty)
            {
                throw new RhinoExecutionException("TransformFailed", "Rhino не выполнил transform.");
            }

            changed.Add(transformedId);
            if (targetLayer.HasValue)
            {
                RhinoObject transformed = document.Objects.FindId(transformedId) ?? throw new RhinoExecutionException("MissingObject", "Transform result исчез.");
                ObjectAttributes attributes = transformed.Attributes.Duplicate();
                attributes.LayerIndex = targetLayer.Value;
                if (!document.Objects.ModifyAttributes(transformed, attributes, true))
                {
                    throw new RhinoExecutionException("AttributesFailed", "Не удалось назначить слой transform result.");
                }
            }

        }
    }

    private static void ApplyAttributes(RhinoDoc document, RhinoContextSnapshot snapshot, SetAttributesOperation operation, ICollection<Guid> changed, ICollection<int> createdLayers)
    {
        Dictionary<string, SelectedObjectSnapshot> selected = snapshot.Selection.ToDictionary(item => item.ReferenceId, StringComparer.Ordinal);
        int layerIndex = EnsureLayer(document, operation.Layer, createdLayers);
        foreach (string reference in operation.References)
        {
            if (!selected.TryGetValue(reference, out SelectedObjectSnapshot? item))
            {
                throw new RhinoExecutionException("UnsupportedReference", "Attributes разрешены только для текущего выделения.");
            }

            RhinoObject current = document.Objects.FindId(item.ObjectId) ?? throw new RhinoExecutionException("MissingObject", "Выбранный объект исчез.");
            ObjectAttributes attributes = current.Attributes.Duplicate();
            attributes.LayerIndex = layerIndex;
            if (!document.Objects.ModifyAttributes(current, attributes, true))
            {
                throw new RhinoExecutionException("AttributesFailed", "Не удалось изменить attributes.");
            }

            changed.Add(item.ObjectId);
        }
    }

    private static int EnsureLayer(RhinoDoc document, string name, ICollection<int> createdLayers)
    {
        Layer? existing = document.Layers.FindName(name);
        if (existing is not null)
        {
            return existing.Index;
        }

        int index = document.Layers.Add(new Layer { Name = name });
        if (index < 0)
        {
            throw new RhinoExecutionException("LayerFailed", $"Не удалось создать слой {name}.");
        }

        createdLayers.Add(index);
        return index;
    }

    private static T RequireSingle<T>(IReadOnlyDictionary<string, List<GeometryBase>> outputs, string id) where T : GeometryBase
    {
        if (!outputs.TryGetValue(id, out List<GeometryBase>? values) || values is not [T typed])
        {
            throw new RhinoExecutionException("WrongGeometryType", $"Operation {id} не дала один {typeof(T).Name}.");
        }

        return typed;
    }

    private static HashSet<string> ConsumedIntermediateIds(OperationGraph graph)
    {
        HashSet<string> operationIds = graph.Nodes.Select(node => node.NodeId).ToHashSet(StringComparer.Ordinal);
        HashSet<string> consumed = new(StringComparer.Ordinal);
        foreach (OperationNode node in graph.Nodes)
        {
            IEnumerable<string> references = node.TypedOperation switch
            {
                CreatePlanarSurfaceOperation surface => [surface.BoundaryId],
                ExtrudeOperation extrusion => [extrusion.ProfileId],
                BooleanOperation boolean => boolean.InputIds,
                LoftOperation loft => loft.CurveIds,
                Sweep1Operation sweep => [sweep.RailId, .. sweep.ProfileIds],
                PipeOperation pipe => [pipe.RailId],
                RevolveOperation revolve => [revolve.ProfileId],
                FilletEdgesOperation fillet => [fillet.BrepId],
                _ => []
            };
            foreach (string reference in references.Where(operationIds.Contains))
            {
                consumed.Add(reference);
            }
        }

        return consumed;
    }

    private static string? LayerFor(TaskOperation operation) => operation switch
    {
        CreateBoxOperation item => item.Layer,
        CreatePolylineOperation item => item.Layer,
        CreatePlanarSurfaceOperation item => item.Layer,
        ExtrudeOperation item => item.Layer,
        BooleanOperation item => item.Layer,
        CopyOperation item => item.Layer,
        CreateCurveOperation item => item.Layer,
        CreateCircleOperation item => item.Layer,
        CreateArcOperation item => item.Layer,
        LoftOperation item => item.Layer,
        Sweep1Operation item => item.Layer,
        PipeOperation item => item.Layer,
        RevolveOperation item => item.Layer,
        FilletEdgesOperation item => item.Layer,
        _ => null
    };
}

public sealed class RhinoExecutionException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
