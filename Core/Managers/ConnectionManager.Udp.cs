using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Net;
using GrpcHttp3Demo.Core.Services;
using GrpcHttp3Demo.Protos;
using GrpcHttp3Demo.Utils;

namespace GrpcHttp3Demo.Core.Managers
{
    public partial class ConnectionManager
    {
        /// <summary>
        /// 根据UDP端点获取会话ID
        /// </summary>
        public string? GetSessionByEndpoint(IPEndPoint endpoint)
        {
            return _endpointIndex.TryGetValue(endpoint, out var sessionId) ? sessionId : null;
        }

        /// <summary>
        /// 尝试根据会话ID获取UDP端点
        /// </summary>
        public bool TryGetEndpointBySession(string sessionId, out IPEndPoint? endpoint)
        {
            endpoint = null;
            var ok = _sessionEndpointIndex.TryGetValue(sessionId, out var ep);
            endpoint = ep;
            return ok;
        }

        /// <summary>
        /// 尝试获取转发目标列表
        /// </summary>
        public bool TryGetForwardTargets(IPEndPoint sourceEp, out ImmutableArray<UdpForwardTarget> targets)
        {
            return _forwardingTable.TryGetValue(sourceEp, out targets);
        }

        /// <summary>
        /// 尝试获取姿态(Pose)转发目标列表
        /// </summary>
        public bool TryGetPoseForwardTargets(IPEndPoint sourceEp, out ImmutableArray<UdpForwardTarget> targets)
        {
            return _poseForwardingTable.TryGetValue(sourceEp, out targets);
        }

        /// <summary>
        /// 尝试获取音频(Audio)转发目标列表
        /// </summary>
        public bool TryGetAudioForwardTargets(IPEndPoint sourceEp, out ImmutableArray<UdpForwardTarget> targets)
        {
            return _audioForwardingTable.TryGetValue(sourceEp, out targets);
        }

        /// <summary>
        /// 尝试获取反馈转发目标（VR -> Robot）
        /// </summary>
        public bool TryGetFeedbackForward(IPEndPoint vrEndpoint, out IPEndPoint? robotEndpoint, out ForwardEdgeCounter? counter)
        {
            robotEndpoint = null;
            counter = null;

            if (_feedbackRoute.TryGetValue(vrEndpoint, out var target))
            {
                robotEndpoint = target.RobotEndpoint;
                counter = target.Counter;
                return true;
            }

            return false;
        }

        /// <summary>
        /// 注册UDP端点到会话
        /// </summary>
        public void RegisterUdpBySession(string sessionId, IPEndPoint endpoint)
        {
            if (_sessions.TryGetValue(sessionId, out var ctx))
            {
                // 控制面（HELLO/PING 且验签通过）到达：刷新控制面活跃时间
                ctx.LastUdpControlUtc = DateTime.UtcNow;
                ctx.UdpRescueCount = 0;

                // 如果端点未变，跳过以避免不必要的表更新
                if (ctx.UdpEndpoint != null && ctx.UdpEndpoint.Equals(endpoint))
                {
                    return;
                }

                // 如果之前有旧的Endpoint，先移除索引
                if (ctx.UdpEndpoint != null)
                {
                    _endpointIndex.TryRemove(ctx.UdpEndpoint, out _);
                    _forwardingTable.TryRemove(ctx.UdpEndpoint, out _);
                    _poseForwardingTable.TryRemove(ctx.UdpEndpoint, out _);
                    _audioForwardingTable.TryRemove(ctx.UdpEndpoint, out _);
                    _sessionEndpointIndex.TryRemove(sessionId, out _);
                }

                ctx.UdpEndpoint = endpoint;
                _endpointIndex[endpoint] = sessionId; // 更新索引
                _sessionEndpointIndex[sessionId] = endpoint;

                // 刷新反馈路由
                RefreshFeedbackRoute(sessionId);
                if (_pairings.TryGetValue(sessionId, out var partner)) RefreshFeedbackRoute(partner);

                // 触发转发表重建
                RebuildForwardingForPublisher(sessionId);
                RebuildForwardingForSubscriber(sessionId);

                Console.WriteLine($"[ConnMgr] UDP Registered: session {sessionId} -> {endpoint}");
            }
            else
            {
                Console.WriteLine($"[ConnMgr] Warning: UDP Register for unknown session {sessionId}");
            }
        }

