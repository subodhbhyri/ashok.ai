using Ashok.Core.Contracts;

namespace Ashok.Core.Playbooks;

public interface IPlaybookStore
{
    IReadOnlyList<Playbook> All { get; }

    Playbook? Find(string id);
}

/// <summary>All playbooks from a directory of *.yaml files, filtered to one OS.</summary>
public sealed class PlaybookStore : IPlaybookStore
{
    private readonly Dictionary<string, Playbook> _byId;

    public PlaybookStore(IEnumerable<Playbook> playbooks)
    {
        _byId = new Dictionary<string, Playbook>(StringComparer.Ordinal);
        foreach (var playbook in playbooks)
        {
            if (!_byId.TryAdd(playbook.Id, playbook))
                throw new PlaybookException(playbook.Id, [$"playbook id '{playbook.Id}' is defined more than once"]);
        }
    }

    public IReadOnlyList<Playbook> All => _byId.Values.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();

    public Playbook? Find(string id) => _byId.GetValueOrDefault(id);

    /// <param name="os">"win10" or "win11"; playbooks that do not list it are skipped.</param>
    public static PlaybookStore LoadDirectory(string directory, string os)
    {
        var files = Directory.GetFiles(directory, "*.yaml").OrderBy(f => f, StringComparer.Ordinal);
        var playbooks = files.Select(PlaybookLoader.Load).Where(p => p.Os.Contains(os)).ToList();
        return new PlaybookStore(playbooks);
    }
}
