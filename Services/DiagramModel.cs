using System.Collections.Generic;

namespace Levity.Services
{
    /// <summary>Hard limits for a diagram. They keep the graph readable and the messages small enough for the relay.</summary>
    public static class DiagramLimits
    {
        public const int MaxGroups = 10;
        public const int MaxNodes = 34;
        public const int MaxEdges = 48;
        public const int MaxLabel = 72;
        public const int MaxOverview = 900;
        public const int MaxPath = 512;
        public const int MaxStoredBytes = 200_000;
    }

    /// <summary>How well the code backs a relationship. Only the code, never the model, can make an edge Verified.</summary>
    public enum EdgeSupport
    {
        /// <summary>A file on one side references the other side (found by Levity's own reference index).</summary>
        Verified,
        /// <summary>The README states it.</summary>
        Documented,
        /// <summary>Plausible but not found in the code Levity could read. Drawn dashed.</summary>
        Inferred,
    }

    public sealed record DiagramGroup(string Id, string Label);

    /// <param name="Group">Group id, or null for a node outside any group (for example an external system).</param>
    /// <param name="Path">Verified repo-relative path (forward slashes), or null.</param>
    /// <param name="Shape">box | database | circle | hexagon</param>
    public sealed record DiagramNode(string Id, string Label, string? Group, string? Path, string Shape);

    public sealed record DiagramEdge(string From, string To, string? Label, string? EvidencePath, EdgeSupport Support);

    /// <summary>A diagram that has passed validation. Constructed only by <see cref="DiagramValidator"/>.</summary>
    public sealed record DiagramGraph(string Overview, IReadOnlyList<DiagramGroup> Groups, IReadOnlyList<DiagramNode> Nodes, IReadOnlyList<DiagramEdge> Edges);

    /// <summary>The JSON Schema agy is asked to follow. Output is still validated by <see cref="DiagramValidator"/>; the schema only steers it.</summary>
    public static class DiagramSchema
    {
        public const string Json = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["overview", "groups", "nodes", "edges"],
          "properties": {
            "overview": { "type": "string", "maxLength": 900 },
            "groups": {
              "type": "array", "maxItems": 10,
              "items": {
                "type": "object", "additionalProperties": false, "required": ["id", "label"],
                "properties": {
                  "id": { "type": "string", "pattern": "^[a-z][a-z0-9_]{0,31}$" },
                  "label": { "type": "string", "maxLength": 72 }
                }
              }
            },
            "nodes": {
              "type": "array", "minItems": 1, "maxItems": 34,
              "items": {
                "type": "object", "additionalProperties": false, "required": ["id", "label"],
                "properties": {
                  "id": { "type": "string", "pattern": "^[a-z][a-z0-9_]{0,31}$" },
                  "label": { "type": "string", "maxLength": 72 },
                  "group": { "type": "string" },
                  "path": { "type": "string", "maxLength": 512 },
                  "shape": { "type": "string", "enum": ["box", "database", "circle", "hexagon"] }
                }
              }
            },
            "edges": {
              "type": "array", "maxItems": 48,
              "items": {
                "type": "object", "additionalProperties": false, "required": ["from", "to"],
                "properties": {
                  "from": { "type": "string" },
                  "to": { "type": "string" },
                  "label": { "type": "string", "maxLength": 72 },
                  "evidencePath": { "type": "string", "maxLength": 512 }
                }
              }
            }
          }
        }
        """;
    }
}
