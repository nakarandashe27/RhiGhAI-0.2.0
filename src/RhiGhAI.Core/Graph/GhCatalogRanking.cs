using RhiGhAI.Core.Contracts;

namespace RhiGhAI.Core.Graph;

/// <summary>
/// Picks which installed components the prompt shows the model. Lives in Core, away from the
/// Grasshopper SDK, so the ranking can be tested without a running Grasshopper.
/// </summary>
public static class GhCatalogRanking
{
    public static readonly string[] CoreCategories =
    [
        "Params", "Maths", "Sets", "Vector", "Curve", "Surface", "Mesh", "Intersect", "Transform"
    ];

    // Ships with Rhino 8 Grasshopper; ranked like a core tab instead of a third-party plug-in.
    private static readonly string[] BundledCategories = ["Kangaroo2"];

    // Form-making components a request for "complex form" almost always needs. Always kept in the
    // prompt so a prompt that matches nothing (other language, vague wording) still offers them.
    private static readonly string[] KeyComponents =
    [
        "Loft", "Sweep1", "Sweep2", "Pipe", "Revolution", "Extrude", "Graph Mapper", "Interpolate",
        "Nurbs Curve", "Divide Curve", "Evaluate Curve", "Perp Frames", "Rotate", "Scale", "Move",
        "Twist", "Series", "Range", "Remap Numbers", "Sine", "Construct Point", "Unit Z", "Amplitude",
        "Surface From Points", "Circle", "Polygon", "Contour"
    ];

    // ponytail: prefix stems, not morphology; extend the table when a common term is missed.
    // Prompt words starting with a stem also search for the English terms (names, descriptions, tabs).
    private static readonly (string Stem, string[] Terms)[] RussianTerms =
    [
        ("лофт", ["loft"]), ("труб", ["pipe"]), ("свип", ["sweep"]), ("протян", ["sweep", "extrude"]),
        ("протяж", ["sweep", "extrude"]), ("выдав", ["extrude"]), ("экструд", ["extrude"]),
        ("волн", ["sine", "graph mapper"]), ("синус", ["sine"]), ("вращ", ["rotate", "revolution"]),
        ("поворот", ["rotate"]), ("поверн", ["rotate"]), ("закрут", ["twist", "rotate"]),
        ("скруч", ["twist", "rotate"]), ("твист", ["twist"]), ("масштаб", ["scale"]),
        ("крив", ["curve", "interpolate"]), ("сплайн", ["interpolate", "nurbs curve"]),
        ("окружн", ["circle"]), ("круг", ["circle"]), ("дуга", ["arc"]), ("дуги", ["arc"]),
        ("поверхн", ["surface"]), ("оболоч", ["surface", "loft"]), ("сетк", ["grid", "mesh"]),
        ("решет", ["grid"]), ("точк", ["point"]), ("лини", ["line"]), ("спирал", ["helix", "spiral"]),
        ("винт", ["helix"]), ("мембран", ["kangaroo"]), ("натяж", ["kangaroo"]), ("тент", ["kangaroo"]),
        ("кенгуру", ["kangaroo"]), ("релакс", ["kangaroo"]), ("формообраз", ["kangaroo"]),
        ("случайн", ["random", "jitter"]), ("шум", ["random"]), ("аттракт", ["distance", "remap"]),
        ("вороно", ["voronoi"]), ("дел", ["divide"]), ("массив", ["array", "series"]),
        ("ряд", ["series"]), ("сфер", ["sphere"]), ("коробк", ["box"]), ("цилиндр", ["cylinder"]),
        ("конус", ["cone"]), ("смещ", ["offset"]), ("оффсет", ["offset"]), ("скругл", ["fillet"]),
        ("морф", ["morph"]), ("изгиб", ["bend"]), ("деформ", ["morph", "bend"]), ("этаж", ["contour"]),
        ("сечен", ["contour", "section"]), ("контур", ["contour"]), ("плавн", ["smooth", "interpolate"]),
        ("гладк", ["smooth"]), ("хадид", ["loft", "sweep", "graph mapper"]),
        ("параметрич", ["loft", "graph mapper"])
    ];

    public static IEnumerable<GhComponentSpec> Rank(IEnumerable<GhComponentSpec> all, string? userText, int maxComponents)
    {
        string[] words = (userText ?? string.Empty)
            .Split([' ', ',', '.', ';', ':', '\n', '\r', '\t', '(', ')', '"'], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 4)
            .Select(word => word.ToLowerInvariant().Replace('ё', 'е'))
            .Distinct(StringComparer.Ordinal)
            .Take(24)
            .SelectMany(Expand)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return all
            .OrderByDescending(spec => Score(spec, words))
            .ThenBy(spec => spec.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(maxComponents, 40, 900));
    }

    private static IEnumerable<string> Expand(string word) =>
        RussianTerms.Where(entry => word.StartsWith(entry.Stem, StringComparison.Ordinal))
            .SelectMany(entry => entry.Terms)
            .Prepend(word);

    private static int Score(GhComponentSpec spec, string[] words)
    {
        int score = spec.Special != GhSpecialKind.None ? 1000 : 0;
        foreach (string word in words)
        {
            if (spec.Name.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                score += 200;
            }
            else if (spec.Category.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                score += 60;
            }
            else if (spec.Description.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                score += 40;
            }
        }

        if (KeyComponents.Contains(spec.Name, StringComparer.OrdinalIgnoreCase))
        {
            score += 300;
        }

        if (CoreCategories.Contains(spec.Category, StringComparer.OrdinalIgnoreCase) ||
            BundledCategories.Contains(spec.Category, StringComparer.OrdinalIgnoreCase))
        {
            score += 20;
        }

        return score;
    }
}
