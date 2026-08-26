using System.Text;
using Grasshopper;
using Grasshopper.Kernel;
using RhiGhAI.Core.Contracts;
using RhiGhAI.Core.Graph;

namespace RhiGhAI.Grasshopper;

/// <summary>
/// The components this Grasshopper installation actually offers, read from the live component
/// server. The model may only use names that appear here, so plans stay emittable on this machine.
/// </summary>
public sealed class GhComponentCatalog : IGhCatalog
{
    // Script components would let a plan run arbitrary code; RhiGhAI never emits them. Names alone
    // cannot carry that rule — they are renameable, localisable, and the installed set is wider than
    // any hand-written list: "IronPython 2 Script" was reachable while this list looked complete.
    // The gate is therefore the subcategory, which every scripting component in Rhino 8 shares
    // (Maths > Script), with names and library ids kept as additional layers rather than the only one.
    private const string ScriptSubCategory = "Script";

    // Both libraries publish nothing but scripting components on a stock Rhino 8.
    private static readonly Guid[] ScriptLibraries =
    [
        new("066d0a87-236f-4eae-a0f4-9e42f5327962"), // RhinoCodePluginGH: Script, C#, Python 3, IronPython 2
        new("df133822-d950-4636-bf23-902812c6aed2")  // ScriptComponents: VB Script and the DotNET LEGACY pair
    ];

    private static readonly string[] BlockedNames =
    [
        "C# Script", "VB Script", "Python Script", "Python 3 Script", "GhPython Script", "Script",
        "IronPython 2 Script", "C# Script (Legacy)", "VB Script (Legacy)",
        "DotNET C# Script (LEGACY)", "DotNET VB Script (LEGACY)",
        // Hops runs a definition fetched over the network. Not installed here, blocked by name anyway.
        "Hops"
    ];

    private static readonly string[] CoreCategories =
    [
        "Params", "Maths", "Sets", "Vector", "Curve", "Surface", "Mesh", "Intersect", "Transform"
    ];

    private static readonly object CacheGate = new();
    private static GhComponentCatalog? _cached;
    private static int _cachedProxyCount;

    private readonly Dictionary<string, GhComponentSpec> _byName;
    private readonly IReadOnlyList<GhComponentSpec> _all;

    private GhComponentCatalog(IReadOnlyList<GhComponentSpec> all)
    {
        _all = all;
        _byName = new Dictionary<string, GhComponentSpec>(StringComparer.OrdinalIgnoreCase);

        // Two plug-ins may both publish a "Circle" or a "Series". Whoever happened to be enumerated
        // first used to win, which depends on .gha load order; Grasshopper's own tabs win instead, so
        // the prompt and the canvas agree on what a bare name means.
        foreach (GhComponentSpec spec in all)
        {
            if (CoreCategories.Contains(spec.Category, StringComparer.OrdinalIgnoreCase))
            {
                _byName.TryAdd(spec.Name, spec);
            }
        }

        foreach (GhComponentSpec spec in all)
        {
            _byName.TryAdd(spec.Name, spec);
        }

        foreach (GhComponentSpec spec in all)
        {
            // Nicknames are ambiguous (and sometimes empty), so they only fill gaps left by real names.
            if (!string.IsNullOrWhiteSpace(spec.NickName))
            {
                _byName.TryAdd(spec.NickName, spec);
            }
        }
    }

    public int Count => _all.Count;

