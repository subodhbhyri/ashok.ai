using System.Text.Json;
using Json.Schema;

namespace Ashok.Core.Tests;

public class ContractFixtureTests
{
    private static readonly string ContractsDir = FindContractsDir();

    private static readonly Dictionary<string, JsonSchema> Schemas = LoadSchemas();

    public static TheoryData<string, string> Fixtures()
    {
        var data = new TheoryData<string, string>();
        foreach (var kind in new[] { "valid", "invalid" })
        foreach (var name in new[] { "ui-snapshot", "plan", "playbook" })
        {
            var dir = Path.Combine(ContractsDir, "fixtures", name, kind);
            foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
                data.Add(name, Path.GetRelativePath(Path.Combine(ContractsDir, "fixtures"), file));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_validates_or_fails_as_expected(string schemaName, string relativePath)
    {
        var expectValid = relativePath.Split(Path.DirectorySeparatorChar)[1] == "valid";
        var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractsDir, "fixtures", relativePath))).RootElement;

        var result = Schemas[schemaName].Evaluate(json);

        Assert.Equal(expectValid, result.IsValid);
    }

    // One shared registry so playbook.schema.json can $ref plan.schema.json by its $id.
    private static Dictionary<string, JsonSchema> LoadSchemas()
    {
        var options = new BuildOptions { SchemaRegistry = new SchemaRegistry() };
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
