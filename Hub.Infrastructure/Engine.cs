﻿using Autofac;
using System.Configuration;
using System.Collections.Specialized;
using Hub.Infrastructure.Extensions;
using System.Runtime.CompilerServices;
using System.Reflection;
using Hub.Infrastructure.DependencyInjection.Interfaces;
using Newtonsoft.Json;
using Hub.Infrastructure.DependencyInjection;
using Hub.Infrastructure.Autofac;
using AutoMapper;
using Hub.Infrastructure.Mapper;
using Hub.Infrastructure.MultiTenant;
using Autofac.Core;
using Hub.Infrastructure.Localization.Interfaces;
using System.Globalization;
using Hub.Infrastructure.Database.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Hub.Infrastructure
{
    public static class Engine
    {
        #region APP SETTINGS 

        private static object appSettingsLock = new object();
        static NameValueCollection ConfigCollection = null;
        static Dictionary<string, NameValueCollection> TenantConfigCollection = new Dictionary<string, NameValueCollection>();
        static AsyncLocal<NameValueCollection> AsyncLocalConfigCollection = new AsyncLocal<NameValueCollection>();


        public static NameValueCollection AppSettings
        {
            get
            {
                if (ConfigCollection == null)
                {
                    lock (appSettingsLock)
                    {
                        if (ConfigCollection == null)
                        {
                            NameValueCollection all;

                            if (ConfigurationManager.AppSettings.Keys.Count != 0)
                            {
                                all = new NameValueCollection(ConfigurationManager.AppSettings);
                            }
                            else
                            {
                                all = new NameValueCollection(Environment.GetEnvironmentVariables().ToNameValueCollection());
                            }

                            // Aplicar chaves de debug (substituir qualquer chave quando em modo Debug)
                            // Exemplo de chave no appsettings.json: "Debug:elos-api-endpoint"
                            foreach (var debugKey in all.AllKeys.Where(a => a.StartsWith("Debug:")))
                            {
                                var originalKey = debugKey.Replace("Debug:", "");

                                if (all.AllKeys.Contains(originalKey))
                                {
                                    all[originalKey] = all[debugKey];
                                }
                                else
                                {
                                    all.Add(originalKey, all[debugKey]);
                                }
                            }

                            ConfigCollection = all;
                        }
                    }
                }


                return ConfigCollection;
            }
        }

        #endregion

        #region CONNECTION STRINGS 

        public static string ConnectionString(string settingName)
        {
            var connectionString = ConfigurationManager.ConnectionStrings[settingName];

            if (connectionString != null)
                return connectionString.ConnectionString;
            else
            {
                var key = AppSettings[$"ConnectionString-{settingName}"];

                if (key != null)
                {
                    return key;
                }

                return Environment.GetEnvironmentVariable($"ConnectionString-{settingName}");
            }
        }

        #endregion

        #region MAPPER 

        private static List<IAutoMapperStartup> _autoMapperStartups;

        public static void RunAutoMapperStartups(IMapperConfigurationExpression cfg)
        {
            if (_autoMapperStartups == null) return;

            foreach (var item in _autoMapperStartups)
            {
                item.RegisterMaps(cfg);
            }
        }

        #endregion

        private static readonly AsyncLocal<ILifetimeScope> CurrentScope = new AsyncLocal<ILifetimeScope>();
        private static IContainer _container;
        private static Action _initializeAction;
        private static ContainerManager _containerManager;
        private static ILocalizationProvider _localizationProvider;

        public static Assembly ExecutingAssembly { get; private set; }

        public static IContainer Container
        {
            get => _container;
            private set
            {
                _container = value;
                _initializeAction?.Invoke();
            }
        }

        public static ContainerManager ContainerManager
        {
            get { return _containerManager; }
        }


        //[MethodImpl(MethodImplOptions.Synchronized)]
        //public static void Initialize(Assembly executingAssembly, IList<IDependencyConfiguration> dependencyRegistrars = null, ContainerBuilder containerBuilder = null)
        //{
        //    ExecutingAssembly = executingAssembly;

        //    JsonConvert.DefaultSettings = () => new JsonSerializerSettings
        //    {
        //        TypeNameHandling = TypeNameHandling.Auto
        //    };

        //    dependencyRegistrars ??= new List<IDependencyConfiguration>();
        //    dependencyRegistrars.Add(new DependencyConfiguration());

        //    _containerManager = new ContainerManager(dependencyRegistrars, containerBuilder);

        //    if (_autoMapperStartups?.Count > 0)
        //    {
        //        var config = new MapperConfiguration(cfg =>
        //        {
        //            Engine.RunAutoMapperStartups(cfg);
        //        });

        //        Singleton<IMapper>.Instance = config.CreateMapper();
        //    }

        //    if (_containerManager.Container != null)
        //    {
        //        _containerManager.Container.Resolve<IMapper>();
        //    }
        //}

        // ISchemaNameProvider nameProvider = null, 

        [MethodImpl(MethodImplOptions.Synchronized)]
        public static void Initialize(
            Assembly executingAssembly,
            IList<IDependencyConfiguration> dependencyRegistrars = null,
            ContainerBuilder containerBuilder = null)
        {
            ExecutingAssembly = executingAssembly;

            JsonConvert.DefaultSettings = () => new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto
            };

            if (dependencyRegistrars == null)
            {
                dependencyRegistrars = new List<IDependencyConfiguration>();
            }

            dependencyRegistrars.Add(new DependencyConfiguration());

            _containerManager = new ContainerManager(dependencyRegistrars, containerBuilder);

            _initializeAction = new Action(() =>
            {
                TryResolve(out _localizationProvider);

                // Registrar suporte a múltiplos tenants
                var services = new ServiceCollection();
                services.AddTenantSupport();


                // Montar o provedor de serviços
                var serviceProvider = services.BuildServiceProvider();

                // Obter os tenants ativos
                var tenantOptions = serviceProvider.GetRequiredService<TenantConfigurationOptions>();

                // Exemplo de uso dos tenants
                foreach (var tenant in tenantOptions.Tenants)
                {
                    Console.WriteLine($"Tenant ativo: {tenant.Name} - {tenant.ConnectionString}");
                }
            });

            if (_containerManager.Container != null)
            {
                _initializeAction();
            }
        }


        public static IDisposable BeginIgnoreTenantConfigs(bool ignoreTenantConfigs = true)
        {
            return new IgnoreTenantConfigScopeDisposable(ignoreTenantConfigs);
        }

        public static AsyncLocal<bool> IgnoreTenantConfigsScope = new AsyncLocal<bool>();

        class IgnoreTenantConfigScopeDisposable : IDisposable
        {
            private bool originalValue;

            public IgnoreTenantConfigScopeDisposable()
            {
                originalValue = IgnoreTenantConfigsScope.Value;
                IgnoreTenantConfigsScope.Value = true;
            }

            public IgnoreTenantConfigScopeDisposable(bool ignoreValue)
            {
                originalValue = IgnoreTenantConfigsScope.Value;
                IgnoreTenantConfigsScope.Value = ignoreValue;
            }

            public void Dispose()
            {
                IgnoreTenantConfigsScope.Value = originalValue;
            }
        }

        // Iniciar o escopo para o tenant
        public static IDisposable BeginLifetimeScope(string tenant)
        {
            // Inicia o escopo do tenant e configura o contexto
            TenantContext.BeginScope(tenant);
            var lifetimeScope = _container.BeginLifetimeScope(builder =>
            {
                // Registra a dependência de tenant específico no escopo
                builder.RegisterInstance(tenant).As<string>().SingleInstance();
            });

            // Define o escopo atual
            CurrentScope.Value = lifetimeScope;

            return new TenantScopeDisposable(lifetimeScope);
        }

        // Finaliza o escopo do tenant
        public static void EndTenantScope()
        {
            TenantContext.EndScope();

            if (CurrentScope.Value != null)
            {
                CurrentScope.Value.Dispose();
                CurrentScope.Value = null;
            }
        }

        // Classe responsável por descartar o escopo do tenant
        private class TenantScopeDisposable : IDisposable
        {
            private readonly ILifetimeScope _lifetimeScope;
            private bool _disposed;

            public TenantScopeDisposable(ILifetimeScope lifetimeScope)
            {
                _lifetimeScope = lifetimeScope;
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _lifetimeScope.Dispose();
                    _disposed = true;
                }
            }
        }

        #region RESOLVES 

        public static T Resolve<T>()
        {
            return ContainerManager.Resolve<T>("", CurrentScope.Value);
        }

        public static T Resolve<T>(ILifetimeScope scope)
        {
            return ContainerManager.Resolve<T>("", scope);
        }

        public static T Resolve<T>(Dictionary<string, object> parameters)
        {
            var listParameters = new List<Parameter>();

            foreach (var item in parameters)
            {
                listParameters.Add(new NamedParameter(item.Key, item.Value));
            }

            return ContainerManager.Container.Resolve<T>(listParameters);
        }

        public static object Resolve(Type type)
        {
            return ContainerManager.Resolve(type);
        }

        public static bool TryResolve<T>(out T instance) where T : class
        {
            return ContainerManager.Container.TryResolve(out instance);
        }

        public static bool TryResolve(Type type, out object instance)
        {
            return ContainerManager.Container.TryResolve(type, out instance);
        }

        public static object Resolve(Type type, params Type[] typeArguments)
        {
            return ContainerManager.Resolve(type.MakeGenericType(typeArguments));
        }

        public static bool TryResolve(Type type, out object instance, params Type[] typeArguments)
        {
            try
            {
                instance = ContainerManager.Resolve(type.MakeGenericType(typeArguments));

                return true;
            }
            catch (Exception)
            {
                instance = null;

                return false;
            }
        }

        public static T[] ResolveAll<T>()
        {
            return ContainerManager.ResolveAll<T>();
        }

        public static string Get(string key)
        {
            if (_localizationProvider == null) return key;

            return _localizationProvider.Get(key);
        }

        public static string GetByValue(string value)
        {
            if (_localizationProvider == null) return value;

            return _localizationProvider.GetByValue(value);
        }

        public static string Get(string key, CultureInfo culture)
        {
            if (_localizationProvider == null) return key;

            return _localizationProvider.Get(key, culture);
        }

        public static string Get(string key, params object[] args)
        {
            if (_localizationProvider == null) return key;

            return string.Format(_localizationProvider.Get(key), args);
        }

        public static string Get(string key, CultureInfo culture, params object[] args)
        {
            if (_localizationProvider == null) return key;

            return string.Format(_localizationProvider.Get(key, culture), args);
        }

        #endregion
    }
}
