﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Castle.MicroKernel;
using Castle.MicroKernel.Facilities;
using Castle.MicroKernel.Registration;
using Inceptum.Cqrs.Configuration;


namespace Castle.MicroKernel.Registration
{
    public static class ComponentRegistrationExtensions
    {
        public static ComponentRegistration<T> AsEventsListener<T>(this ComponentRegistration<T> registration) where T : class
        {
            return registration.ExtendedProperties(new { IsEventsListener=true });
        }  
        
        public static ComponentRegistration<T> AsCommandsHandler<T>(this ComponentRegistration<T> registration, string localBoundContext) where T : class
        {
            return registration.ExtendedProperties(new { CommandsHandlerFor = localBoundContext });
        }
    }
}

namespace Inceptum.Cqrs
{



    public class CqrsFacility:AbstractFacility
    {
        private readonly Dictionary<IHandler, Action<IHandler>> m_WaitList = new Dictionary<IHandler, Action<IHandler>>();
        private ICqrsEngine m_CqrsEngine;
        private BoundContext[] m_BoundContexts;


        public CqrsFacility BoundContexts(params BoundContext[] boundContexts)
        {
            m_BoundContexts = boundContexts;
            return this;
        }

        protected override void Init()
        {
            Kernel.Register(Component.For<ICqrsEngine>().ImplementedBy<CqrsEngine>().DependsOn(new
                {
                    boundContexts = m_BoundContexts
                }));

            Kernel.ComponentRegistered += onComponentRegistered;
            Kernel.HandlersChanged += (ref bool changed) => processWaitList();
            m_CqrsEngine = Kernel.Resolve<ICqrsEngine>();
        }

       

        private void processWaitList()
        {
            foreach (var pair in m_WaitList.ToArray().Where(pair => pair.Key.CurrentState == HandlerState.Valid))
            {
                pair.Value(pair.Key);
                m_WaitList.Remove(pair.Key);
            }
        }

        private void registerEventsListener(IHandler handler)
        {
            m_CqrsEngine.WireEventsListener(Kernel.Resolve(handler.ComponentModel.Name, handler.ComponentModel.Services.First()));
        }

        private void registerIsCommandsHandler(IHandler handler, string localBoundContext)
        {
            m_CqrsEngine.WireCommandsHandler(Kernel.Resolve(handler.ComponentModel.Name, handler.ComponentModel.Services.First()), localBoundContext);
        }


        [MethodImpl(MethodImplOptions.Synchronized)]
        private void onComponentRegistered(string key, IHandler handler)
        {
            var isEventsListener = (bool) (handler.ComponentModel.ExtendedProperties["IsEventsListener"] ?? false);
            var commandsHandlerFor = (string)(handler.ComponentModel.ExtendedProperties["CommandsHandlerFor"]);
            var isCommandsHandler = commandsHandlerFor != null;

            if(isCommandsHandler&& isEventsListener)
                throw new InvalidOperationException("Component can not be events listener and commands handler simultaneousely");
            
            if (isEventsListener)
            {
                if (handler.CurrentState == HandlerState.WaitingDependency)
                {
                    m_WaitList.Add(handler,registerEventsListener);
                }
                else
                {
                    registerEventsListener(handler);
                }
            }


            if (isCommandsHandler)
            {
                if (handler.CurrentState == HandlerState.WaitingDependency)
                {
                    m_WaitList.Add(handler, handler1 => registerIsCommandsHandler(handler,commandsHandlerFor));
                }
                else
                {
                    registerIsCommandsHandler(handler, commandsHandlerFor);
                }
            }

            processWaitList();
        }

        
    }
}