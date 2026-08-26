using Rhino;
using Rhino.DocObjects;

namespace RhiGhAI.Rhino.Execution;

public static class RhinoSnapshotBuilder
{
    public static RhinoContextSnapshot Capture(RhinoDoc document)
    {
        List<SelectedObjectSnapshot> selection = [];
        foreach (RhinoObject item in document.Objects.GetSelectedObjects(false, false))
        {
            if (selection.Count >= 100)
            {
                throw new RhinoExecutionException("SelectionLimit", "Выбрано больше 100 объектов; сузьте выделение и повторите задачу.");
            }

            string layer = document.Layers[item.Attributes.LayerIndex]?.FullPath ?? "";
            selection.Add(new SelectedObjectSnapshot(
                $"selection:{item.Id:D}",
                item.Id,
                item.ObjectType.ToString(),
                layer,
                item.Geometry.DataCRC(0),
                AttributeFingerprint(item, layer),
                item.Geometry.GetBoundingBox(true)));
        }

        List<string> layers = [];
        foreach (Layer layer in document.Layers)
        {
            if (!layer.IsDeleted)
            {
                if (layers.Count < 200)
                {
                    layers.Add(layer.FullPath);
                }
            }
        }

        return new RhinoContextSnapshot(
            document.RuntimeSerialNumber,
            string.IsNullOrWhiteSpace(document.Name) ? "Untitled" : document.Name,
            document.ModelUnitSystem.ToString(),
            document.Layers.CurrentLayer.FullPath,
            document.ModelAbsoluteTolerance,
            document.ModelAngleToleranceRadians,
            layers.Order(StringComparer.Ordinal).ToArray(),
            selection);
    }

    public static void EnsureFresh(RhinoDoc document, RhinoContextSnapshot snapshot)
    {
        if (document.RuntimeSerialNumber != snapshot.DocumentRuntimeSerial ||
            RhinoDoc.ActiveDoc?.RuntimeSerialNumber != snapshot.DocumentRuntimeSerial ||
            !string.Equals(document.ModelUnitSystem.ToString(), snapshot.UnitSystem, StringComparison.Ordinal) ||
            document.ModelAbsoluteTolerance != snapshot.AbsoluteTolerance ||
            document.ModelAngleToleranceRadians != snapshot.AngleToleranceRadians)
        {
            throw new RhinoExecutionException("StaleDocument", "Активный документ изменился до commit; повторите задачу в нужном документе.");
        }

        foreach (SelectedObjectSnapshot selected in snapshot.Selection)
        {
            RhinoObject? current = document.Objects.FindId(selected.ObjectId);
            if (current is null ||
                current.Geometry.DataCRC(0) != selected.GeometryCrc ||
                !string.Equals(
                    AttributeFingerprint(current, document.Layers[current.Attributes.LayerIndex]?.FullPath ?? string.Empty),
                    selected.AttributesFingerprint,
                    StringComparison.Ordinal) ||
                current.IsSelected(false) == 0)
            {
                throw new RhinoExecutionException("StaleSelection", "Выделение или геометрия изменились до commit.");
            }
        }
    }

    private static string AttributeFingerprint(RhinoObject item, string layer) =>
        $"{layer}\u001f{item.Attributes.Name}\u001f{item.Attributes.Visible}\u001f{item.IsLocked}";
}