        /// <summary>
        /// 获取会话的UDP端点
        /// </summary>
        public IPEndPoint? GetUdpEndpointBySession(string sessionId)
        {
            return _sessions.TryGetValue(sessionId, out var ctx) ? ctx.UdpEndpoint : null;
        }

        // --- 内部：转发表重建逻辑（低频写，热路径无锁读） ---
        
        /// <summary>
        /// 重建发布者的转发表
        /// </summary>
        private void RebuildForwardingForPublisher(string publisherSessionId)
        {
            if (!_sessions.TryGetValue(publisherSessionId, out var pubCtx) || pubCtx.UdpEndpoint == null)
            {
                if (_sessionEndpointIndex.TryGetValue(publisherSessionId, out var oldEp))
                {
                    _forwardingTable.TryRemove(oldEp, out _);
                    _poseForwardingTable.TryRemove(oldEp, out _);
                    _audioForwardingTable.TryRemove(oldEp, out _);
                }
                return;
            }

            var sourceEp = pubCtx.UdpEndpoint;

            // 以 targetSessionId 为 key 汇总（union），再生成 video/pose/audio 三张表。
            var targets = new Dictionary<string, (IPEndPoint ep, ForwardEdgeCounter counter, bool video, bool pose, bool audio)>();

            void AddTarget(string targetSessionId, bool wantVideo, bool wantPose, bool wantAudio)
            {
                if (string.IsNullOrEmpty(targetSessionId)) return;
                if (!_sessions.TryGetValue(targetSessionId, out var tCtx) || tCtx.UdpEndpoint == null) return;

                var ep = tCtx.UdpEndpoint;
                if (ep.Equals(sourceEp)) return;

                if (targets.TryGetValue(targetSessionId, out var existing))
                {
                    targets[targetSessionId] = (existing.ep, existing.counter, existing.video || wantVideo, existing.pose || wantPose, existing.audio || wantAudio);
                    return;
                }

                var counter = _forwardingMetrics.GetOrCreateEdge(publisherSessionId, targetSessionId);
                targets[targetSessionId] = (ep, counter, wantVideo, wantPose, wantAudio);
            }

            // 1) 配对默认转发：如果我已配对，对端自动成为默认接收者
            var pairedSessionId = GetPairedSession(publisherSessionId);
            if (pairedSessionId != null)
            {
                AddTarget(pairedSessionId, wantVideo: true, wantPose: true, wantAudio: true);
            }

            // 2) 订阅转发：按订阅表把订阅者加入目标集合（按资源过滤）
            if (_subscriptionMeta.TryGetValue(publisherSessionId, out var subs))
            {
                foreach (var kv in subs)
                {
                    var subscriberSessionId = kv.Key;
                    var meta = kv.Value;

                    AddTarget(subscriberSessionId, wantVideo: meta.SubVideo, wantPose: meta.SubPose, wantAudio: meta.SubAudio);
                }
            }

            // 3) 调试广播：只在开发模式下开启，目标集合 = 全体已注册 UDP 会话（排除自己）
            if (AppConfig.IsBroadcastToAll)
            {
                foreach (var session in _sessions.Values)
                {
                    if (session.UdpEndpoint == null) continue;
                    if (session.SessionId == publisherSessionId) continue;
                    AddTarget(session.SessionId, wantVideo: true, wantPose: true, wantAudio: true);
                }
            }

            var video = new List<UdpForwardTarget>(targets.Count);
            var pose = new List<UdpForwardTarget>(targets.Count);
            var audio = new List<UdpForwardTarget>(targets.Count);

            foreach (var kv in targets)
            {
                var targetSessionId = kv.Key;
                var (ep, counter, wantVideo, wantPose, wantAudio) = kv.Value;

                if (wantVideo) video.Add(new UdpForwardTarget(ep, targetSessionId, counter));
                if (wantPose) pose.Add(new UdpForwardTarget(ep, targetSessionId, counter));
                if (wantAudio) audio.Add(new UdpForwardTarget(ep, targetSessionId, counter));
            }

            _forwardingTable[sourceEp] = video.ToImmutableArray();
            _poseForwardingTable[sourceEp] = pose.ToImmutableArray();
            _audioForwardingTable[sourceEp] = audio.ToImmutableArray();
        }

