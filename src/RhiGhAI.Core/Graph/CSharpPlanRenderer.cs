using System.Globalization;
using System.Text;
using RhiGhAI.Core.Contracts;

namespace RhiGhAI.Core.Graph;

public static class CSharpPlanRenderer
{
    public static string Render(OperationGraph graph)
    {
        StringBuilder builder = new();
        builder.AppendLine("// Контролируемое представление RhiGhAI — этот текст не исполняется напрямую.");
        builder.AppendLine("using Rhino.Geometry;");
        builder.AppendLine();
        foreach (OperationNode node in graph.Nodes)
        {
            builder.AppendLine(RenderNode(node.TypedOperation));
        }

        return builder.ToString();
    }

    private static string RenderNode(TaskOperation operation) => operation switch
    {
        EnsureLayerOperation layer => $"EnsureLayer({Quote(layer.Name)});",
        CreateBoxOperation box => $"var {box.Id} = CreateBox(new Point3d({N(box.Origin.X)}, {N(box.Origin.Y)}, {N(box.Origin.Z)}), new Vector3d({N(box.Size.X)}, {N(box.Size.Y)}, {N(box.Size.Z)}), {Quote(box.Layer)});",
        CreatePolylineOperation curve => $"var {curve.Id} = CreatePolyline(new[] {{ {string.Join(", ", curve.Points.Select(point => $"new Point3d({N(point.X)}, {N(point.Y)}, {N(point.Z)})"))} }}, {curve.Closed.ToString().ToLowerInvariant()}, {Quote(curve.Layer)});",
        CreatePlanarSurfaceOperation surface => $"var {surface.Id} = CreatePlanarSurface({surface.BoundaryId}, {Quote(surface.Layer)});",
        ExtrudeOperation extrusion => $"var {extrusion.Id} = Extrude({extrusion.ProfileId}, new Vector3d({N(extrusion.Direction.X)}, {N(extrusion.Direction.Y)}, {N(extrusion.Direction.Z)}), {extrusion.Cap.ToString().ToLowerInvariant()}, {Quote(extrusion.Layer)});",
        BooleanOperation boolean => $"var {boolean.Id} = Boolean{boolean.Mode}({string.Join(", ", boolean.InputIds)}, {Quote(boolean.Layer)});",
        TransformOperation transform => $"var {transform.Id} = Transform(new[] {{ {string.Join(", ", transform.References.Select(Quote))} }}, {Xform(transform.Translation, transform.Rotation, transform.Scale)}, {NullableQuote(transform.Layer)});",
        CopyOperation copy => $"var {copy.Id} = Copy(new[] {{ {string.Join(", ", copy.References.Select(Quote))} }}, {Xform(copy.Translation, copy.Rotation, copy.Scale)}, {NullableQuote(copy.Layer)});",
        SetAttributesOperation attributes => $"var {attributes.Id} = SetLayer(new[] {{ {string.Join(", ", attributes.References.Select(Quote))} }}, {Quote(attributes.Layer)});",
        CreateCurveOperation curve => $"var {curve.Id} = CreateInterpolatedCurve(new[] {{ {string.Join(", ", curve.Points.Select(P))} }}, {B(curve.Closed)}, {Quote(curve.Layer)});",
        CreateCircleOperation circle => $"var {circle.Id} = CreateCircle({P(circle.Center)}, {V(circle.Normal)}, {N(circle.Radius)}, {Quote(circle.Layer)});",
        CreateArcOperation arc => $"var {arc.Id} = CreateArc({P(arc.Start)}, {P(arc.Mid)}, {P(arc.End)}, {Quote(arc.Layer)});",
        LoftOperation loft => $"var {loft.Id} = Loft(new[] {{ {string.Join(", ", loft.CurveIds)} }}, closed: {B(loft.Closed)}, cap: {B(loft.Cap)}, {Quote(loft.Layer)});",
        Sweep1Operation sweep => $"var {sweep.Id} = Sweep1({sweep.RailId}, new[] {{ {string.Join(", ", sweep.ProfileIds)} }}, closed: {B(sweep.Closed)}, cap: {B(sweep.Cap)}, {Quote(sweep.Layer)});",
        PipeOperation pipe => $"var {pipe.Id} = Pipe({pipe.RailId}, {N(pipe.Radius)}, cap: {B(pipe.Cap)}, {Quote(pipe.Layer)});",
        RevolveOperation revolve => $"var {revolve.Id} = Revolve({revolve.ProfileId}, new Line({P(revolve.AxisStart)}, {P(revolve.AxisEnd)}), {N(revolve.AngleDegrees)}, {Quote(revolve.Layer)});",
        FilletEdgesOperation fillet => $"var {fillet.Id} = FilletEdges({fillet.BrepId}, {N(fillet.Radius)}, {Quote(fillet.Layer)});",
        _ => throw new NotSupportedException(operation.GetType().Name)
    };

    private static string Xform(Vector3 translation, RotationSpec? rotation, ScaleSpec? scale)
    {
        string result = $"Translation({N(translation.X)}, {N(translation.Y)}, {N(translation.Z)})";
        if (rotation is not null)
        {
            result += $" * Rotation({N(rotation.AngleDegrees)}°, {V(rotation.Axis)}, {P(rotation.Center)})";
        }

        if (scale is not null)
        {
            result += $" * Scale({P(scale.Center)}, {N(scale.Factor)})";
        }

        return result;
    }

    private static string P(Point3 point) => $"new Point3d({N(point.X)}, {N(point.Y)}, {N(point.Z)})";
    private static string V(Vector3 vector) => $"new Vector3d({N(vector.X)}, {N(vector.Y)}, {N(vector.Z)})";
    private static string B(bool value) => value ? "true" : "false";

    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    private static string NullableQuote(string? value) => value is null ? "null" : Quote(value);
}
