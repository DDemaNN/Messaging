﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reactive.Disposables;
using System.Reflection;
using System.Runtime.CompilerServices;
using EventStore;
using EventStore.Dispatcher;
using Inceptum.Messaging.Contract;

namespace Inceptum.Cqrs
{

    class CommitDispatcher : IDispatchCommits
    {
        private CqrsEngine m_CqrsEngine;
        readonly Dictionary<Type, Action<object>> m_Callers = new Dictionary<Type, Action<object>>();
        private string m_BoundContext;


        public CommitDispatcher(CqrsEngine cqrsEngine,string boundContext)
        {
            m_BoundContext = boundContext;
            m_CqrsEngine = cqrsEngine;
        }

        public void Dispose()
        {
        }

        public void Dispatch(Commit commit)
        {
            foreach (EventMessage @event in commit.Events)
            {
                publishEvent(@event.Body);
/*
                //TODO: dirty implementation affecting performance. Need to update inceptum messaging to handle such scenario
                MethodInfo method = typeof(IMessagingEngine).GetMethods().FirstOrDefault(m => m.Name == "Send" && m.GetParameters().Count() == 2);
                MethodInfo genericMethod = method.MakeGenericMethod(new[] { @event.Body.GetType() });
                genericMethod.Invoke(m_MessagingEngine, new[] { @event.Body, m_Endpoint });
*/
            }
        }

        private void publishEvent(object e)
        {
            Action<object> caller;
            lock (m_Callers)
            {
                var type = e.GetType();
                if (!m_Callers.TryGetValue(type, out caller))
                {
                    var @event = Expression.Parameter(typeof(object), "event");
                    var call = Expression.Call(Expression.Constant(m_CqrsEngine), "PublishEvent", new[] { type }, @event, Expression.Convert(@event, type),Expression.Constant(m_BoundContext));
                    var lambda = (Expression<Action<object>>)Expression.Lambda(call, @event);
                    caller=lambda.Compile();                    
                    m_Callers.Add(type,caller);
                }
            }
            
            caller(e);
        }

    }

    public class CqrsEngine : ICqrsEngine, IDisposable
    {
        private readonly CommandDispatcher m_CommandDispatcher = new CommandDispatcher();
        private readonly EventDispatcher m_EventDispatcher = new EventDispatcher();
        private readonly Dictionary<string, BoundContext> m_LocalBoundContexts = new Dictionary<string, BoundContext>();
        private readonly IMessagingEngine m_MessagingEngine;
        private readonly Dictionary<string, BoundContext> m_RemoteBoundContexts = new Dictionary<string, BoundContext>();
        private CompositeDisposable m_Subscription;

        public CommandDispatcher CommandDispatcher
        {
            get { return m_CommandDispatcher; }
        }

        public EventDispatcher EventDispatcher
        {
            get { return m_EventDispatcher; }
        }

        public CqrsEngine(IMessagingEngine messagingEngine,Action<Configurator> config )
        {
            m_MessagingEngine = messagingEngine;
            var configurator = new Configurator();
            config(configurator);
            m_LocalBoundContexts = configurator.LocalBoundContexts.ToDictionary(bc => bc.Name);
            m_RemoteBoundContexts = configurator.RemoteBoundContexts.ToDictionary(bc => bc.Name);
            foreach (var localBoundContext in m_LocalBoundContexts.Values)
            {
                localBoundContext.InitEventStore(new CommitDispatcher(this,localBoundContext.Name));
            }
        }

        public void Init()
        {
            subscribe();
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Dispose()
        {
            if (m_Subscription != null)
                m_Subscription.Dispose();
        }

        public void SendCommand<T>(T command,string boundContext )
        {
            //TODO: add configuration validation: 2 BC can not listen for commands on same EP, remote BC can listen for particular command type only on single EP
            var bc = m_RemoteBoundContexts.Concat(m_LocalBoundContexts).FirstOrDefault(c => c.Key == boundContext);
            var routing = bc.Value.CommandsRouting.FirstOrDefault(r => r.Types.Contains(typeof (T)));
            if (routing != null)
            {
                var endpoint = routing.PublishEndpoint.Value;
                m_MessagingEngine.Send(command, endpoint);
            }
        }

        public void PublishEvent<T>(T @event,string boundContext)
        {
            //TODO: add configuration validation: local BC can publisdh particular event type only to single EP
            var bc = m_LocalBoundContexts.FirstOrDefault(c => c.Key == boundContext);
            var routing = bc.Value.EventsRouting.FirstOrDefault(r => r.Types.Contains(typeof (T)));
            if (routing != null)
            {
                var endpoint = routing.PublishEndpoint.Value;
                m_MessagingEngine.Send(@event, endpoint);
            }
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        private void subscribe()
        {
            var eventEndpointBindings = from bc in m_RemoteBoundContexts.Concat(m_LocalBoundContexts)
                                        from routing in bc.Value.EventsRouting
                                        let subscribeEndpoint = routing.SubscribeEndpoint
                                        where subscribeEndpoint != null
                                        group routing by new {endpoint = subscribeEndpoint.Value, boundContext = bc.Key}
                                        into grouping
                                        select new
                                                {
                                                    grouping.Key.boundContext,
                                                    grouping.Key.endpoint,
                                                    types = grouping.SelectMany(p => p.Types).Distinct().ToArray()
                                                };

            var commandEndpointBindings = from bc in m_LocalBoundContexts
                                          from routing in bc.Value.CommandsRouting
                                          let subscribeEndpoint = routing.SubscribeEndpoint
                                          where subscribeEndpoint != null
                                          group routing by new { endpoint = subscribeEndpoint.Value, boundContext = bc.Key }
                                          into grouping
                                          select new
                                                  {
                                                      grouping.Key.boundContext,
                                                      grouping.Key.endpoint,
                                                      types = grouping.SelectMany(p => p.Types).Distinct().ToArray()
                                                  };


            var eventSubscriptions =
                eventEndpointBindings.Select(binding => m_MessagingEngine.Subscribe(binding.endpoint, e => m_EventDispatcher.Dispacth(e, binding.boundContext), false, binding.types));
            var commandSubscriptions =
                commandEndpointBindings.Select(binding => m_MessagingEngine.Subscribe(binding.endpoint, e => m_CommandDispatcher.Dispacth(e, binding.boundContext), false, binding.types));

            m_Subscription = new CompositeDisposable(eventSubscriptions.Concat(commandSubscriptions).ToArray());
        }


    }


}