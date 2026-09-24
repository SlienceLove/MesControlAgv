namespace MesControlAgv.Contracts;

public sealed record ShineLabServerStatusResponse(
    bool Enabled,
    string ListenAddress,
    int Port,
    int StaleAfterSeconds,
    int CommandTimeoutMs,
    string PayloadProfile,
    bool EnableTrayCommands = false);
