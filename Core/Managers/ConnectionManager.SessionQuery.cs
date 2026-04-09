using System;
using System.Collections.Generic;
using System.Linq;
using GrpcHttp3Demo.Core.Services;
using GrpcHttp3Demo.Protos;

namespace GrpcHttp3Demo.Core.Managers
{
    public partial class ConnectionManager
    {
        public IEnumerable<object> ListSessions(TimeSpan onlineTimeout, RegisterRequest.Types.EndpointType? roleFilter, bool onlineOnly)
        {
            var now = DateTime.UtcNow;

            foreach (var kv in _sessions)
            {
                var ctx = kv.Value;
                var online = now - ctx.LastHeartbeatUtc <= onlineTimeout;

                if (onlineOnly && !online)
                {
                    continue;
                }

                if (roleFilter.HasValue && roleFilter.Value != RegisterRequest.Types.EndpointType.Unknown && ctx.Role != roleFilter.Value)
                {
                    continue;
                }

                yield return new
                {
                    sessionId = ctx.SessionId,
                    deviceId = ctx.DeviceId,
                    role = ctx.Role.ToString(),
                    online,
                    pushConnected = ctx.EventSender != null,
                    hasUdpEndpoint = ctx.UdpEndpoint != null,
                    pairedSessionId = GetPairedSession(ctx.SessionId)
                };
            }
        }

        public object? GetSessionDetail(string sessionId, TimeSpan onlineTimeout, UdpForwardingMetricsService? forwardingMetrics = null)
        {
            if (!_sessions.TryGetValue(sessionId, out var ctx))
            {
                return null;
            }

            var now = DateTime.UtcNow;
            var online = now - ctx.LastHeartbeatUtc <= onlineTimeout;
            var pairedSessionId = GetPairedSession(sessionId);

            // 订阅关系（Publisher 侧 meta）：
            // - 作为发布者：我有哪些订阅者（rich meta）
            // - 作为订阅者：我订阅了哪些发布者（从全局表反查）
            var subscribersMeta = Array.Empty<object>();
            if (_subscriptionMeta.TryGetValue(sessionId, out var subsMetaDict))
            {
                subscribersMeta = subsMetaDict.Values.Select(m => new
                {
                    subscriberSessionId = m.SubscriberId,
                    subscriberDeviceId = _sessions.TryGetValue(m.SubscriberId, out var subCtx) ? subCtx.DeviceId : null,
                    subVideo = m.SubVideo,
                    subPose = m.SubPose,
                    subAudio = m.SubAudio,
                    lastUpdatedUtc = m.LastUpdatedUtc,
                    targetBitrateKbps = m.TargetBitrateKbps,
                    subscriberBandwidthKbps = m.SubscriberBandwidthKbps,
                    spsBytesLength = m.Sps?.Length ?? 0,
                    ppsBytesLength = m.Pps?.Length ?? 0
                }).ToArray<object>();
            }

            var subscribedTo = _subscriptionMeta
                .Select(kv => new { PublisherSessionId = kv.Key, Subs = kv.Value })
                .Where(x => x.Subs.TryGetValue(sessionId, out _))
                .Select(x =>
                {
                    x.Subs.TryGetValue(sessionId, out var meta);
                    return new
                    {
                        publisherSessionId = x.PublisherSessionId,
                        publisherDeviceId = _sessions.TryGetValue(x.PublisherSessionId, out var pubCtx) ? pubCtx.DeviceId : null,
                        subVideo = meta?.SubVideo ?? false,
                        subPose = meta?.SubPose ?? false,
                        subAudio = meta?.SubAudio ?? false,
                        lastUpdatedUtc = meta?.LastUpdatedUtc
                    };
                })
                .ToArray<object>();

            // 转发统计（高性能路径：counter 在转发表重建阶段预绑定，热路径只做 Interlocked）
            // 详情页展示：只列出“配对者 + 订阅者”这两类目标。
            var forwardToTargetSessionIds = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(pairedSessionId)) forwardToTargetSessionIds.Add(pairedSessionId);
            foreach (var s in ctx.Subscribers.Values) forwardToTargetSessionIds.Add(s.SubscriberId);

            var outboundTo = forwardToTargetSessionIds
                .Select(tid =>
                {
                    _sessions.TryGetValue(tid, out var tCtx);

                    object? stats = null;
                    if (forwardingMetrics != null && forwardingMetrics.TryGetEdge(sessionId, tid, out var edge) && edge != null)
                    {
                        stats = edge.Snapshot();
                    }

                    return new
                    {
                        targetSessionId = tid,
                        targetDeviceId = tCtx?.DeviceId,
                        targetRole = tCtx?.Role.ToString(),
                        targetHasUdpEndpoint = tCtx?.UdpEndpoint != null,
                        stats
                    };
                })
                .ToArray<object>();

            return new
            {
                sessionId = ctx.SessionId,
                deviceId = ctx.DeviceId,
                role = ctx.Role.ToString(),
                online,
                pushConnected = ctx.EventSender != null,

                // client (gRPC/WS) connection info
                client = new
                {
                    ip = ctx.ClientIp,
                    port = ctx.ClientPort
                },

                // udp mapping info
                udp = new
                {
                    endpoint = ctx.UdpEndpoint?.ToString(),
                    lastControlUtc = ctx.LastUdpControlUtc == DateTime.MinValue ? (DateTime?)null : ctx.LastUdpControlUtc,
                    lastDataUtc = ctx.LastUdpDataUtc == DateTime.MinValue ? (DateTime?)null : ctx.LastUdpDataUtc
                },

                heartbeat = new
                {
                    lastHeartbeatUtc = ctx.LastHeartbeatUtc
                },

                pairing = new
                {
                    pairedSessionId,
                    pairedDeviceId = ctx.PairedDeviceId
                },

                subscriptions = new
                {
                    // 谁订阅了我（仅计数；详情可后续扩展）
                    subscriberCount = ctx.Subscribers.Count,
                    subscribers = ctx.Subscribers.Values.Select(s => new
                    {
                        subscriberId = s.SubscriberId,
                        subVideo = s.SubVideo,
                        subPose = s.SubPose,
                        subAudio = s.SubAudio
                    }).ToArray()
                },

                subscriptionMeta = new
                {
                    // Publisher 侧 meta（rich）：用于展示编码参数/带宽/码率等扩展字段
                    subscribers = subscribersMeta,

                    // 我订阅了谁（从 Publisher 侧 meta 反查）
                    subscribedTo
                },

                forwarding = new
                {
                    updatedUtc = forwardingMetrics?.LastTickUtc,
                    outboundTo
                }
            };
        }
    }
}
