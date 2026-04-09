using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using GrpcHttp3Demo.Models;
using GrpcHttp3Demo.Utils;

namespace GrpcHttp3Demo.Core.Managers
{
    public partial class ConnectionManager
    {
        /// <summary>
        /// 更新订阅状态
        /// </summary>
        /// <param name="publisherSessionId">发布者会话ID</param>
        /// <param name="subscriberSessionId">订阅者会话ID</param>
        /// <param name="isSub">true为订阅，false为取消订阅</param>
        /// <param name="subVideo">是否订阅视频</param>
        /// <param name="subPose">是否订阅姿态</param>
        /// <param name="subAudio">是否订阅音频</param>
        public void UpdateSubscription(string publisherSessionId, string subscriberSessionId, bool isSub, bool subVideo, bool subPose, bool subAudio)
        {
            if (_sessions.TryGetValue(publisherSessionId, out var pubCtx))
            {
                if (isSub)
                {
                    // 更新发布者上下文中的订阅者列表
                    pubCtx.Subscribers[subscriberSessionId] = new SubscriptionDetail
                    {
                        SubscriberId = subscriberSessionId,
                        SubVideo = subVideo,
                        SubPose = subPose,
                        SubAudio = subAudio
                    };

                    // 更新全局订阅元数据表
                    var meta = _subscriptionMeta.GetOrAdd(publisherSessionId, _ => new ConcurrentDictionary<string, SubscriptionMeta>());
                    meta[subscriberSessionId] = new SubscriptionMeta
                    {
                        SubscriberId = subscriberSessionId,
                        SubVideo = subVideo,
                        SubPose = subPose,
                        SubAudio = subAudio,
                        LastUpdatedUtc = DateTime.UtcNow
                    };

                    // 触发转发表重建
                    RebuildForwardingForPublisher(publisherSessionId);
                    Console.WriteLine($"[ConnMgr] {subscriberSessionId} subscribed to {publisherSessionId} (V:{subVideo} P:{subPose} A:{subAudio})");
                }
                else
                {
                    // 移除订阅
                    pubCtx.Subscribers.TryRemove(subscriberSessionId, out _);
                    if (_subscriptionMeta.TryGetValue(publisherSessionId, out var meta))
                    {
                        meta.TryRemove(subscriberSessionId, out _);
                    }

                    // 触发转发表重建
                    RebuildForwardingForPublisher(publisherSessionId);
                    Console.WriteLine($"[ConnMgr] {subscriberSessionId} unsubscribed from {publisherSessionId}");
                }
            }
        }

        /// <summary>
        /// 获取指定发布者的所有订阅者ID列表
        /// </summary>
        /// <param name="publisherSessionId">发布者会话ID</param>
        /// <returns>订阅者ID列表</returns>
        public List<string> GetSubscribers(string publisherSessionId)
        {
            if (_sessions.TryGetValue(publisherSessionId, out var ctx))
            {
                return ctx.Subscribers.Keys.ToList();
            }
            return new List<string>();
        }

        /// <summary>
        /// 获取用于转发 VideoConfig 的目标会话：配对端 + 订阅了该发布者视频的订阅者（去重）。
        /// </summary>
        public IReadOnlyCollection<string> GetVideoConfigTargets(string publisherSessionId)
        {
            var targets = new HashSet<string>(StringComparer.Ordinal);

            var paired = GetPairedSession(publisherSessionId);
            if (!string.IsNullOrEmpty(paired))
            {
                targets.Add(paired);
            }

            if (_sessions.TryGetValue(publisherSessionId, out var ctx))
            {
                foreach (var sub in ctx.Subscribers.Values)
                {
                    if (sub.SubVideo && !string.IsNullOrEmpty(sub.SubscriberId))
                    {
                        targets.Add(sub.SubscriberId);
                    }
                }
            }

            targets.Remove(publisherSessionId);
            return targets.ToList();
        }

        /// <summary>
        /// 获取用于转发 AudioConfig 的目标会话：配对端 + 订阅了该发布者音频的订阅者（去重）。
        /// </summary>
        public IReadOnlyCollection<string> GetAudioConfigTargets(string publisherSessionId)
        {
            var targets = new HashSet<string>(StringComparer.Ordinal);

            var paired = GetPairedSession(publisherSessionId);
            if (!string.IsNullOrEmpty(paired))
            {
                targets.Add(paired);
            }

            if (_sessions.TryGetValue(publisherSessionId, out var ctx))
            {
                foreach (var sub in ctx.Subscribers.Values)
                {
                    if (sub.SubAudio && !string.IsNullOrEmpty(sub.SubscriberId))
                    {
                        targets.Add(sub.SubscriberId);
                    }
                }
            }

            targets.Remove(publisherSessionId);
            return targets.ToList();
        }
    }
}
