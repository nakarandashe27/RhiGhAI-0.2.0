using System.Text.Json;
using System.Text.Json.Serialization;

namespace RhiGhAI.Core.Contracts;

public static class TaskPlanJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();
    public static JsonElement OutputSchema { get; } = CreateSchema();

    public static TaskPlanEnvelope Parse(string json)
    {
        TaskPlanEnvelope plan = JsonSerializer.Deserialize<TaskPlanEnvelope>(json, Options)
            ?? throw new JsonException("TaskPlan is null.");
        return plan;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict
        };
        return options;
    }

    private static JsonElement CreateSchema()
    {
        using JsonDocument document = JsonDocument.Parse(SchemaJson);
        return document.RootElement.Clone();
    }

    // OpenAI structured outputs accept only a subset of JSON Schema: no "oneOf", no "const",
    // and no length/range/pattern keywords. Every numeric and textual bound below is therefore
    // enforced by TaskPlanCompiler instead; this schema only fixes the shape.
    private const string SchemaJson = """
    {
      "type":"object",
      "additionalProperties":false,
      "required":["schemaVersion","targetHost","summary","assumptions","operations"],
      "properties":{
        "schemaVersion":{"type":"integer","enum":[1]},
        "targetHost":{"type":"string","enum":["rhino"]},
        "summary":{"type":"string"},
        "assumptions":{"type":"array","items":{"type":"string"}},
        "operations":{
          "type":"array",
          "items":{"anyOf":[
            {"type":"object","additionalProperties":false,"required":["kind","id","name"],"properties":{"kind":{"type":"string","enum":["ensureLayer"]},"id":{"$ref":"#/$defs/id"},"name":{"$ref":"#/$defs/layer"}}},
            {"type":"object","additionalProperties":false,"required":["kind","id","origin","size","layer"],"properties":{"kind":{"type":"string","enum":["createBox"]},"id":{"$ref":"#/$defs/id"},"origin":{"$ref":"#/$defs/point"},"size":{"$ref":"#/$defs/size"},"layer":{"$ref":"#/$defs/layer"}}},
            {"type":"object","additionalProperties":false,"required":["kind","id","points","closed","layer"],"properties":{"kind":{"type":"string","enum":["createPolyline"]},"id":{"$ref":"#/$defs/id"},"points":{"type":"array","items":{"$ref":"#/$defs/point"}},"closed":{"type":"boolean"},"layer":{"$ref":"#/$defs/layer"}}},
            {"type":"object","additionalProperties":false,"required":["kind","id","boundaryId","layer"],"properties":{"kind":{"type":"string","enum":["createPlanarSurface"]},"id":{"$ref":"#/$defs/id"},"boundaryId":{"$ref":"#/$defs/id"},"layer":{"$ref":"#/$defs/layer"}}},
            {"type":"object","additionalProperties":false,"required":["kind","id","profileId","direction","cap","layer"],"properties":{"kind":{"type":"string","enum":["extrude"]},"id":{"$ref":"#/$defs/id"},"profileId":{"$ref":"#/$defs/id"},"direction":{"$ref":"#/$defs/vector"},"cap":{"type":"boolean"},"layer":{"$ref":"#/$defs/layer"}}},
            {"type":"object","additionalProperties":false,"required":["kind","id","mode","inputIds","layer"],"properties":{"kind":{"type":"string","enum":["boolean"]},"mode":{"type":"string","enum":["union","difference","intersection"]},"id":{"$ref":"#/$defs/id"},"inputIds":{"type":"array","items":{"$ref":"#/$defs/id"}},"layer":{"$ref":"#/$defs/layer"}}},
            {"type":"object","additionalProperties":false,"required":["kind","id","references","translation","layer"],"properties":{"kind":{"type":"string","enum":["transform"]},"id":{"$ref":"#/$defs/id"},"references":{"$ref":"#/$defs/references"},"translation":{"$ref":"#/$defs/vector"},"layer":{"type":["string","null"]}}},
            {"type":"object","additionalProperties":false,"required":["kind","id","references","translation","layer"],"properties":{"kind":{"type":"string","enum":["copy"]},"id":{"$ref":"#/$defs/id"},"references":{"$ref":"#/$defs/references"},"translation":{"$ref":"#/$defs/vector"},"layer":{"type":["string","null"]}}},
            {"type":"object","additionalProperties":false,"required":["kind","id","references","layer"],"properties":{"kind":{"type":"string","enum":["setAttributes"]},"id":{"$ref":"#/$defs/id"},"references":{"$ref":"#/$defs/references"},"layer":{"$ref":"#/$defs/layer"}}}
          ]}
        }
      },
      "$defs":{
        "id":{"type":"string"},
        "layer":{"type":"string"},
        "number":{"type":"number"},
        "point":{"type":"object","additionalProperties":false,"required":["x","y","z"],"properties":{"x":{"$ref":"#/$defs/number"},"y":{"$ref":"#/$defs/number"},"z":{"$ref":"#/$defs/number"}}},
        "vector":{"$ref":"#/$defs/point"},
        "size":{"type":"object","additionalProperties":false,"required":["x","y","z"],"properties":{"x":{"$ref":"#/$defs/number"},"y":{"$ref":"#/$defs/number"},"z":{"$ref":"#/$defs/number"}}},
        "references":{"type":"array","items":{"type":"string"}}
      }
    }
    """;
}
