using System.Drawing;
using System.Globalization;
using System.Runtime.CompilerServices;
using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.GUI.Base;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using RhiGhAI.Core.Contracts;
using RhiGhAI.Core.Graph;
using Rhino;
using Rhino.Geometry;

namespace RhiGhAI.Grasshopper;

public sealed record GhEmitResult(int ObjectCount, int WireCount);

/// <summary>
/// Emits a checked graph as native Grasshopper objects: real components, real wires, one group.
/// Nothing here is a RhiGhAI-specific runtime — after emission the definition belongs to the user.
/// </summary>
public static class GrasshopperBridge
{
    private static readonly Guid GrasshopperPluginId = new("b45a29b1-4343-4035-989e-044e8580d9cf");

    // Objects RhiGhAI created for a conversation, so a follow-up request replaces them instead of
    // piling up. Keyed by the live GH_Document instance, not by GH_Document.DocumentID: saving a
    // definition and reopening it restores both the same DocumentID and the same InstanceGuids, and
    // those objects belong to the user — a reopened file is a different instance and never matches.
    // Inside one document ownership stays guid-based, because Grasshopper's own undo puts back
    // deserialized copies that keep their InstanceGuid but are not the same objects.
    private static readonly ConditionalWeakTable<GH_Document, Dictionary<string, List<Guid>>> Owned = new();

    public static GhComponentCatalog LoadCatalog()
    {
        EnsureEditor();
        return GhComponentCatalog.Load();
    }

    public static GhEmitResult Emit(string ownershipId, GhGraphPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        GH_DocumentEditor editor = EnsureEditor();
        editor.Show();
        GH_Document document = ActiveDocument();

        // Build and configure everything off-document first: a bad literal must not touch the canvas.
        Dictionary<string, IGH_DocumentObject> emitted = new(StringComparer.Ordinal);
        foreach (GhResolvedNode node in plan.Nodes)
        {
            EnsureEmittable(node.Spec.Name, node.Spec.SubCategory);
            IGH_DocumentObject instance = Instances.ComponentServer.EmitObject(node.Spec.ComponentGuid)
                ?? throw new TaskPlanValidationException("ComponentUnavailable", $"Grasshopper не смог создать «{node.Spec.Name}».");

            // Asked again of the object Grasshopper actually built, so a stale or tampered catalogue
            // entry cannot be the single thing standing between a plan and arbitrary code.
            EnsureEmittable(instance.Name, instance.SubCategory);
            if (instance is IGH_Component component && component.Params.Input.Count != node.Spec.Inputs.Count)
            {
                // Port indices were resolved against the catalogue's instance of this component. A
                // different shape here would move every literal and every wire one port along in silence.
                throw new TaskPlanValidationException(
                    "PortCountMismatch",
                    $"У «{node.Spec.Name}» на холсте {component.Params.Input.Count} входов вместо {node.Spec.Inputs.Count}.");
            }

            instance.CreateAttributes();
            Configure(node, instance);
            emitted[node.Id] = instance;
        }

        List<IGH_DocumentObject> additions = [.. plan.Nodes.Select(node => emitted[node.Id])];
        GH_Group group = new() { NickName = Title(plan.Summary), Colour = Color.FromArgb(60, 255, 133, 98) };
        group.CreateAttributes();
        foreach (IGH_DocumentObject instance in additions)
        {
            group.AddObject(instance.InstanceGuid);
        }

        // The group joins the additions before the undo record, so one Ctrl-Z removes it too.
        additions.Add(group);
        List<IGH_DocumentObject> removals = PreviousObjects(document, ownershipId);
        int undoBefore = document.UndoServer.UndoCount;
        bool recorded = false;
        try
        {
            if (removals.Count > 0)
            {
                document.UndoUtil.RecordRemoveObjectEvent("RhiGhAI definition", removals);
                recorded = true;
                foreach (IGH_DocumentObject stale in removals)
                {
                    document.RemoveObject(stale, false);
                }
            }

            // Measured only now. Taken before the previous graph is gone, the bounding box still
            // contains it and every repeat request lands one graph-width further to the right.
            PointF origin = Origin(document);
            foreach (GhResolvedNode node in plan.Nodes)
            {
                emitted[node.Id].Attributes.Pivot = new PointF(origin.X + (node.Column * 260f), origin.Y + (node.Row * 120f));
            }

            document.UndoUtil.RecordAddObjectEvent("RhiGhAI definition", additions);
            if (recorded && document.UndoServer.UndoCount == undoBefore + 2)
            {
                // Merge only once both records have actually landed. Grasshopper keeps none of them at
                // undo depth zero, and merging then would fold the user's previous action into ours.
                document.UndoUtil.MergeRecords(2);
            }

            // Same reasoning as the merge above: with no record on the stack, the rollback below would
            // reach past this attempt and undo whatever the user did last.
            recorded = document.UndoServer.UndoCount > undoBefore;
            foreach (IGH_DocumentObject instance in additions)
            {
                if (!document.AddObject(instance, false))
                {
                    throw new InvalidOperationException($"Grasshopper отказался добавить «{instance.Name}».");
                }
            }

            foreach (GhResolvedWire wire in plan.Wires)
            {
                IGH_Param target = InputPort(emitted[wire.ToId], wire.InputIndex);
                IGH_Param source = OutputPort(emitted[wire.FromId], wire.OutputIndex);
                target.AddSource(source);
            }

            lock (Owned)
            {
                Owned.GetOrCreateValue(document)[ownershipId] = [.. additions.Select(instance => instance.InstanceGuid)];
            }

            document.NewSolution(false);
            return new GhEmitResult(plan.Nodes.Count, plan.Wires.Count);
        }
        catch (Exception exception)
        {
            try
            {
                if (recorded)
                {
                    document.Undo();
                }

                document.NewSolution(false);
            }
            catch (Exception rollbackException)
            {
                throw new GrasshopperExecutionException(
                    "GrasshopperRollbackFailed",
                    "Grasshopper не смог откатить неудачную попытку; дальнейшее выполнение остановлено.",
                    new AggregateException(exception, rollbackException));
            }

            if (exception is TaskPlanValidationException)
            {
                throw;
            }

            throw new GrasshopperExecutionException("GrasshopperExecutionFailed", exception.Message, exception);
        }
    }