    /// <summary>
    /// True for anything that can run code, or evaluate an expression the user never wrote.
    /// Public because the emitter asks the same question a second time about the object Grasshopper
    /// actually built, so this catalogue is not the only place such a component can be stopped.
    /// </summary>
    public static bool IsBlocked(string? name, string? subCategory, Guid libraryGuid = default) =>
        string.Equals(subCategory?.Trim(), ScriptSubCategory, StringComparison.OrdinalIgnoreCase) ||
        ScriptLibraries.Contains(libraryGuid) ||
        BlockedNames.Contains(name?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the component server once per Rhino session (~60 ms for a thousand components).
    /// Grasshopper loads libraries lazily, so the cache is rebuilt whenever the number of proxies
    /// moves: an early request would otherwise freeze a half-loaded catalogue for the whole session.
    /// </summary>
    public static GhComponentCatalog Load()
    {
        // Contended only in theory — every caller arrives on the Rhino UI thread — but two overlapping
        // refreshes used to instantiate ~1500 proxies twice, running third-party constructors twice with it.
        lock (CacheGate)
        {
            int proxyCount = Instances.ComponentServer.ObjectProxies.Count;
            if (_cached is not null && _cachedProxyCount == proxyCount)
            {
                return _cached;
            }

            List<GhComponentSpec> specs = [];
            foreach (IGH_ObjectProxy proxy in Instances.ComponentServer.ObjectProxies)
            {
                if (proxy is null || proxy.Obsolete || proxy.Kind != GH_ObjectType.CompiledObject || proxy.Exposure == GH_Exposure.hidden)
                {
                    continue;
                }

                string name = proxy.Desc?.Name ?? string.Empty;
                if (name.Length == 0 || IsBlocked(name, proxy.Desc?.SubCategory, proxy.LibraryGuid))
                {
                    continue;
                }

                GhComponentSpec? spec = Describe(proxy);
                if (spec is not null)
                {
                    specs.Add(spec);
                }
            }

            _cachedProxyCount = proxyCount;
            _cached = new GhComponentCatalog(specs);
            return _cached;
        }
    }

    public bool TryFind(string name, out GhComponentSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(name) && _byName.TryGetValue(name.Trim(), out GhComponentSpec? found))
        {
            spec = found;
            return true;
        }

        spec = null!;
        return false;
    }

    public IReadOnlyList<string> Suggest(string name, int count)
    {
        string needle = (name ?? string.Empty).Trim();
        if (needle.Length < 2)
        {
            return [];
        }

        return [.. _all
            .Where(spec => spec.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                           needle.Contains(spec.Name, StringComparison.OrdinalIgnoreCase))
            .Select(spec => spec.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(count, 1, 20))];
    }

    /// <summary>
    /// Compact catalogue for the prompt: everything relevant to this request first, then the
    /// components Grasshopper itself puts on the front of its tabs.
    /// </summary>
    public string Describe(string userText, int maxComponents = 240)
    {
        string[] words = (userText ?? string.Empty)
            .Split([' ', ',', '.', ';', ':', '\n', '\r', '\t', '(', ')', '"'], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 4)
            .Select(word => word.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .Take(24)
            .ToArray();

        StringBuilder builder = new();
        foreach (GhComponentSpec spec in _all
            .OrderByDescending(spec => Score(spec, words))
            .ThenBy(spec => spec.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxComponents, 40, 900)))
        {
            builder.Append(spec.Name).Append(" | ").Append(spec.Category).Append('>').Append(spec.SubCategory);
            builder.Append(" | in: ").Append(Ports(spec.Inputs));
            builder.Append(" | out: ").Append(Ports(spec.Outputs));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    private static int Score(GhComponentSpec spec, string[] words)
    {
        int score = spec.Special != GhSpecialKind.None ? 1000 : 0;
        foreach (string word in words)
        {
            if (spec.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                score += 200;
            }
            else if (spec.Description.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                score += 40;
            }
        }

        if (CoreCategories.Contains(spec.Category, StringComparer.OrdinalIgnoreCase))
        {
            score += 20;
        }

        return score;
    }

    private static string Ports(IReadOnlyList<GhPortSpec> ports) =>
        ports.Count == 0 ? "—" : string.Join(", ", ports.Select(port => port.Name));

    private static GhComponentSpec? Describe(IGH_ObjectProxy proxy)
    {
        try
        {
            IGH_DocumentObject instance = proxy.CreateInstance();
            List<GhPortSpec> inputs = [];
            List<GhPortSpec> outputs = [];
            switch (instance)
            {
                case IGH_Component component:
                    inputs.AddRange(component.Params.Input.Select(param => new GhPortSpec(param.Name, param.NickName)));
                    outputs.AddRange(component.Params.Output.Select(param => new GhPortSpec(param.Name, param.NickName)));
                    break;
                case IGH_Param parameter:
                    // A floating parameter is its own single input and its own single output.
                    inputs.Add(new GhPortSpec(parameter.Name, parameter.NickName));
                    outputs.Add(new GhPortSpec(parameter.Name, parameter.NickName));
                    break;
                default:
                    return null;
            }

            IGH_InstanceDescription description = proxy.Desc;
            return new GhComponentSpec(
                proxy.Guid,
                description.Name,
                description.NickName ?? description.Name,
                description.Category ?? string.Empty,
                description.SubCategory ?? string.Empty,
                description.Description ?? string.Empty,
                inputs,
                outputs);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // A third-party component that cannot be instantiated simply stays out of the catalogue.
            return null;
        }
    }
}
