using System.Globalization;
using System.Text;
using RhiGhAI.Core.Contracts;

namespace RhiGhAI.Core.Graph;

/// <summary>The set of components this Grasshopper installation can actually emit.</summary>
public interface IGhCatalog
{
    bool TryFind(string name, out GhComponentSpec spec);

    /// <summary>Closest known names, used to tell the model what it should have written.</summary>
    IReadOnlyList<string> Suggest(string name, int count);
}

/// <summary>
/// Checks a model-authored Grasshopper graph against the local catalogue and lays it out
/// left to right. Nothing reaches the canvas before this passes.
/// </summary>
public static class GhGraphCompiler
{
    private const int MaxNodes = 80;
    private const int MaxWires = 240;
    private const int MaxValueLength = 200;

    // A slider carries at most min, max, value and decimals; a panel or a toggle carries one setting.
    private const int MaxSpecialValues = 4;

    // decimal, which the slider is built on, tops out just under 7.9e28. A literal above this bound
    // parses as a finite double here and then throws OverflowException inside Grasshopper.
    private const double SliderBound = 1e28;

    public static GhGraphPlan Compile(GhGraphEnvelope graph, IGhCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(catalog);
        if (graph.SchemaVersion != 1)
        {
            throw new TaskPlanValidationException("UnsupportedSchemaVersion", $"Поддерживается только schemaVersion 1, получено {graph.SchemaVersion}.");
        }

        if (graph.Summary is not { Length: > 0 and <= 400 } ||
            graph.Assumptions is null ||
            graph.Assumptions.Count > 12 ||
            graph.Assumptions.Any(item => item is null || item.Length > 300))
        {
            throw new TaskPlanValidationException("InvalidEnvelope", "Summary или assumptions выходят за границы контракта.");
        }

        if (graph.Nodes is not { Count: > 0 and <= MaxNodes })
        {
            throw new TaskPlanValidationException("NodeLimit", $"Определение должно содержать от 1 до {MaxNodes} компонентов.");
        }

        if (graph.Wires is null || graph.Wires.Count > MaxWires)
        {
            throw new TaskPlanValidationException("WireLimit", $"Определение должно содержать не более {MaxWires} связей.");
        }

        Dictionary<string, GhNodeSpec> byId = new(StringComparer.Ordinal);
        Dictionary<string, GhComponentSpec> specs = new(StringComparer.Ordinal);
        foreach (GhNodeSpec node in graph.Nodes)
        {
            if (node is null || !IsValidId(node.Id) || !byId.TryAdd(node.Id, node))
            {
                throw new TaskPlanValidationException("InvalidNodeId", $"Некорректный или повторный id узла: {node?.Id}.");
            }

            if (string.IsNullOrWhiteSpace(node.Component) || !catalog.TryFind(node.Component, out GhComponentSpec spec))
            {
                IReadOnlyList<string> suggestions = catalog.Suggest(node.Component ?? string.Empty, 6);
                string hint = suggestions.Count > 0 ? $" Возможные компоненты: {string.Join(", ", suggestions)}." : string.Empty;
                throw new TaskPlanValidationException(
                    "UnknownComponent",
                    $"Компонент «{node.Component}» отсутствует в этой установке Grasshopper.{hint}");
            }

            specs[node.Id] = spec;
        }

        Dictionary<string, List<GhResolvedValue>> values = new(StringComparer.Ordinal);
        foreach (GhNodeSpec node in graph.Nodes)
        {
            values[node.Id] = ResolveValues(node, specs[node.Id]);
        }

        List<GhResolvedWire> wires = [];
        HashSet<string> occupied = new(StringComparer.Ordinal);
        foreach (GhWireSpec wire in graph.Wires)
        {
            if (wire is null || !byId.ContainsKey(wire.From ?? string.Empty) || !byId.ContainsKey(wire.To ?? string.Empty))
            {
                throw new TaskPlanValidationException("UnknownWireEndpoint", $"Связь ссылается на несуществующий узел: {wire?.From} → {wire?.To}.");
            }

            string fromId = wire.From!;
            string toId = wire.To!;
            if (string.Equals(fromId, toId, StringComparison.Ordinal))
            {
                throw new TaskPlanValidationException("SelfWire", $"Узел {fromId} не может быть соединён сам с собой.");
            }

            GhComponentSpec source = specs[fromId];
            GhComponentSpec target = specs[toId];
            int outputIndex = ResolvePort(source.Outputs, wire.Output);
            if (outputIndex < 0)
            {
                throw new TaskPlanValidationException(
                    "UnknownOutputPort",
                    $"У компонента «{source.Name}» нет выхода «{wire.Output}». Доступные выходы: {PortList(source.Outputs)}.");
            }

            int inputIndex = ResolvePort(target.Inputs, wire.Input);
            if (inputIndex < 0)
            {
                throw new TaskPlanValidationException(
                    "UnknownInputPort",
                    $"У компонента «{target.Name}» нет входа «{wire.Input}». Доступные входы: {PortList(target.Inputs)}.");
            }

            // A literal typed into a port that also receives a wire would be dead data on the canvas.
            occupied.Add($"{toId}#{inputIndex}");
            wires.Add(new GhResolvedWire(
                fromId,
                outputIndex,
                toId,
                inputIndex,
                (wire.Output ?? string.Empty).Trim(),
                (wire.Input ?? string.Empty).Trim()));
        }

        foreach ((string id, List<GhResolvedValue> nodeValues) in values)
        {
            nodeValues.RemoveAll(value => value.PortIndex >= 0 && occupied.Contains($"{id}#{value.PortIndex}"));
        }

        IReadOnlyDictionary<string, (int Column, int Row)> layout = Layout(graph, wires);
        List<GhResolvedNode> nodes = [];
        foreach (GhNodeSpec node in graph.Nodes)
        {
            (int column, int row) = layout[node.Id];
            nodes.Add(new GhResolvedNode(node.Id, specs[node.Id], values[node.Id], column, row));
        }

        return new GhGraphPlan(graph.Summary, graph.Assumptions, nodes, wires);
    }