    /// <summary>Drops a conversation's ownership in every document that still holds it.</summary>
    public static void Forget(string ownershipId)
    {
        lock (Owned)
        {
            foreach (KeyValuePair<GH_Document, Dictionary<string, List<Guid>>> entry in Owned)
            {
                entry.Value.Remove(ownershipId);
            }
        }
    }

    private static void EnsureEmittable(string? name, string? subCategory)
    {
        if (GhComponentCatalog.IsBlocked(name, subCategory))
        {
            throw new TaskPlanValidationException(
                "BlockedComponent",
                $"Компонент «{name}» выполняет произвольный код и не может быть создан.");
        }
    }

    private static List<IGH_DocumentObject> PreviousObjects(GH_Document document, string ownershipId)
    {
        List<IGH_DocumentObject> previous = [];
        lock (Owned)
        {
            if (!Owned.TryGetValue(document, out Dictionary<string, List<Guid>>? byConversation) ||
                !byConversation.TryGetValue(ownershipId, out List<Guid>? ids))
            {
                return previous;
            }

            foreach (Guid id in ids)
            {
                if (document.FindObject(id, false) is { } existing)
                {
                    previous.Add(existing);
                }
            }
        }

        return previous;
    }

    private static void Configure(GhResolvedNode node, IGH_DocumentObject instance)
    {
        switch (node.Spec.Special)
        {
            case GhSpecialKind.Slider when instance is GH_NumberSlider slider:
                ConfigureSlider(slider, node.Values);
                return;
            case GhSpecialKind.Panel when instance is GH_Panel panel:
                panel.UserText = node.Values.Count > 0 ? node.Values[0].Value : string.Empty;
                return;
            case GhSpecialKind.Toggle when instance is GH_BooleanToggle toggle:
                toggle.Value = node.Values.Count > 0 && bool.Parse(node.Values[0].Value);
                return;
            default:
                break;
        }

        foreach (GhResolvedValue value in node.Values)
        {
            SetLiteral(InputPort(instance, value.PortIndex), value);
        }
    }

