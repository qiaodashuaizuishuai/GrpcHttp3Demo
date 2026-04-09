using System;
using GrpcHttp3Demo.Protos;

namespace GrpcHttp3Demo.Core.Managers
{
    public partial class ConnectionManager
    {
        public object GetOnlineRoleSnapshot(TimeSpan onlineTimeout)
        {
            var now = DateTime.UtcNow;

            var totalRegistered = 0;
            var totalOnline = 0;
            var robotOnline = 0;
            var vrOnline = 0;
            var unknownOnline = 0;
            var pushConnectedOnline = 0;

            foreach (var kv in _sessions)
            {
                totalRegistered++;
                var ctx = kv.Value;

                if (now - ctx.LastHeartbeatUtc > onlineTimeout)
                {
                    continue;
                }

                totalOnline++;

                if (ctx.EventSender != null)
                {
                    pushConnectedOnline++;
                }

                switch (ctx.Role)
                {
                    case RegisterRequest.Types.EndpointType.Robot:
                        robotOnline++;
                        break;
                    case RegisterRequest.Types.EndpointType.Vr:
                        vrOnline++;
                        break;
                    default:
                        unknownOnline++;
                        break;
                }
            }

            return new
            {
                timeoutSeconds = (int)Math.Max(1, onlineTimeout.TotalSeconds),
                registered = totalRegistered,
                online = totalOnline,
                onlineByRole = new
                {
                    robot = robotOnline,
                    vr = vrOnline,
                    unknown = unknownOnline
                },
                pushConnectedOnline
            };
        }
    }
}
