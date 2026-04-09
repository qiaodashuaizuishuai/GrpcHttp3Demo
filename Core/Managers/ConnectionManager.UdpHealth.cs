using System;
using System.Net;
using GrpcHttp3Demo.Protos;

namespace GrpcHttp3Demo.Core.Managers
{
    public partial class ConnectionManager
    {
        public void UpdateUdpDataActivity(string sessionId)
        {
            if (_sessions.TryGetValue(sessionId, out var ctx))
            {
                ctx.LastUdpDataUtc = DateTime.UtcNow;
            }
        }

        /// <summary>
        /// UDP 端点映射过期检测与救援：
        /// - 仅 HELLO/PING(验签通过) 会刷新 LastUdpControlUtc 与映射。
        /// - 数据包只刷新 LastUdpDataUtc，不刷新映射。
        /// - 当映射过期：清理 UDP 索引/转发表，并通过 push 通道提示客户端重发 UDP HELLO。
        /// </summary>
        public void CheckAndRescueUdpMappings(TimeSpan controlTimeout, TimeSpan rescueCooldown, int maxRescues)
        {
            var now = DateTime.UtcNow;

            foreach (var kv in _sessions)
            {
                var sessionId = kv.Key;
                var ctx = kv.Value;

                if (ctx.UdpEndpoint == null)
                {
                    continue;
                }

                var lastControl = ctx.LastUdpControlUtc;
                if (lastControl == DateTime.MinValue)
                {
                    // 从未收到有效 HELLO/PING，则不做过期清理（避免误杀）。
                    continue;
                }

                if (now - lastControl <= controlTimeout)
                {
                    continue;
                }

                // 1) 清理 UDP 映射与转发表
                var oldEp = ctx.UdpEndpoint;
                ctx.UdpEndpoint = null;

                if (oldEp != null)
                {
                    _endpointIndex.TryRemove(oldEp, out _);
                    _forwardingTable.TryRemove(oldEp, out _);
                    _poseForwardingTable.TryRemove(oldEp, out _);
                    _feedbackRoute.TryRemove(oldEp, out _);
                }

                _sessionEndpointIndex.TryRemove(sessionId, out _);

                // 2) 通知客户端重发 UDP HELLO（如果 push 可用且未超过救援上限）
                if (!IsPushConnected(sessionId))
                {
                    continue;
                }

                if (ctx.UdpRescueCount >= maxRescues)
                {
                    continue;
                }

                if (ctx.LastUdpRescueUtc != DateTime.MinValue && now - ctx.LastUdpRescueUtc < rescueCooldown)
                {
                    continue;
                }

                ctx.UdpRescueCount++;
                ctx.LastUdpRescueUtc = now;

                var cmd = new EventMessage
                {
                    TargetSessionId = sessionId,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    System = new SystemCommand { Action = SystemCommand.Types.Action.RequestUdpHello }
                };

                _ = SendEventAsync(sessionId, cmd);
            }
        }
    }
}
