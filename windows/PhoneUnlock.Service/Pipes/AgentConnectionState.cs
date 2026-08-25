namespace PhoneUnlock.Service.Pipes;

public sealed class AgentConnectionState
{
    private int connected;

    public bool IsConnected => Volatile.Read(ref connected) == 1;

    public void SetConnected(bool value) => Volatile.Write(ref connected, value ? 1 : 0);
}
