using System.IO;
using Ashok.Core.Planner;

namespace Ashok.App;

/// <summary>Keeps the install token in %LOCALAPPDATA%\Ashok\install-token.txt. It identifies this install; it is not the API key.</summary>
public sealed class FileInstallTokenStore(string? path = null) : IInstallTokenStore
{
    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ashok", "install-token.txt");

    public string? Load()
    {
        try
        {
            var token = File.Exists(_path) ? File.ReadAllText(_path).Trim() : "";
            return token.Length > 0 ? token : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Save(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, token);
    }
}
