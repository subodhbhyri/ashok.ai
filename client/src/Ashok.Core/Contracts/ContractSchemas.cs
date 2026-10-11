using System.Reflection;
using Json.Schema;

namespace Ashok.Core.Contracts;

/// <summary>The contract schemas, embedded from docs/contracts, built with one registry so $refs resolve.</summary>
public static class ContractSchemas
{
    private static readonly Lazy<IReadOnlyDictionary<string, JsonSchema>> Schemas = new(Load);

    public static JsonSchema Playbook => Schemas.Value["playbook"];

    public static JsonSchema Plan => Schemas.Value["plan"];

    public static JsonSchema UiSnapshot => Schemas.Value["ui-snapshot"];

    private static IReadOnlyDictionary<string, JsonSchema> Load()
    {
        var options = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        var assembly = typeof(ContractSchemas).Assembly;

        // plan must be built before playbook, which $refs it by $id.
        var result = new Dictionary<string, JsonSchema>();
        foreach (var name in new[] { "plan", "ui-snapshot", "playbook" })
        {
            using var stream = assembly.GetManifestResourceStream($"Ashok.Core.Contracts.{name}.schema.json")
                ?? throw new InvalidOperationException($"Embedded schema {name} is missing.");
            using var reader = new StreamReader(stream);
            result[name] = JsonSchema.FromText(reader.ReadToEnd(), options);
        }
        return result;
    }
}