    private static void ConfigureSlider(GH_NumberSlider slider, IReadOnlyList<GhResolvedValue> values)
    {
        decimal minimum = 0;
        decimal maximum = 100;
        decimal current = 0;
        bool hasCurrent = false;
        int decimals = 2;
        foreach (GhResolvedValue value in values)
        {
            decimal number = decimal.Parse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            switch (value.PortName)
            {
                case "min": minimum = number; break;
                case "max": maximum = number; break;
                case "decimals": decimals = (int)Math.Clamp(number, 0, 6); break;
                default: current = number; hasCurrent = true; break;
            }
        }

        slider.Slider.Minimum = minimum;
        slider.Slider.Maximum = maximum;
        slider.Slider.DecimalPlaces = decimals;
        slider.Slider.Type = decimals == 0 ? GH_SliderAccuracy.Integer : GH_SliderAccuracy.Float;
        slider.SetSliderValue(hasCurrent ? current : minimum);
    }

    private static void SetLiteral(IGH_Param param, GhResolvedValue value)
    {
        string raw = value.Value;
        switch (param)
        {
            case Param_Integer integer:
                Append(integer, new GH_Integer(ParseInt(param, raw)));
                break;
            case Param_Number number:
                Append(number, new GH_Number(ParseDouble(param, raw)));
                break;
            case Param_Boolean boolean:
                Append(boolean, new GH_Boolean(ParseBool(param, raw)));
                break;
            case Param_String text:
                Append(text, new GH_String(raw));
                break;
            case Param_Point point:
                Append(point, new GH_Point(ParsePoint(param, raw)));
                break;
            case Param_Vector vector:
                Point3d components = ParsePoint(param, raw);
                Append(vector, new GH_Vector(new Vector3d(components.X, components.Y, components.Z)));
                break;
            case Param_Plane plane:
                Append(plane, new GH_Plane(ParsePlane(param, raw)));
                break;
            case Param_Interval interval:
                Append(interval, new GH_Interval(ParseInterval(param, raw)));
                break;
            default:
                throw new TaskPlanValidationException(
                    "UnsupportedPortValue",
                    $"Вход «{param.Name}» не принимает литерал — подайте на него компонент.");
        }
    }

    private static void Append<T>(GH_PersistentParam<T> param, T value) where T : class, IGH_Goo
    {
        param.PersistentData.Clear();
        param.PersistentData.Append(value);
    }

    private static int ParseInt(IGH_Param param, string raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : (int)Math.Round(ParseDouble(param, raw));

    private static double ParseDouble(IGH_Param param, string raw) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
            ? value
            : throw new TaskPlanValidationException("InvalidPortValue", $"Вход «{param.Name}» ожидает число, получено «{raw}».");

    private static bool ParseBool(IGH_Param param, string raw) =>
        bool.TryParse(raw, out bool value)
            ? value
            : throw new TaskPlanValidationException("InvalidPortValue", $"Вход «{param.Name}» ожидает true или false, получено «{raw}».");

    private static Point3d ParsePoint(IGH_Param param, string raw)
    {
        string[] parts = raw.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 2 or > 3)
        {
            throw new TaskPlanValidationException("InvalidPortValue", $"Вход «{param.Name}» ожидает «x,y,z», получено «{raw}».");
        }

        double z = parts.Length == 3 ? ParseDouble(param, parts[2]) : 0;
        return new Point3d(ParseDouble(param, parts[0]), ParseDouble(param, parts[1]), z);
    }

