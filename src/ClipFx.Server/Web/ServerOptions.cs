namespace ClipFx.Web;

public sealed class ServerOptions
{
    public const int DefaultPort = 47613;
    public int Port { get; set; } = DefaultPort;
    public string DataDir { get; set; } = DefaultDataDir();
    /// <summary>Serve the UI from disk instead of embedded files (development).</summary>
    public string? UiDir { get; set; }

    public static string DefaultDataDir() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClipFx Manager")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "clipfx-manager");
}
