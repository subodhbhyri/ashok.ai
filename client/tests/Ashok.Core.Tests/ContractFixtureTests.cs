using System.Text.Json;

namespace Ashok.Core.Tests;

public class ContractFixtureTests
{
    public static TheoryData<string, string> Fixtures()
    {
        var data = new TheoryData<string, string>();
        var fixturesDir = Path.Combine(ContractSchemas.ContractsDir, "fixtures");
        foreach (var kind in new[] { "valid", "invalid" })
        foreach (var name in new[] { "ui-snapshot", "plan", "playbook" })
        {
            var dir = Path.Combine(fixturesDir, name, kind);
            foreach (var file in Directory.GetFiles(dir, "*.json").OrderBy(f => f))
                data.Add(name, Path.GetRelativePath(fixturesDir, file));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_validates_or_fails_as_expected(string schemaName, string relativePath)
    {
        var expectValid = relativePath.Split(Path.DirectorySeparatorChar)[1] == "valid";
        var json = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(ContractSchemas.ContractsDir, "fixtures", relativePath))).RootElement;

        Assert.Equal(expectValid, ContractSchemas.Get(schemaName).Evaluate(json).IsValid);
    }
}