    /// <summary>Readable listing of the checked definition, shown before anything is emitted.</summary>
    public static string Render(GhGraphPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        CultureInfo culture = CultureInfo.InvariantCulture;
        StringBuilder builder = new();
        builder.AppendLine(culture, $"// Grasshopper: {plan.Summary}");
        foreach (string assumption in plan.Assumptions)
        {
            builder.AppendLine(culture, $"// допущение: {assumption}");
        }

        builder.AppendLine();
        foreach (GhResolvedNode node in plan.Nodes.OrderBy(item => item.Column).ThenBy(item => item.Row))
        {
            string settings = node.Values.Count == 0
                ? string.Empty
                : $"  {{ {string.Join(", ", node.Values.Select(value => $"{Named(value.PortName, value.RequestedPort)} = {value.Value}"))} }}";
            builder.AppendLine(culture, $"{node.Id} = {node.Spec.Name}({PortList(node.Spec.Inputs)}){settings}");
        }

        if (plan.Wires.Count > 0)
        {
            builder.AppendLine();
        }

        Dictionary<string, GhResolvedNode> byId = plan.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        foreach (GhResolvedWire wire in plan.Wires)
        {
            GhResolvedNode from = byId[wire.FromId];
            GhResolvedNode to = byId[wire.ToId];
            builder.AppendLine(
                culture,
                $"{wire.FromId}.{Named(from.Spec.Outputs[wire.OutputIndex].Name, wire.RequestedOutput)} → " +
                $"{wire.ToId}.{Named(to.Spec.Inputs[wire.InputIndex].Name, wire.RequestedInput)}");
        }

        return builder.ToString();
    }

