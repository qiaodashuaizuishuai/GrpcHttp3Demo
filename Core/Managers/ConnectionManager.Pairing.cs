using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using GrpcHttp3Demo.Protos;
using GrpcHttp3Demo.Models;

namespace GrpcHttp3Demo.Core.Managers
{
    public partial class ConnectionManager
    {
        private static string CreatePairKey(string a, string b)
        {
            // Ensure symmetric key independent of call side.
            return string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
        }

        public byte[] GetOrCreateP2pSharedKey(string sessionA, string sessionB)
        {
            var key = CreatePairKey(sessionA, sessionB);
            return _p2pSharedKeys.GetOrAdd(key, _ =>
            {
                var bytes = new byte[32];
                RandomNumberGenerator.Fill(bytes);
                return bytes;
            });
        }

        private void RemoveP2pSharedKey(string sessionA, string sessionB)
        {
            var key = CreatePairKey(sessionA, sessionB);
            _p2pSharedKeys.TryRemove(key, out _);
        }

        /// <summary>
        /// 获取已配对的会话ID
        /// </summary>
        /// <param name="sessionId">当前会话ID</param>
        /// <returns>配对的会话ID，如果未配对则返回null</returns>
        public string? GetPairedSession(string sessionId)
        {
            return _pairings.TryGetValue(sessionId, out var partnerSessionId) ? partnerSessionId : null;
        }

        /// <summary>
        /// 根据角色获取未配对的端点列表
        /// </summary>
        /// <param name="desiredRole">期望的角色</param>
        /// <returns>未配对端点列表</returns>
        public List<UnpairedEndpoint> GetUnpairedByRole(RegisterRequest.Types.EndpointType desiredRole)
        {
            var list = new List<UnpairedEndpoint>();
            foreach (var ctx in _sessions.Values)
            {
                if (ctx.Role != desiredRole) continue;
                if (_pairings.ContainsKey(ctx.SessionId)) continue;

                list.Add(new UnpairedEndpoint
                {
                    SessionId = ctx.SessionId,
                    DeviceId = ctx.DeviceId,
                    Role = ctx.Role
                });
            }
            return list;
        }

        /// <summary>
        /// 建立两个会话之间的配对关系
        /// </summary>
        /// <param name="sessionA">会话A</param>
        /// <param name="sessionB">会话B</param>
        public void PairSessions(string sessionA, string sessionB)
        {
            // 幂等：如果已经互为配对，不做任何变更。
            if (GetPairedSession(sessionA) == sessionB && GetPairedSession(sessionB) == sessionA)
            {
                return;
            }

            // 防御：若任一方已与他人配对，先解除旧配对，避免残留反向映射。
            UnpairSession(sessionA);
            UnpairSession(sessionB);

            _pairings[sessionA] = sessionB;
            _pairings[sessionB] = sessionA;
            
            if (_sessions.TryGetValue(sessionA, out var ctxA)) ctxA.PairedDeviceId = ctxBId(sessionB);
            if (_sessions.TryGetValue(sessionB, out var ctxB)) ctxB.PairedDeviceId = ctxBId(sessionA);

            Console.WriteLine($"[ConnMgr] Paired sessions: {sessionA} <-> {sessionB}");

            // 配对成功后，刷新反馈路由（因为反馈路由依赖配对关系）
            RefreshFeedbackRoute(sessionA);
            RefreshFeedbackRoute(sessionB);

            // 配对会改变默认转发目标：重建双方转发表
            RebuildForwardingForPublisher(sessionA);
            RebuildForwardingForPublisher(sessionB);

            string? ctxBId(string s) => _sessions.TryGetValue(s, out var c) ? c.DeviceId : null;
        }

        /// <summary>
        /// 解除会话的配对关系
        /// </summary>
        /// <param name="sessionId">要解除配对的会话ID</param>
        public void UnpairSession(string sessionId)
        {
            if (_pairings.TryRemove(sessionId, out var partnerSession))
            {
                _pairings.TryRemove(partnerSession, out _);

                // 清理 P2P 密钥（如果后续重新配对，将生成新密钥）
                RemoveP2pSharedKey(sessionId, partnerSession);

                if (_sessions.TryGetValue(sessionId, out var ctx)) ctx.PairedDeviceId = null;
                if (_sessions.TryGetValue(partnerSession, out var pCtx)) pCtx.PairedDeviceId = null;
                
                // 解除配对后，移除反馈路由
                RemoveFeedbackRoute(sessionId);
                RemoveFeedbackRoute(partnerSession);

                // 解除配对会改变默认转发目标：重建双方转发表
                RebuildForwardingForPublisher(sessionId);
                RebuildForwardingForPublisher(partnerSession);
                
                Console.WriteLine($"[ConnMgr] Unpaired sessions: {sessionId} <-> {partnerSession}");
            }
        }
    }
}
