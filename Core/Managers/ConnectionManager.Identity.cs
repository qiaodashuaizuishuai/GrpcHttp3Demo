using System;
using GrpcHttp3Demo.Protos;

namespace GrpcHttp3Demo.Core.Managers
{
    public partial class ConnectionManager
    {
        internal readonly record struct IdentityKey(string DeviceId, RegisterRequest.Types.EndpointType Role)
        {
            public static IdentityKey From(string deviceId, RegisterRequest.Types.EndpointType role)
            {
                deviceId ??= string.Empty;
                // 约定：DeviceId 作为逻辑身份，大小写不敏感
                var normalized = deviceId.Trim().ToLowerInvariant();
                return new IdentityKey(normalized, role);
            }

            public bool IsValid => !string.IsNullOrWhiteSpace(DeviceId) && Role != RegisterRequest.Types.EndpointType.Unknown;
        }
    }
}