    private static List<GhResolvedValue> ResolveValues(GhNodeSpec node, GhComponentSpec spec)
    {
        int maxValues = spec.Special == GhSpecialKind.None ? spec.Inputs.Count : MaxSpecialValues;
        if ((node.Values?.Count ?? 0) > maxValues)
        {
            throw new TaskPlanValidationException(
                "ValueLimit",
                $"У узла {node.Id} значений больше, чем компонент «{spec.Name}» принимает ({maxValues}).");
        }

        List<GhResolvedValue> resolved = [];
        HashSet<int> used = [];
        HashSet<string> usedSettings = new(StringComparer.Ordinal);
        foreach (GhValueSpec value in node.Values ?? [])
        {
            if (value is null || string.IsNullOrWhiteSpace(value.Port) || value.Value is null || value.Value.Length > MaxValueLength)
            {
                throw new TaskPlanValidationException("InvalidValue", $"Некорректное значение на узле {node.Id}.");
            }

            if (spec.Special != GhSpecialKind.None)
            {
                // Duplicates have to die here too: the checks below read the first setting of a name
                // and the emitter keeps the last, so a repeated "min" is validated and then discarded.
                string setting = value.Port.Trim().ToLowerInvariant();
                if (!usedSettings.Add(setting))
                {
                    throw new TaskPlanValidationException("DuplicateValue", $"Настройка «{setting}» узла {node.Id} задана дважды.");
                }

                resolved.Add(new GhResolvedValue(-1, setting, value.Value.Trim(), value.Port.Trim()));
                continue;
            }

            int index = ResolvePort(spec.Inputs, value.Port);
            if (index < 0)
            {
                throw new TaskPlanValidationException(
                    "UnknownInputPort",
                    $"У компонента «{spec.Name}» нет входа «{value.Port}». Доступные входы: {PortList(spec.Inputs)}.");
            }

            if (!used.Add(index))
            {
                throw new TaskPlanValidationException("DuplicateValue", $"Вход «{value.Port}» узла {node.Id} задан дважды.");
            }

            resolved.Add(new GhResolvedValue(index, spec.Inputs[index].Name, value.Value.Trim(), value.Port.Trim()));
        }

        return spec.Special switch
        {
            GhSpecialKind.Slider => ValidateSlider(node.Id, resolved),
            GhSpecialKind.Toggle => ValidateToggle(node.Id, resolved),
            GhSpecialKind.Panel => ValidatePanel(node.Id, resolved),
            _ => resolved
        };
    }

    private static List<GhResolvedValue> ValidateSlider(string nodeId, List<GhResolvedValue> values)
    {
        double Read(string port, double fallback)
        {
            GhResolvedValue? value = values.Find(item => item.PortName == port);
            if (value is null)
            {
                return fallback;
            }

            if (!double.TryParse(value.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) || !double.IsFinite(parsed))
            {
                throw new TaskPlanValidationException("InvalidSlider", $"Слайдер {nodeId}: «{port}» должен быть числом, получено «{value.Value}».");
            }

            if (Math.Abs(parsed) > SliderBound)
            {
                throw new TaskPlanValidationException(
                    "InvalidSlider",
                    $"Слайдер {nodeId}: «{port}» выходит за диапазон значений слайдера, получено «{value.Value}».");
            }

            return parsed;
        }

        foreach (GhResolvedValue value in values)
        {
            if (value.PortName is not ("min" or "max" or "value" or "decimals"))
            {
                throw new TaskPlanValidationException(
                    "InvalidSlider",
                    $"Слайдер {nodeId} принимает только min, max, value и decimals; получено «{value.PortName}».");
            }
        }

        GhResolvedValue? places = values.Find(item => item.PortName == "decimals");
        if (places is not null &&
            (!int.TryParse(places.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int decimals) || decimals is < 0 or > 6))
        {
            // Never parsed before, yet the emitter runs decimal.Parse over every setting including this one.
            throw new TaskPlanValidationException(
                "InvalidSlider",
                $"Слайдер {nodeId}: «decimals» должен быть целым от 0 до 6, получено «{places.Value}».");
        }

        double minimum = Read("min", 0);
        double maximum = Read("max", 100);
        double current = Read("value", minimum);
        if (minimum >= maximum)
        {
            throw new TaskPlanValidationException("InvalidSlider", $"Слайдер {nodeId}: min должен быть меньше max.");
        }

        if (current < minimum || current > maximum)
        {
            throw new TaskPlanValidationException("InvalidSlider", $"Слайдер {nodeId}: value должно лежать между min и max.");
        }

        return values;
    }

