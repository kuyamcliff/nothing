using System.Diagnostics;
using System.ServiceProcess;
using ClipFx.Platform.Windows;

namespace ClipFx.App;

/// <summary>Installs and controls the background service. Needs administrator rights.</summary>
static class ServiceSetup
{
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    static ServiceController? Find() =>
        ServiceController.GetServices().FirstOrDefault(s => s.ServiceName.Equals(Program.ServiceName, StringComparison.OrdinalIgnoreCase));

    public static int Install()
    {
        try
        {
            string exe = Environment.ProcessPath!;
            string bin = $"binPath= \"\\\"{exe}\\\" --service\"";
            using (var existing = Find())
            {
                if (existing != null)
                {
                    StopService(existing);
                    Sc($"config {Program.ServiceName} {bin} start= auto DisplayName= \"ClipFx Manager\"");
                }
                else
                {
                    Sc($"create {Program.ServiceName} {bin} start= auto DisplayName= \"ClipFx Manager\"");
                }
            }
            Sc($"description {Program.ServiceName} \"Monitors and limits internet data per app.\"");
            Sc($"failure {Program.ServiceName} reset= 86400 actions= restart/2000/restart/5000/restart/10000");
            Sc($"failureflag {Program.ServiceName} 1");
            return EnsureRunning();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    public static int EnsureRunning()
    {
        using var svc = Find();
        if (svc == null) return Install();
        svc.Refresh();
        if (svc.Status != ServiceControllerStatus.Running)
        {
            if (svc.Status != ServiceControllerStatus.StartPending) svc.Start();
            svc.WaitForStatus(ServiceControllerStatus.Running, Wait);
        }
        return 0;
    }

    public static int Stop()
    {
        using var svc = Find();
        if (svc != null) StopService(svc);
        return 0;
    }

    public static int Uninstall()
    {
        try
        {
            using (var svc = Find())
            {
                if (svc != null)
                {
                    StopService(svc);
                    Sc($"delete {Program.ServiceName}");
                }
            }
            new WindowsFirewall().RemoveAll();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    static void StopService(ServiceController svc)
    {
        svc.Refresh();
        if (svc.Status is ServiceControllerStatus.Stopped) return;
        if (svc.Status != ServiceControllerStatus.StopPending) svc.Stop();
        svc.WaitForStatus(ServiceControllerStatus.Stopped, Wait);
    }

    static void Sc(string args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"sc {args.Split(' ')[0]} failed ({p.ExitCode}): {output.Trim()}");
    }
}
