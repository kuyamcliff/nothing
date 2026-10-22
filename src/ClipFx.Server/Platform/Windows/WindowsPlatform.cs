namespace ClipFx.Platform.Windows;

public static class WindowsPlatform
{
    public static PlatformServices Create()
    {
        var source = new EtwTrafficSource();
        var resolver = new WindowsProcessResolver();
        source.ProcessSeen += pid => resolver.Resolve(pid);
        return Build(source, resolver);
    }

    static PlatformServices Build(EtwTrafficSource source, WindowsProcessResolver resolver) => new(
        source,
        resolver,
        new WindowsFirewall(),
        new TcpConnectionKiller(),
        new WindowsIconProvider(),
        Simulated: false);
}
