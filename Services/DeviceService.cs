using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Synclo.Models;

namespace Synclo.Services;

public sealed class DeviceService(APIService api)
{
    public async Task<List<DeviceModel>> GetDevicesAsync(CancellationToken ct = default)
    {
        using var res = await api.GetAsync("/api/devices", ct);
        var content = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            // Throw so the ViewModel knows it's a server error, not just an empty list.
            throw new ServerFailureException(content);

        var list = api.Deserialize<List<DeviceModel>>(content);
        return list ?? []; // Return an empty list instead of null if deserialization fails
    }

    public async Task DeleteDeviceAsync(string deviceId, CancellationToken ct = default)
    {
        using var res = await api.DeleteAsync("/api/devices/{deviceId}", ct);
        if (!res.IsSuccessStatusCode)
        {
            var error = await res.Content.ReadAsStringAsync(ct);
            throw new ServerFailureException(error);
        }
        // No need to return bool. If it didn't throw, it succeeded.
    }
}