namespace ClipFx.Engine;

public interface IEnforcementState
{
    bool IsApplied(string key);
    bool GlobalApplied { get; }
}
