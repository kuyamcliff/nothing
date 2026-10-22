namespace ClipFx.Platform.Windows;

public static class WindowsPlatform
{
    public static PlatformServices Create() => new(
        new EtwTrafficSource(),
        new WindowsProcessResolver(),
        new WindowsFirewall(),
        new TcpConnectionKiller(),
        new WindowsIconProvider(),
        Simulated: false);
}
