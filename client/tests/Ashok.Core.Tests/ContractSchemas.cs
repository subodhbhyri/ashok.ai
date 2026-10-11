using Json.Schema;

namespace Ashok.Core.Tests;

/// <summary>Loads docs/contracts/*.schema.json with one shared registry so cross-schema $refs resolve.</summary>
public static class ContractSchemas
{
    public static readonly string ContractsDir = FindContractsDir();

    private static readonly Dictionary<string, JsonSchema> Schemas = Load();

    public static JsonSchema Get(string name) => Schemas[name];

    private static Dictionary<string, JsonSchema> Load()
    {
        var options = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
        // plan must be built before playbook, which $refs it by $id.
        return new[] { "plan", "ui-snapshot", "playbook" }.ToDictionary(
            name => name,
            name => JsonSchema.FromText(File.ReadAllText(Path.Combine(ContractsDir, $"{name}.schema.json")), options));
    }

    private static string FindContractsDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "contracts");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("docs/contracts not found above " + AppContext.BaseDirectory);
    }
}
