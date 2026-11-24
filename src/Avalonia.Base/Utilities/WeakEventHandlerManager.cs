using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Avalonia.Collectible;

namespace Avalonia.Utilities
{
    /// <summary>
    /// Manages subscriptions to events using weak listeners.
    /// </summary>
    public static class WeakEventHandlerManager
    {
        /// <summary>
        /// Subscribes to an event on an object using a weak subscription.
        /// </summary>
        /// <typeparam name="TTarget">The type of the target.</typeparam>
        /// <typeparam name="TEventArgs">The type of the event arguments.</typeparam>
        /// <typeparam name="TSubscriber">The type of the subscriber.</typeparam>
        /// <param name="target">The event source.</param>
        /// <param name="eventName">The name of the event.</param>
        /// <param name="subscriber">The subscriber.</param>
        public static void Subscribe<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicEvents | DynamicallyAccessedMemberTypes.NonPublicEvents)] TTarget, TEventArgs, TSubscriber>(
            TTarget target, string eventName, EventHandler<TEventArgs> subscriber)
            where TEventArgs : EventArgs where TSubscriber : class
        {
            _ = target ?? throw new ArgumentNullException(nameof(target));

            var dic = SubscriptionTypeStorage<TEventArgs, TSubscriber>.Subscribers.GetOrCreateValue(target);

            if (!dic.TryGetValue(eventName, out var sub))
            {
                dic[eventName] = sub = new Subscription<TEventArgs, TSubscriber>(dic, typeof(TTarget), target, eventName);
            }

            sub.Add(subscriber);
        }

        /// <summary>
        /// Unsubscribes from an event.
        /// </summary>
        /// <typeparam name="TEventArgs">The type of the event arguments.</typeparam>
        /// <typeparam name="TSubscriber">The type of the subscriber.</typeparam>
        /// <param name="target">The event source.</param>
        /// <param name="eventName">The name of the event.</param>
        /// <param name="subscriber">The subscriber.</param>
        public static void Unsubscribe<TEventArgs, TSubscriber>(object target, string eventName, EventHandler<TEventArgs> subscriber)
            where TEventArgs : EventArgs where TSubscriber : class
        {
            if (SubscriptionTypeStorage<TEventArgs, TSubscriber>.Subscribers.TryGetValue(target, out var dic))
            {
                if (dic.TryGetValue(eventName, out var sub))
                {
                    sub.Remove(subscriber);
                }
            }
        }

        private static class SubscriptionTypeStorage<TArgs, TSubscriber>
            where TArgs : EventArgs where TSubscriber : class
        {
            public static readonly ConditionalWeakTable<object, SubscriptionDic<TArgs, TSubscriber>> Subscribers = new();
        }

        private class SubscriptionDic<T, TSubscriber> : Dictionary<string, Subscription<T, TSubscriber>>
            where T : EventArgs where TSubscriber : class
        {
        }

        private static readonly Dictionary<Type, Dictionary<string, EventInfo>> s_accessors = new();
        private static readonly object s_trackingLock = new();
        private static readonly List<WeakReference<ICollectibleSubscriptionTarget>> s_trackedSubscriptions = new();

        private static void TrackSubscription(ICollectibleSubscriptionTarget subscription)
        {
            lock (s_trackingLock)
            {
                s_trackedSubscriptions.Add(new WeakReference<ICollectibleSubscriptionTarget>(subscription));
            }
        }

        internal static void SweepCollectible(IReadOnlySet<AssemblyLoadContext> retiringContexts)
        {
            if (retiringContexts.Count == 0)
            {
                return;
            }

            List<ICollectibleSubscriptionTarget>? liveSubscriptions = null;
            lock (s_trackingLock)
            {
                for (var i = s_trackedSubscriptions.Count - 1; i >= 0; i--)
                {
                    if (s_trackedSubscriptions[i].TryGetTarget(out var subscription))
                    {
                        liveSubscriptions ??= new List<ICollectibleSubscriptionTarget>();
                        liveSubscriptions.Add(subscription);
                    }
                    else
                    {
                        s_trackedSubscriptions.RemoveAt(i);
                    }
                }
            }

            if (liveSubscriptions is not null)
            {
                foreach (var subscription in liveSubscriptions)
                {
                    subscription.SweepSubscriptionCollectible(retiringContexts);
                }
            }

            SweepAccessorsOnly(retiringContexts);
        }

        internal static void SweepAccessorsOnly(IReadOnlySet<AssemblyLoadContext> retiringContexts)
        {
            if (retiringContexts.Count == 0)
            {
                return;
            }

            lock (s_accessors)
            {
                List<Type>? typesToRemove = null;
                foreach (var pair in s_accessors)
                {
                    if (CollectibleAssemblyCleanup.References(pair.Key, retiringContexts))
                    {
                        typesToRemove ??= new List<Type>();
                        typesToRemove.Add(pair.Key);
                        continue;
                    }

                    List<string>? eventsToRemove = null;
                    foreach (var eventPair in pair.Value)
                    {
                        if (CollectibleAssemblyCleanup.References(eventPair.Value, retiringContexts))
                        {
                            eventsToRemove ??= new List<string>();
                            eventsToRemove.Add(eventPair.Key);
                        }
                    }

                    if (eventsToRemove is not null)
                    {
                        foreach (var eventName in eventsToRemove)
                        {
                            pair.Value.Remove(eventName);
                        }
                    }

                    if (pair.Value.Count == 0)
                    {
                        typesToRemove ??= new List<Type>();
                        typesToRemove.Add(pair.Key);
                    }
                }

                if (typesToRemove is not null)
                {
                    foreach (var type in typesToRemove)
                    {
                        s_accessors.Remove(type);
                    }
                }
            }
        }

        private class Subscription<T, TSubscriber> : ICollectibleSubscriptionTarget
            where T : EventArgs where TSubscriber : class
        {
            private readonly EventInfo _info;
            private readonly SubscriptionDic<T, TSubscriber> _sdic;
            private readonly object _target;
            private readonly string _eventName;
            private readonly Delegate _delegate;

            private Descriptor[] _data = new Descriptor[2];
            private int _count;
            private bool _isTracked;

            private delegate void CallerDelegate(TSubscriber s, object? sender, T args);

            private struct Descriptor
            {
                public WeakReference<TSubscriber>? Subscriber;
                public CallerDelegate? Caller;
            }

            private static readonly Dictionary<MethodInfo, CallerDelegate> s_callers = new();
            
            public Subscription(SubscriptionDic<T, TSubscriber> sdic,
                [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicEvents | DynamicallyAccessedMemberTypes.NonPublicEvents)] Type targetType,
                object target, string eventName)
            {
                _sdic = sdic;
                _target = target;
                _eventName = eventName;
                if (!s_accessors.TryGetValue(targetType, out var evDic))
                    s_accessors[targetType] = evDic = new Dictionary<string, EventInfo>();

                if (evDic.TryGetValue(eventName, out var info))
                {
                    _info = info;
                }
                else
                {
                    var ev = targetType.GetRuntimeEvents().FirstOrDefault(x => x.Name == eventName);

                    if (ev == null)
                    {
                        throw new ArgumentException(
                            $"The event {eventName} was not found on {target.GetType()}.");
                    }

                    evDic[eventName] = _info = ev;
                }

                var del = new Action<object, T>(OnEvent);
                _delegate = del.GetMethodInfo().CreateDelegate(_info.EventHandlerType!, del.Target);
                _info.AddMethod!.Invoke(target, new object?[] { _delegate });
            }

            private void Destroy()
            {
                _info.RemoveMethod!.Invoke(_target, new object?[] { _delegate });
                _sdic.Remove(_eventName);
                if (_sdic.Count == 0)
                {
                    SubscriptionTypeStorage<T, TSubscriber>.Subscribers.Remove(_target);
                }
            }

            public void Add(EventHandler<T> s)
            {
                Compact(true);
                if (_count == _data.Length)
                {
                    //Extend capacity
                    var ndata = new Descriptor[_data.Length*2];
                    Array.Copy(_data, ndata, _data.Length);
                    _data = ndata;
                }

                MethodInfo method = s.Method;

                var subscriber = (TSubscriber)s.Target!;
                if (!s_callers.TryGetValue(method, out var caller))
                    s_callers[method] = caller =
                        (CallerDelegate)Delegate.CreateDelegate(typeof(CallerDelegate), null, method);
                _data[_count] = new Descriptor
                {
                    Caller = caller,
                    Subscriber = new WeakReference<TSubscriber>(subscriber)
                };
                _count++;

                if (!_isTracked)
                {
                    _isTracked = true;
                    TrackSubscription(this);
                }
            }

            public void Remove(EventHandler<T> s)
            {
                var removed = false;

                for (int c = 0; c < _count; ++c)
                {
                    var reference = _data[c].Subscriber;

                    if (reference != null && reference.TryGetTarget(out var instance) && Equals(instance, s.Target))
                    {
                        _data[c] = default;
                        removed = true;
                    }
                }

                if (removed)
                {
                    Compact();
                }
            }

            private void Compact(bool preventDestroy = false)
            {
                var previousCount = _count;
                int empty = -1;
                for (int c = 0; c < _count; c++)
                {
                    var r = _data[c];

                    TSubscriber? target = null;

                    r.Subscriber?.TryGetTarget(out target);

                    //Mark current index as first empty
                    if (target == null && empty == -1)
                        empty = c;
                    //If current element isn't null and we have an empty one
                    if (target != null && empty != -1)
                    {
                        _data[c] = default;
                        _data[empty] = r;
                        empty++;
                    }
                }
                if (empty != -1)
                    _count = empty;
                if (_count < previousCount)
                    Array.Clear(_data, _count, previousCount - _count);
                if (_count == 0 && !preventDestroy)
                    Destroy();
            }

            public void SweepSubscriptionCollectible(IReadOnlySet<AssemblyLoadContext> retiringContexts)
            {
                if (typeof(TSubscriber).IsCollectible && CollectibleAssemblyCleanup.References(typeof(TSubscriber), retiringContexts))
                {
                    s_callers.Clear();
                }
                else if (!typeof(TSubscriber).IsCollectible)
                {
                    List<MethodInfo>? methodsToRemove = null;
                    foreach (var callerPair in s_callers)
                    {
                        if (CollectibleAssemblyCleanup.References(callerPair.Key, retiringContexts))
                        {
                            methodsToRemove ??= new List<MethodInfo>();
                            methodsToRemove.Add(callerPair.Key);
                        }
                    }

                    if (methodsToRemove is not null)
                    {
                        foreach (var method in methodsToRemove)
                        {
                            s_callers.Remove(method);
                        }
                    }
                }

                if ((typeof(TSubscriber).IsCollectible && CollectibleAssemblyCleanup.References(typeof(TSubscriber), retiringContexts)) ||
                    (typeof(T).IsCollectible && CollectibleAssemblyCleanup.References(typeof(T), retiringContexts)) ||
                    CollectibleAssemblyCleanup.ReferencesObject(_target, retiringContexts) ||
                    CollectibleAssemblyCleanup.References(_info, retiringContexts))
                {
                    Array.Clear(_data, 0, _data.Length);
                    _count = 0;
                    Destroy();
                    return;
                }

                var hasRemoved = false;
                for (var i = 0; i < _count; i++)
                {
                    var descriptor = _data[i];
                    var shouldRemove = descriptor.Subscriber is null ||
                        !descriptor.Subscriber.TryGetTarget(out var subscriber) ||
                        CollectibleAssemblyCleanup.ReferencesObject(subscriber, retiringContexts) ||
                        CollectibleAssemblyCleanup.References(descriptor.Caller, retiringContexts);

                    if (shouldRemove)
                    {
                        _data[i] = default;
                        hasRemoved = true;
                    }
                }

                if (hasRemoved)
                {
                    Compact();
                }
            }

            private void OnEvent(object? sender, T eventArgs)
            {
                var needCompact = false;
                for (var c = 0; c < _count; c++)
                {
                    var r = _data[c].Subscriber!;
                    if (r.TryGetTarget(out var sub))
                    {
                        _data[c].Caller!(sub, sender, eventArgs);
                    }
                    else
                        needCompact = true;
                }
                if (needCompact)
                    Compact();
            }
        }
    }
}
