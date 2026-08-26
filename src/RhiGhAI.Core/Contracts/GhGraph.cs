using System.Text.Json;

namespace RhiGhAI.Core.Contracts;

/// <summary>
/// A Grasshopper definition described by the model: real components, real wires.
/// Everything here is emitted as native Grasshopper objects the user can rewire afterwards.
/// </summary>
public sealed record GhGraphEnvelope(
    int SchemaVersion,
    string Summary,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<GhNodeSpec> Nodes,
    IReadOnlyList<GhWireSpec> Wires);

/// <summary>One canvas object. <paramref name="Component"/> is a Grasshopper component or parameter name.</summary>
public sealed record GhNodeSpec(string Id, string Component, IReadOnlyList<GhValueSpec> Values);

/// <summary>A literal typed into an input port, or a slider/panel setting.</summary>
public sealed record GhValueSpec(string Port, string Value);

public sealed record GhWireSpec(string From, string Output, string To, string Input);

public sealed record GhPortSpec(string Name, string NickName);

/// <summary>What the local Grasshopper installation actually offers; the model never invents these.</summary>
public sealed record GhComponentSpec(
    Guid ComponentGuid,
    string Name,
    string NickName,
    string Category,
    string SubCategory,
    string Description,
    IReadOnlyList<GhPortSpec> Inputs,
    IReadOnlyList<GhPortSpec> Outputs)
{
    /// <summary>Sliders, panels and toggles carry settings instead of input ports.</summary>
    public GhSpecialKind Special => Name switch
    {
        "Number Slider" => GhSpecialKind.Slider,
        "Panel" => GhSpecialKind.Panel,
        "Boolean Toggle" => GhSpecialKind.Toggle,
        _ => GhSpecialKind.None
    };
}

public enum GhSpecialKind
{
    None,
    Slider,
    Panel,
    Toggle
}

/// <summary>
/// A checked literal. <paramref name="RequestedPort"/> keeps what the model actually wrote: a
/// component with exactly one input accepts any port name, and the render is the only place the
/// user ever sees that the two differ.
/// </summary>
public sealed record GhResolvedValue(int PortIndex, string PortName, string Value, string RequestedPort = "");

public sealed record GhResolvedNode(
    string Id,
    GhComponentSpec Spec,
    IReadOnlyList<GhResolvedValue> Values,
    int Column,
    int Row);

public sealed record GhResolvedWire(
    string FromId,
    int OutputIndex,
    string ToId,
    int InputIndex,
    string RequestedOutput = "",
    string RequestedInput = "");

/// <summary>A graph that has been checked against the local catalogue and laid out left to right.</summary>
public sealed record GhGraphPlan(
    string Summary,
    IReadOnlyList<string> Assumptions,
    IReadOnlyList<GhResolvedNode> Nodes,
    IReadOnlyList<GhResolvedWire> Wires);

public static class GhGraphJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public static JsonElement OutputSchema { get; } = Parse(SchemaJson);

    public static GhGraphEnvelope ParseGraph(string json) =>
        JsonSerializer.Deserialize<GhGraphEnvelope>(json, Options) ?? throw new JsonException("GhGraph is null.");

    private static JsonElement Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    // Same OpenAI structured-output subset as TaskPlan: no oneOf, no const, no bounds.
    // Every bound below is enforced by GhGraphValidator against the live component catalogue.
    private const string SchemaJson = """
    {
      "type":"object",
      "additionalProperties":false,
      "required":["schemaVersion","summary","assumptions","nodes","wires"],
      "properties":{
        "schemaVersion":{"type":"integer","enum":[1]},
        "summary":{"type":"string"},
        "assumptions":{"type":"array","items":{"type":"string"}},
        "nodes":{
          "type":"array",
          "items":{
            "type":"object",
            "additionalProperties":false,
            "required":["id","component","values"],
            "properties":{
              "id":{"type":"string"},
              "component":{"type":"string"},
              "values":{
                "type":"array",
                "items":{
                  "type":"object",
                  "additionalProperties":false,
                  "required":["port","value"],
                  "properties":{"port":{"type":"string"},"value":{"type":"string"}}
                }
              }
            }
          }
        },
        "wires":{
          "type":"array",
          "items":{
            "type":"object",
            "additionalProperties":false,
            "required":["from","output","to","input"],
            "properties":{
              "from":{"type":"string"},
              "output":{"type":"string"},
              "to":{"type":"string"},
              "input":{"type":"string"}
            }
          }
        }
      }
    }
    """;
}
