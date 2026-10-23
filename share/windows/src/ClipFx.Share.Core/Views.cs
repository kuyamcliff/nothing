namespace ClipFx.Share;

public sealed record PeerView(string Id, string Name, string Platform, string Address, int Port, bool Online, bool Trusted, bool Manual, DateTime LastSeen);

public sealed record TransferView(
    string Id, Direction Direction, PeerView Peer, TransferState State, string? Error,
    string Title, int Files, int Folders, long TotalBytes, long DoneBytes, double Rate, double? Eta,
    string? CurrentFile, DateTime Started, DateTime? Finished, string? OpenPath, string? SaveDir,
    string? Text, bool SpaceOk, IReadOnlyList<string> Preview,
    bool CanPause, bool CanResume, bool CanCancel, bool CanRetry);

public sealed record SelfView(string Id, string Name, string Platform, int Port, IReadOnlyList<string> Addresses);
