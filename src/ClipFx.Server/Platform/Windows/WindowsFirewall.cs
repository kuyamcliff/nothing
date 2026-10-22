using System.Reflection;
using ClipFx.Model;

namespace ClipFx.Platform.Windows;

/// <summary>
/// Blocks apps with Windows Defender Firewall rules (inbound + outbound).
/// Block rules override allow rules and are kept by Windows across restarts.
/// </summary>
public sealed class WindowsFirewall : IFirewall
{
    public const string Group = "ClipFx Manager";
    const string Prefix = "clipfx|";
    const int ActionBlock = 0;
    const int DirIn = 1, DirOut = 2;
    const int AllProfiles = 0x7FFFFFFF;
    static readonly int[] Profiles = [1, 2, 4]; // domain, private, public

    readonly object _gate = new();

    sealed record Spec(string? Program, string? Service, string? PackageSid)
    {
        public override string ToString() => $"{Program}~{Service}~{PackageSid}";
    }

    static object NewPolicy() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: true)!)!;
    static dynamic NewRule() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: true)!)!;

    static IReadOnlyList<Spec> SpecsFor(AppIdentity id)
    {
        var svchost = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "svchost.exe");
        return id.Kind switch
        {
            AppKind.System => [new Spec("System", null, null)],
            AppKind.Service => id.Services.Select(s => new Spec(svchost, s, null)).ToList(),
            AppKind.Packaged => new[]
            {
                id.PackageSid != null ? new Spec(null, null, id.PackageSid) : null,
                id.Path != null ? new Spec(id.Path, null, null) : null,
            }.OfType<Spec>().ToList(),
            AppKind.Program when id.Path != null => [new Spec(id.Path, null, null)],
            _ => [],
        };
    }

    public string Signature(AppIdentity identity) => KeyId.Of(string.Join("|", SpecsFor(identity)));

    record OurRule(string Name, string Key, string Signature);

    static List<OurRule> ReadOurRules(object policyObj)
    {
        dynamic policy = policyObj;
        var list = new List<OurRule>();
        foreach (dynamic rule in policy.Rules)
        {
            string? grouping = rule.Grouping;
            if (grouping != Group) continue;
            string? desc = rule.Description;
            string name = rule.Name;
            if (desc == null || !desc.StartsWith(Prefix, StringComparison.Ordinal)) { list.Add(new OurRule(name, "", "")); continue; }
            var parts = desc.Split('|', 3);
            list.Add(new OurRule(name, parts.Length > 1 ? parts[1] : "", parts.Length > 2 ? parts[2] : ""));
        }
        return list;
    }

    public IReadOnlyDictionary<string, string> GetApplied()
    {
        lock (_gate)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var r in ReadOurRules(NewPolicy()))
            {
                if (r.Key.Length == 0) continue;
                // Mixed signatures mean a half-written set; report a mismatch so it gets rewritten.
                result[r.Key] = result.TryGetValue(r.Key, out var s) && s != r.Signature ? "" : r.Signature;
            }
            return result;
        }
    }

    public void Block(AppIdentity identity)
    {
        var specs = SpecsFor(identity);
        if (specs.Count == 0) throw new InvalidOperationException($"{identity.Name} can't be blocked.");
        string sig = Signature(identity);
        lock (_gate)
        {
            object policyObj = NewPolicy();
            dynamic policy = policyObj;
            RemoveWhere(policyObj, r => r.Key == identity.Key);
            int n = 0;
            foreach (var spec in specs)
            {
                foreach (int dir in new[] { DirOut, DirIn })
                {
                    dynamic rule = NewRule();
                    rule.Name = $"ClipFx Manager - {Trim(identity.Name)} [{identity.Id}-{n}{(dir == DirOut ? "o" : "i")}]";
                    rule.Description = $"{Prefix}{identity.Key}|{sig}";
                    rule.Grouping = Group;
                    rule.Action = ActionBlock;
                    rule.Direction = dir;
                    rule.Profiles = AllProfiles;
                    rule.InterfaceTypes = "All";
                    if (spec.Program != null) rule.ApplicationName = spec.Program;
                    if (spec.Service != null) rule.serviceName = spec.Service;
                    if (spec.PackageSid != null) rule.LocalAppPackageId = spec.PackageSid;
                    rule.Enabled = true;
                    policy.Rules.Add(rule);
                }
                n++;
            }
        }
    }

    public void Unblock(string key)
    {
        lock (_gate) RemoveWhere(NewPolicy(), r => r.Key == key);
    }

    public void SetGlobalBlock(bool on)
    {
        lock (_gate)
        {
            object policyObj = NewPolicy();
            dynamic policy = policyObj;
            RemoveWhere(policyObj, r => r.Key == IFirewall.GlobalKey);
            if (!on) return;
            foreach (int dir in new[] { DirOut, DirIn })
            {
                dynamic rule = NewRule();
                rule.Name = $"ClipFx Manager - Data limit reached [{(dir == DirOut ? "o" : "i")}]";
                rule.Description = $"{Prefix}{IFirewall.GlobalKey}|g";
                rule.Grouping = Group;
                rule.Action = ActionBlock;
                rule.Direction = dir;
                rule.Profiles = AllProfiles;
                rule.InterfaceTypes = "All";
                rule.Enabled = true;
                policy.Rules.Add(rule);
            }
        }
    }

    public bool IsEnabled()
    {
        object policy = NewPolicy();
        int current = (int)Invoke(policy, "CurrentProfileTypes", BindingFlags.GetProperty)!;
        foreach (int p in Profiles)
            if ((current & p) != 0 && !(bool)Invoke(policy, "FirewallEnabled", BindingFlags.GetProperty, p)!)
                return false;
        return true;
    }

    public void Enable()
    {
        object policy = NewPolicy();
        foreach (int p in Profiles) Invoke(policy, "FirewallEnabled", BindingFlags.SetProperty, p, true);
    }

    public void RemoveAll()
    {
        lock (_gate) RemoveWhere(NewPolicy(), _ => true);
    }

    static void RemoveWhere(object policyObj, Func<OurRule, bool> match)
    {
        dynamic policy = policyObj;
        List<OurRule> rules = ReadOurRules(policyObj);
        foreach (var r in rules.Where(match)) policy.Rules.Remove(r.Name);
    }

    static object? Invoke(object target, string name, BindingFlags kind, params object[] args) =>
        target.GetType().InvokeMember(name, kind, null, target, args);

    static string Trim(string s) => s.Length <= 60 ? s : s[..60];
}
