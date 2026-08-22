namespace Hydra.Scenes;

public sealed class SceneOverrideStore(string configPath)
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(configPath)!, ".screenfuse-scene");

    public string? Read()
    {
        try
        {
            if (!File.Exists(Path)) return null;
            var value = File.ReadAllText(Path).Trim();
            return value.Length == 0 ? null : value;
        }
        catch (IOException) { return null; }
    }

    public void Write(string scene)
    {
        var temp = Path + ".tmp";
        File.WriteAllText(temp, scene + Environment.NewLine);
        File.Move(temp, Path, true);
    }
}
