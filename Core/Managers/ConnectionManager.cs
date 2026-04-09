using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Grpc.Core;
using GrpcHttp3Demo.Protos;
using GrpcHttp3Demo.Models;
using GrpcHttp3Demo.Core.Interfaces;

using GrpcHttp3Demo.Utils;

namespace GrpcHttp3Demo.Core.Managers
{
    /// <summary>
    /// 连接管理器 (核心部分)
    /// 负责会话的生命周期管理、注册与注销
    /// </summary>
    public partial class ConnectionManager
    {
        private readonly GrpcHttp3Demo.Core.Services.UdpForwardingMetricsService _forwardingMetrics;

        public ConnectionManager(GrpcHttp3Demo.Core.Services.UdpForwardingMetricsService forwardingMetrics)
        {
            _forwardingMetrics = forwardingMetrics;
        }

        /// <summary>
        /// 获取会话上下文
        /// </summary>
        /// <param name="sessionId">会话ID</param>
        /// <returns>设备上下文，如果不存在则返回null</returns>
        public DeviceContext? GetSession(string sessionId)
        {
            return _sessions.TryGetValue(sessionId, out var ctx) ? ctx : null;
        }

        /// <summary>
        /// 注册 gRPC 客户端
        /// </summary>
        /// <param name="sessionId">会话ID</param>
        /// <param name="deviceId">设备ID</param>
        /// <param name="role">角色类型</param>
        /// <param name="clientIp">客户端IP</param>
        /// <param name="clientPort">客户端端口</param>
        public void RegisterGrpc(string sessionId, string deviceId, RegisterRequest.Types.EndpointType role, 
                     string clientIp, int clientPort)
        {
            // 去重策略：同一 (DeviceId, Role) 短时间重复注册 -> 踢旧保新。
            // 约束：DeviceId 为空或 Role 未知时不做去重（避免误杀）。
            var identityKey = IdentityKey.From(deviceId, role);
            if (identityKey.IsValid)
            {
                var gate = _identityGates.GetOrAdd(identityKey, _ => new object());
                lock (gate)
                {
                    // 先踢掉所有同身份的旧 session（历史 bug 可能导致不止一个）
                    var toKick = _sessions
                        .Where(kv =>
                            kv.Key != sessionId &&
                            kv.Value.Role == role &&
                            string.Equals(kv.Value.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                        .Select(kv => kv.Key)
                        .ToArray();

                    foreach (var oldSessionId in toKick)
                    {
                        Console.WriteLine($"[ConnMgr] Duplicate identity detected. Kicking old session: {oldSessionId} (device={deviceId} role={role})");
                        UnregisterGrpc(oldSessionId);
                    }

                    // 占有身份索引（踢旧保新）
                    _identityIndex[identityKey] = sessionId;

                    // 完成注册信息写入
                    var newCtx = _sessions.GetOrAdd(sessionId, _ => new DeviceContext
                    {
                        DeviceId = deviceId,
                        SessionId = sessionId
                    });

                    newCtx.SessionId = sessionId;
                    newCtx.DeviceId = deviceId;
                    newCtx.Role = role;
                    newCtx.ClientIp = clientIp;
                    newCtx.ClientPort = clientPort;

                    Console.WriteLine($"[ConnMgr] gRPC Registered: session={sessionId} device={deviceId} role={role}");
                    return;
                }
            }

            var ctx = _sessions.GetOrAdd(sessionId, _ => new DeviceContext
            {
                DeviceId = deviceId,
                SessionId = sessionId
            });

            ctx.SessionId = sessionId;
            ctx.DeviceId = deviceId;
            ctx.Role = role;
            ctx.ClientIp = clientIp;
            ctx.ClientPort = clientPort;

            Console.WriteLine($"[ConnMgr] gRPC Registered: session={sessionId} device={deviceId} role={role}");
        }

        /// <summary>
        /// 注销 gRPC 客户端并清理所有相关资源
        /// </summary>
        /// <param name="sessionId">会话ID</param>
        public void UnregisterGrpc(string sessionId)
        {
            if (_sessions.TryRemove(sessionId, out var ctx))
            {
                // 清理身份索引（如果当前 owner 正是该 session）
                var identityKey = IdentityKey.From(ctx.DeviceId, ctx.Role);
                if (identityKey.IsValid && _identityIndex.TryGetValue(identityKey, out var owner) && owner == sessionId)
                {
                    _identityIndex.TryRemove(identityKey, out _);
                }

                // 1. 清理配对关系
                UnpairSession(sessionId);

                // 2. 清理UDP索引
                if (ctx.UdpEndpoint != null)
                {
                    _endpointIndex.TryRemove(ctx.UdpEndpoint, out _);
                    _forwardingTable.TryRemove(ctx.UdpEndpoint, out _);
                }
                _sessionEndpointIndex.TryRemove(sessionId, out _);

                // 3. 清理订阅关系 (我是发布者)
                // ctx.Subscribers 自动丢弃

                // 4. 清理订阅关系 (我是订阅者)
                foreach (var other in _sessions.Values)
                {
                    // Subscribers keyed by subscriber session
                    other.Subscribers.TryRemove(sessionId, out _);
                }

                // 5. 清理元信息订阅表及受影响的转发表
                _subscriptionMeta.TryRemove(sessionId, out _);
                RebuildForwardingForSubscriber(sessionId);

                Console.WriteLine($"[ConnMgr] Unregistered session: {sessionId}");
            }
        }
    }
}
