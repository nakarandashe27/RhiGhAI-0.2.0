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
        TransformOperation transform => $"var {transform.Id} = Transform(new[] {{ {string.Join(", ", transform.References.Select(Quote))} }}, Translation({N(transform.Translation.X)}, {N(transform.Translation.Y)}, {N(transform.Translation.Z)}), {NullableQuote(transform.Layer)});",
        CopyOperation copy => $"var {copy.Id} = Copy(new[] {{ {string.Join(", ", copy.References.Select(Quote))} }}, Translation({N(copy.Translation.X)}, {N(copy.Translation.Y)}, {N(copy.Translation.Z)}), {NullableQuote(copy.Layer)});",
        SetAttributesOperation attributes => $"var {attributes.Id} = SetLayer(new[] {{ {string.Join(", ", attributes.References.Select(Quote))} }}, {Quote(attributes.Layer)});",
        _ => throw new NotSupportedException(operation.GetType().Name)
    };

    private static string N(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Quote(string value) => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    private static string NullableQuote(string? value) => value is null ? "null" : Quote(value);
}
