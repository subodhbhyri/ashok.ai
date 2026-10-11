using System.Text.Json;
using System.Text.Json.Nodes;
using Ashok.Core.Contracts;
using Json.Schema;
using YamlDotNet.Serialization;

namespace Ashok.Core.Playbooks;

public sealed class PlaybookException(string file, IReadOnlyList<string> problems)
    : Exception($"{file}: {string.Join("; ", problems)}")
{
    public string File { get; } = file;

    public IReadOnlyList<string> Problems { get; } = problems;
}

/// <summary>Reads a playbook YAML file, validates it against playbook.schema.json, and returns the typed record.</summary>
public static class PlaybookLoader
{
    private static readonly IDeserializer Yaml = new DeserializerBuilder().Build();

    public static Playbook Parse(string yaml, string source)
    {
        JsonNode? node;
        try
        {
            node = ToJson(Yaml.Deserialize<object?>(yaml));
        }
        catch (Exception e) when (e is YamlDotNet.Core.YamlException)
        {
            throw new PlaybookException(source, [$"not valid YAML ({e.Message})"]);
        }

        if (node is not JsonObject)
            throw new PlaybookException(source, ["the file must contain a YAML mapping"]);

        var result = ContractSchemas.Playbook.Evaluate(JsonSerializer.SerializeToElement(node),
            new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (!result.IsValid)
            throw new PlaybookException(source, Problems(result));

        var playbook = node.Deserialize<Playbook>(ContractJson.Options)
            ?? throw new PlaybookException(source, ["could not be read"]);

        // The schema cannot express uniqueness of step ids.
        var duplicate = playbook.Steps.GroupBy(s => s.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new PlaybookException(source, [$"step id '{duplicate.Key}' is used more than once"]);
        return playbook;
    }

    public static Playbook Load(string path) => Parse(File.ReadAllText(path), Path.GetFileName(path));

    private static List<string> Problems(EvaluationResults result)
    {
        var problems = new List<string>();
        foreach (var detail in result.Details ?? [])
        {
            if (detail.Errors is null) continue;
            foreach (var (keyword, message) in detail.Errors!)
                problems.Add($"{detail.InstanceLocation} {keyword}: {message}");
        }
        return problems.Count > 0 ? problems : ["does not match the playbook schema"];
    }

    // YAML scalars all arrive as strings, which is what every playbook field is.
    private static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        IDictionary<object, object> map => new JsonObject(map.Select(kv =>
            new KeyValuePair<string, JsonNode?>(kv.Key.ToString() ?? "", ToJson(kv.Value)))),
        IEnumerable<object> list => new JsonArray(list.Select(ToJson).ToArray()),
        _ => JsonValue.Create(value.ToString()),
    };
}