    private static List<GhResolvedValue> ValidatePanel(string nodeId, List<GhResolvedValue> values)
    {
        // The emitter writes values[0] into the panel, so a second setting would vanish without a word.
        if (values.Count > 1)
        {
            throw new TaskPlanValidationException("InvalidPanel", $"Панель {nodeId} принимает ровно один текст.");
        }

        return values;
    }

    private static List<GhResolvedValue> ValidateToggle(string nodeId, List<GhResolvedValue> values)
    {
        foreach (GhResolvedValue value in values)
        {
            if (value.PortName != "value" || !bool.TryParse(value.Value, out _))
            {
                throw new TaskPlanValidationException("InvalidToggle", $"Переключатель {nodeId} принимает только value = true|false.");
            }
        }

        return values;
    }

    private static IReadOnlyDictionary<string, (int Column, int Row)> Layout(GhGraphEnvelope graph, List<GhResolvedWire> wires)
    {
        Dictionary<string, List<string>> outgoing = new(StringComparer.Ordinal);
        Dictionary<string, int> incoming = new(StringComparer.Ordinal);
        foreach (GhNodeSpec node in graph.Nodes)
        {
            outgoing[node.Id] = [];
            incoming[node.Id] = 0;
        }

        foreach (GhResolvedWire wire in wires)
        {
            outgoing[wire.FromId].Add(wire.ToId);
            incoming[wire.ToId]++;
        }

        Dictionary<string, int> column = graph.Nodes.ToDictionary(node => node.Id, _ => 0, StringComparer.Ordinal);
        Queue<string> ready = new(incoming.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        int visited = 0;
        while (ready.Count > 0)
        {
            string current = ready.Dequeue();
            visited++;
            foreach (string next in outgoing[current])
            {
                column[next] = Math.Max(column[next], column[current] + 1);
                if (--incoming[next] == 0)
                {
                    ready.Enqueue(next);
                }
            }
        }

        if (visited != graph.Nodes.Count)
        {
            throw new TaskPlanValidationException("CyclicGraph", "Определение содержит цикл; Grasshopper не выполняет циклические связи.");
        }

        Dictionary<string, (int Column, int Row)> layout = new(StringComparer.Ordinal);
        Dictionary<int, int> rows = [];
        foreach (GhNodeSpec node in graph.Nodes.OrderBy(node => column[node.Id]))
        {
            int nodeColumn = column[node.Id];
            rows.TryGetValue(nodeColumn, out int row);
            rows[nodeColumn] = row + 1;
            layout[node.Id] = (nodeColumn, row);
        }

        return layout;
    }

    private static int ResolvePort(IReadOnlyList<GhPortSpec> ports, string? requested)
    {
        // A single-port object (a slider, a floating parameter) has only one possible answer,
        // whatever the model called it.
        if (ports.Count == 1 || string.IsNullOrWhiteSpace(requested))
        {
            return ports.Count == 1 ? 0 : -1;
        }

        string name = requested.Trim();
        for (int index = 0; index < ports.Count; index++)
        {
            if (string.Equals(ports[index].Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ports[index].NickName, name, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return int.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out int position) && position >= 0 && position < ports.Count
            ? position
            : -1;
    }

    /// <summary>
    /// The port that was used, plus what the model asked for when the two differ. An object with a
    /// single port accepts any name, so this is where a misspelling becomes visible rather than silent.
    /// </summary>
    private static string Named(string resolved, string requested) =>
        requested.Length == 0 || string.Equals(resolved, requested, StringComparison.OrdinalIgnoreCase)
            ? resolved
            : $"{resolved} (запрошен «{requested}»)";

    private static string PortList(IReadOnlyList<GhPortSpec> ports) =>
        ports.Count == 0 ? "—" : string.Join(", ", ports.Select(port => port.Name));

    private static bool IsValidId(string? id) =>
        id is { Length: > 0 and <= 40 } && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
}
