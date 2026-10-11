namespace Ashok.Core.Planner;

/// <summary>Keeps the install token between runs. The token identifies this install to the Worker; it is not the API key.</summary>
public interface IInstallTokenStore
{
    string? Load();

    void Save(string token);
}
