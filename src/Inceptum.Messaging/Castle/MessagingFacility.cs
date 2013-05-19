﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Castle.Core;
using Castle.MicroKernel;
using Castle.MicroKernel.Facilities;
using Castle.MicroKernel.Registration;
using Inceptum.Core;
using Inceptum.Messaging.Contract;
using Inceptum.Messaging.Serialization;

namespace Inceptum.Messaging.Castle
{
    public class MessagingFacility : AbstractFacility
    {
        private IDictionary<string, TransportInfo> m_Transports;
        private readonly IDictionary<string, JailStrategy> m_JailStrategies;
        private readonly List<IHandler> m_SerializerWaitList = new List<IHandler>();
        private readonly List<IHandler> m_SerializerFactoryWaitList = new List<IHandler>();
        private ISerializationManager m_SerializationManager;

        public IDictionary<string, TransportInfo> Transports
        {
            get { return m_Transports; }
            set { m_Transports = value; }
        }


        public MessagingFacility()
        {
        }

        public MessagingFacility(IDictionary<string, TransportInfo> transports, IDictionary<string, JailStrategy> jailStrategies = null)
        {
            m_Transports = transports;
            m_JailStrategies = jailStrategies;
        }

        protected override void Init()
        {
            Kernel.Register(
                Component.For<IMessagingEngine>().ImplementedBy<MessagingEngine>(),
                Component.For<ISerializationManager>().ImplementedBy<SerializationManager>()
                );

            if (m_Transports != null)
            {
                Kernel.Register(Component.For<ITransportResolver>().ImplementedBy<TransportResolver>().DependsOn(new { transports = m_Transports, jailStrategies = m_JailStrategies }));
            }

            m_SerializationManager = Kernel.Resolve<ISerializationManager>();
            Kernel.ComponentRegistered += onComponentRegistered;
            Kernel.ComponentModelCreated += ProcessModel;
            //TODO: make optional
            Kernel.Register(Component.For<ISerializerFactory>().ImplementedBy<ProtobufSerializerFactory>());
        }

        [MethodImpl(MethodImplOptions.Synchronized)]
        private void onComponentRegistered(string key, IHandler handler)
        {
            if ((bool)(handler.ComponentModel.ExtendedProperties["IsSerializer"] ?? false))
            {
                if (handler.CurrentState == HandlerState.WaitingDependency)
                {
                    m_SerializerWaitList.Add(handler);
                }
                else
                {
                    registerSerializer(handler);
                }
            }

            if ((bool)(handler.ComponentModel.ExtendedProperties["IsSerializerFactory"] ?? false))
            {
                if (handler.CurrentState == HandlerState.WaitingDependency)
                {
                    m_SerializerFactoryWaitList.Add(handler);
                }
                else
                {
                    registerSerializerFactory(handler);
                }
            }

            processWaitList();
        }

        private void registerSerializerFactory(IHandler handler)
        {
            m_SerializationManager.RegisterSerializerFactory(Kernel.Resolve(handler.ComponentModel.Name,typeof(ISerializerFactory)) as ISerializerFactory);
        }

        private void registerSerializer(IHandler handler)
        {
            var type = handler.ComponentModel.ExtendedProperties["SerializableType"] as Type;
            m_SerializationManager.RegisterSerializer(type, Kernel.Resolve(handler.ComponentModel.Name, typeof (IMessageSerializer<>).MakeGenericType(type)));
        }

        private void onHandlerStateChanged(object source, EventArgs args)
        {
            processWaitList();
        }




        private void processWaitList()
        {
            foreach (var serializerHandler in m_SerializerWaitList.ToArray().Where(serializerHandler => serializerHandler.CurrentState == HandlerState.Valid))
            {
                registerSerializer(serializerHandler);
                m_SerializerWaitList.Remove(serializerHandler);
            }

            foreach (var factoryHandler in m_SerializerFactoryWaitList.ToArray().Where(factoryHandler => factoryHandler.CurrentState == HandlerState.Valid))
            {
                registerSerializerFactory(factoryHandler);
                m_SerializerWaitList.Remove(factoryHandler);
            }
        }


        public void ProcessModel(ComponentModel model)
        {
            var serializerType = model.Services.FirstOrDefault(s=> s.IsGenericType && s.GetGenericTypeDefinition() == typeof(IMessageSerializer<>));
            if (serializerType!=null)
            {
                model.ExtendedProperties["IsSerializer"] = true;
                model.ExtendedProperties["SerializableType"] = serializerType.GetGenericArguments()[0];
            }
            else
            {
                model.ExtendedProperties["IsSerializer"] = false;
            }        
    
            
            if (model.Services.Contains(typeof(ISerializerFactory)))
            {
                model.ExtendedProperties["IsSerializerFactory"] = true;
            }
            else
            {
                model.ExtendedProperties["IsSerializerFactory"] = false;
            }
        }
    }
}