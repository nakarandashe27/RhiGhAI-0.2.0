using System.Text.Json;
using RhiGhAI.Core.Contracts;
using RhiGhAI.Core.Graph;
using Xunit;

namespace RhiGhAI.Tests;

/// <summary>
/// OpenAI structured outputs reject "oneOf", bare "const" and every length/range/pattern keyword.
/// A schema that violates the subset makes every turn fail with invalid_json_schema, so the rules
/// are pinned here instead of being rediscovered against the live API.
/// </summary>
public sealed class OutputSchemaTests
{
    [Fact]
    public void OutputSchemaStaysInsideTheStructuredOutputsSubset()
    {
        string[] forbidden =
        [
            "oneOf", "const", "minLength", "maxLength", "minItems", "maxItems",
            "pattern", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum"
        ];

        List<string> found = [];
        Walk(TaskPlanJson.OutputSchema, found);
        Walk(GhGraphJson.OutputSchema, found);
        Assert.Empty(found.Intersect(forbidden));

        void Walk(JsonElement element, List<string> keywords)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        keywords.Add(property.Name);
                        Walk(property.Value, keywords);
                    }

                    break;
                case JsonValueKind.Array:
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        Walk(item, keywords);
                    }

                    break;
            }
        }
    }

    [Fact]
    public void EverySchemaBranchDeclaresAnObjectWithAllPropertiesRequired()
    {
        JsonElement branches = TaskPlanJson.OutputSchema
            .GetProperty("properties").GetProperty("operations").GetProperty("items").GetProperty("anyOf");

        Assert.Equal(9, branches.GetArrayLength());
        foreach (JsonElement branch in branches.EnumerateArray())
        {
            Assert.Equal("object", branch.GetProperty("type").GetString());
            Assert.False(branch.GetProperty("additionalProperties").GetBoolean());
            HashSet<string> properties = branch.GetProperty("properties").EnumerateObject().Select(item => item.Name).ToHashSet();
            HashSet<string> required = branch.GetProperty("required").EnumerateArray().Select(item => item.GetString()!).ToHashSet();
            Assert.Equal(properties, required);
        }
    }

    [Fact]
    public void RealCodexResponseParsesAndCompiles()
    {
        // Captured verbatim from gpt-5.6-sol through codex app-server with this exact output schema.
        const string json = """
        {"schemaVersion":1,"targetHost":"rhino","summary":"Create a 2400 × 1200 × 18 mm panel on the Panels layer.","assumptions":["The panel is an axis-aligned box with its lower corner at the world origin (0, 0, 0).","Dimensions map to X = 2400 mm, Y = 1200 mm, and Z = 18 mm."],"operations":[{"kind":"ensureLayer","id":"ensure_panels_layer","name":"Panels"},{"kind":"createBox","id":"panel_2400x1200x18","origin":{"x":0,"y":0,"z":0},"size":{"x":2400,"y":1200,"z":18},"layer":"Panels"}]}
        """;

        TaskPlanEnvelope plan = TaskPlanJson.Parse(json);
        OperationGraph graph = TaskPlanCompiler.Compile(plan, new ValidationContext(new HashSet<string>()));

        Assert.Equal(2, graph.Nodes.Length);
        Assert.Contains(graph.Nodes, node => node.OperationKind == "createBox");
    }

}