        /// <summary>
        /// 当订阅者状态变化时，重建相关发布者的转发表
        /// </summary>
        private void RebuildForwardingForSubscriber(string subscriberSessionId)
        {
            // 广播模式：任意会话的上线/换端口/下线都会影响所有发布者的转发表
            //（因为转发目标集合 = 全体已注册UDP会话 - 自己）
            if (AppConfig.IsBroadcastToAll)
            {
                foreach (var pubSessionId in _sessions.Keys)
                {
                    RebuildForwardingForPublisher(pubSessionId);
                }
                return;
            }

            // [Fix] 同时触发布者（互为配对）的转发表刷新
            // 因为配对也是一种隐含的“双向订阅”，当一方 UDP 就绪时，另一方需要知道去哪里转发。
            var partner = GetPairedSession(subscriberSessionId);
            if (!string.IsNullOrEmpty(partner))
            {
                RebuildForwardingForPublisher(partner);
            }

            // 非广播模式：只重建“订阅了该订阅者”的发布者
            foreach (var kv in _subscriptionMeta)
            {
                if (kv.Value.ContainsKey(subscriberSessionId))
                {
                    RebuildForwardingForPublisher(kv.Key);
                }
            }
        }

        /// <summary>
        /// 刷新反馈路由（用于 VR -> Robot 的直接转发）
        /// </summary>
        private void RefreshFeedbackRoute(string sessionId)
        {
            if (!_sessions.TryGetValue(sessionId, out var ctx) || ctx.UdpEndpoint == null)
            {
                return;
            }

            var partnerSession = GetPairedSession(sessionId);
            if (partnerSession == null) return;
            if (!_sessions.TryGetValue(partnerSession, out var partnerCtx) || partnerCtx.UdpEndpoint == null) return;

            IPEndPoint? vrEp = null;
            IPEndPoint? robotEp = null;
            string? vrSessionId = null;
            string? robotSessionId = null;

            if (ctx.Role == RegisterRequest.Types.EndpointType.Vr && partnerCtx.Role == RegisterRequest.Types.EndpointType.Robot)
            {
                vrEp = ctx.UdpEndpoint;
                robotEp = partnerCtx.UdpEndpoint;
                vrSessionId = sessionId;
                robotSessionId = partnerSession;
            }
            else if (ctx.Role == RegisterRequest.Types.EndpointType.Robot && partnerCtx.Role == RegisterRequest.Types.EndpointType.Vr)
            {
                vrEp = partnerCtx.UdpEndpoint;
                robotEp = ctx.UdpEndpoint;
                vrSessionId = partnerSession;
                robotSessionId = sessionId;
            }

            if (vrEp != null && robotEp != null && !string.IsNullOrEmpty(vrSessionId) && !string.IsNullOrEmpty(robotSessionId))
            {
                var counter = _forwardingMetrics.GetOrCreateEdge(vrSessionId, robotSessionId);
                _feedbackRoute[vrEp] = new UdpFeedbackForwardTarget(robotEp, robotSessionId, counter);
            }
        }

        /// <summary>
        /// 移除反馈路由
        /// </summary>
        private void RemoveFeedbackRoute(string sessionId)
        {
            if (_sessions.TryGetValue(sessionId, out var ctx) && ctx.UdpEndpoint != null)
            {
                _feedbackRoute.TryRemove(ctx.UdpEndpoint, out _);
            }
            if (_pairings.TryGetValue(sessionId, out var partner) && _sessions.TryGetValue(partner, out var pCtx) && pCtx.UdpEndpoint != null)
            {
                _feedbackRoute.TryRemove(pCtx.UdpEndpoint, out _);
            }
        }
    }
}