    private static Plane ParsePlane(IGH_Param param, string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "xy" or "world xy" or "worldxy" or "" => Plane.WorldXY,
        "xz" or "zx" or "world xz" or "worldxz" => Plane.WorldZX,
        "yz" or "world yz" or "worldyz" => Plane.WorldYZ,
        _ => throw new TaskPlanValidationException(
            "InvalidPortValue",
            $"Вход «{param.Name}» принимает только xy, xz или yz; для других плоскостей подайте компонент.")
    };

    private static Interval ParseInterval(IGH_Param param, string raw)
    {
        string[] parts = raw.Split(["..", ",", ";", " to "], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            throw new TaskPlanValidationException("InvalidPortValue", $"Вход «{param.Name}» ожидает «начало..конец», получено «{raw}».");
        }

        return new Interval(ParseDouble(param, parts[0]), ParseDouble(param, parts[1]));
    }

    private static IGH_Param InputPort(IGH_DocumentObject instance, int index) => instance switch
    {
        IGH_Component component => component.Params.Input[index],
        IGH_Param parameter => parameter,
        _ => throw new TaskPlanValidationException("UnsupportedComponent", $"«{instance.Name}» не принимает связи.")
    };

    private static IGH_Param OutputPort(IGH_DocumentObject instance, int index) => instance switch
    {
        IGH_Component component => component.Params.Output[index],
        IGH_Param parameter => parameter,
        _ => throw new TaskPlanValidationException("UnsupportedComponent", $"«{instance.Name}» не выдаёт данные.")
    };

    private static string Title(string summary)
    {
        string trimmed = summary.ReplaceLineEndings(" ").Trim();
        return trimmed.Length <= 60 ? $"RhiGhAI · {trimmed}" : $"RhiGhAI · {trimmed[..60]}…";
    }

    private static PointF Origin(GH_Document document)
    {
        if (document.ObjectCount > 0)
        {
            RectangleF bounds = document.BoundingBox(false);
            if (bounds.Width > 0 || bounds.Height > 0)
            {
                return new PointF(bounds.Right + 140f, bounds.Top);
            }
        }

        PointF midPoint = Instances.ActiveCanvas?.Viewport.MidPoint ?? new PointF(200, 200);
        return new PointF(midPoint.X - 300f, midPoint.Y - 100f);
    }

    private static GH_Document ActiveDocument()
    {
        GH_Document? document = Instances.ActiveCanvas?.Document;
        if (document is not null)
        {
            return document;
        }

        if (Instances.ActiveCanvas is null)
        {
            // Asked before anything is created: a document added to the server and then abandoned stays
            // there for the rest of the session, and Rhino offers to save every one of them on the way out.
            throw new GrasshopperExecutionException(
                "GrasshopperUnavailable",
                "Grasshopper canvas недоступен. Откройте Grasshopper один раз и повторите задачу.",
                new InvalidOperationException("Grasshopper canvas is unavailable."));
        }

        document = new GH_Document();
        Instances.DocumentServer.AddDocument(document);
        Instances.ActiveCanvas.Document = document;
        return document;
    }

    private static GH_DocumentEditor EnsureEditor()
    {
        if (Instances.DocumentEditor is null)
        {
            Rhino.PlugIns.PlugIn.LoadPlugIn(GrasshopperPluginId);
        }

        if (Instances.DocumentEditor is null)
        {
            // Loading the plug-in does not create the editor window; the command does.
            RhinoApp.RunScript("_Grasshopper", false);
        }

        return Instances.DocumentEditor ?? throw new GrasshopperExecutionException(
            "GrasshopperUnavailable",
            "Grasshopper ещё не готов. Откройте Grasshopper один раз и повторите задачу.",
            new InvalidOperationException("Grasshopper DocumentEditor is unavailable."));
    }
}

public sealed class GrasshopperExecutionException(string code, string message, Exception innerException)
    : Exception(message, innerException)
{
    public string Code { get; } = code;
}
