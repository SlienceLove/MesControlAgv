using System.Security.Cryptography;
using System.Text;

namespace MesControlAgv.Domain;

public static class TransportOperationIds
{
    public static Guid Pickup(Guid taskId) => Derive(taskId, "pickup");
    public static Guid Dropoff(Guid taskId) => Derive(taskId, "dropoff");

    private static Guid Derive(Guid taskId, string leg)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{taskId:N}:{leg}"));
        return new Guid(bytes[..16]);
    }
}
