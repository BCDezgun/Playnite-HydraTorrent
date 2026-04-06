using System;
using System.Collections.Generic;

namespace HydraTorrent.Services
{
    public class ServiceContainer
    {
        private readonly Dictionary<Type, Func<object>> _factories = new Dictionary<Type, Func<object>>();
        private readonly Dictionary<Type, object> _singletons = new Dictionary<Type, object>();

        public void Register<TInterface, TImplementation>(bool singleton = true)
            where TImplementation : TInterface, new()
        {
            if (singleton)
            {
                _factories[typeof(TInterface)] = () =>
                {
                    if (!_singletons.TryGetValue(typeof(TInterface), out var instance))
                    {
                        instance = new TImplementation();
                        _singletons[typeof(TInterface)] = instance;
                    }
                    return instance;
                };
            }
            else
            {
                _factories[typeof(TInterface)] = () => new TImplementation();
            }
        }

        public void Register<TInterface>(Func<TInterface> factory, bool singleton = true)
        {
            if (singleton)
            {
                object cached = null;
                _factories[typeof(TInterface)] = () =>
                {
                    if (cached == null)
                    {
                        cached = factory();
                    }
                    return cached;
                };
            }
            else
            {
                _factories[typeof(TInterface)] = () => factory();
            }
        }

        public void RegisterInstance<TInterface>(TInterface instance)
        {
            _singletons[typeof(TInterface)] = instance;
            _factories[typeof(TInterface)] = () => instance;
        }

        public TInterface Resolve<TInterface>()
        {
            if (_factories.TryGetValue(typeof(TInterface), out var factory))
            {
                return (TInterface)factory();
            }

            throw new InvalidOperationException($"Service {typeof(TInterface).Name} is not registered.");
        }

        public bool IsRegistered<TInterface>()
        {
            return _factories.ContainsKey(typeof(TInterface));
        }
    }
}
