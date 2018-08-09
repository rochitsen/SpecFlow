using System;
using System.Collections.Generic;
using BoDi;
using System.Linq;
using System.Reflection;
using TechTalk.SpecFlow.Configuration;
using TechTalk.SpecFlow.Plugins;
using TechTalk.SpecFlow.Tracing;
using TechTalk.SpecFlow.UnitTestProvider;

namespace TechTalk.SpecFlow.Infrastructure
{
    public interface IContainerBuilder
    {
        IObjectContainer CreateGlobalContainer(IRuntimeConfigurationProvider configurationProvider = null);
        IObjectContainer CreateTestThreadContainer(IObjectContainer globalContainer);
        IObjectContainer CreateScenarioContainer(IObjectContainer testThreadContainer, ScenarioInfo scenarioInfo);
        IObjectContainer CreateFeatureContainer(IObjectContainer testThreadContainer, FeatureInfo featureInfo);
    }

    public class ContainerBuilder : IContainerBuilder
    {
        public static IDefaultDependencyProvider DefaultDependencyProvider = new DefaultDependencyProvider();

        private readonly IDefaultDependencyProvider defaultDependencyProvider;

        public ContainerBuilder(IDefaultDependencyProvider defaultDependencyProvider = null)
        {
            this.defaultDependencyProvider = defaultDependencyProvider ?? DefaultDependencyProvider;
        }

        public virtual IObjectContainer CreateGlobalContainer(IRuntimeConfigurationProvider configurationProvider = null)
        {
            var container = new ObjectContainer();
            container.RegisterInstanceAs<IContainerBuilder>(this);

            RegisterDefaults(container);

            if (configurationProvider != null)
                container.RegisterInstanceAs(configurationProvider);

            configurationProvider = configurationProvider ?? container.Resolve<IRuntimeConfigurationProvider>();

            container.RegisterTypeAs<RuntimePluginEvents, RuntimePluginEvents>(); //NOTE: we need this unnecessary registration, due to a bug in BoDi (does not inherit non-registered objects)
            var runtimePluginEvents = container.Resolve<RuntimePluginEvents>();

            SpecFlowConfiguration specFlowConfiguration = ConfigurationLoader.GetDefault();
            specFlowConfiguration = configurationProvider.LoadConfiguration(specFlowConfiguration);

            if (specFlowConfiguration.CustomDependencies != null)
                container.RegisterFromConfiguration(specFlowConfiguration.CustomDependencies);

            var unitTestProviderConfigration = container.Resolve<UnitTestProviderConfiguration>();

            LoadPlugins(configurationProvider, container, runtimePluginEvents, specFlowConfiguration, unitTestProviderConfigration);

            runtimePluginEvents.RaiseConfigurationDefaults(specFlowConfiguration);

            runtimePluginEvents.RaiseRegisterGlobalDependencies(container);
            
            container.RegisterInstanceAs(specFlowConfiguration);
            
            if (unitTestProviderConfigration != null)
                container.RegisterInstanceAs(container.Resolve<IUnitTestRuntimeProvider>(unitTestProviderConfigration.UnitTestProvider ?? ConfigDefaults.UnitTestProviderName));

            runtimePluginEvents.RaiseCustomizeGlobalDependencies(container, specFlowConfiguration);

            container.Resolve<IConfigurationLoader>().TraceConfigSource(container.Resolve<ITraceListener>(), specFlowConfiguration);

            return container;
        }

        public virtual IObjectContainer CreateTestThreadContainer(IObjectContainer globalContainer)
        {
            var testThreadContainer = new ObjectContainer(globalContainer);

            defaultDependencyProvider.RegisterTestThreadContainerDefaults(testThreadContainer);

            var runtimePluginEvents = globalContainer.Resolve<RuntimePluginEvents>();
            runtimePluginEvents.RaiseCustomizeTestThreadDependencies(testThreadContainer);
            testThreadContainer.Resolve<ITestObjectResolver>();
            return testThreadContainer;
        }

        public virtual IObjectContainer CreateScenarioContainer(IObjectContainer testThreadContainer, ScenarioInfo scenarioInfo)
        {
            if (testThreadContainer == null)
                throw new ArgumentNullException(nameof(testThreadContainer));

            var scenarioContainer = new ObjectContainer(testThreadContainer);
            scenarioContainer.RegisterInstanceAs(scenarioInfo);
            defaultDependencyProvider.RegisterScenarioContainerDefaults(scenarioContainer);

            scenarioContainer.ObjectCreated += obj =>
            {
                var containerDependentObject = obj as IContainerDependentObject;
                if (containerDependentObject != null)
                    containerDependentObject.SetObjectContainer(scenarioContainer);
            };

            var runtimePluginEvents = testThreadContainer.Resolve<RuntimePluginEvents>();
            runtimePluginEvents.RaiseCustomizeScenarioDependencies(scenarioContainer);

            return scenarioContainer;
        }

        public IObjectContainer CreateFeatureContainer(IObjectContainer testThreadContainer, FeatureInfo featureInfo)
        {
            if (testThreadContainer == null)
                throw new ArgumentNullException(nameof(testThreadContainer));

            var featureContainer = new ObjectContainer(testThreadContainer);
            featureContainer.RegisterInstanceAs(featureInfo);

            return featureContainer;
        }

        protected virtual void LoadPlugins(IRuntimeConfigurationProvider configurationProvider, ObjectContainer container, RuntimePluginEvents runtimePluginEvents,
            SpecFlowConfiguration specFlowConfiguration, UnitTestProviderConfiguration unitTestProviderConfigration)
        {
            // initialize plugins that were registered from code
            foreach (var runtimePlugin in container.Resolve<IDictionary<string, IRuntimePlugin>>().Values)
            {
                // these plugins cannot have parameters
                runtimePlugin.Initialize(runtimePluginEvents, new RuntimePluginParameters(), unitTestProviderConfigration);
            }

            // load & initalize parameters from configuration
            var pluginLocator = container.Resolve<IRuntimePluginLocator>();
            var pluginLoader = container.Resolve<IRuntimePluginLoader>();
            var traceListener = container.Resolve<ITraceListener>();
            foreach (var pluginAssemblyName in pluginLocator.GetAllRuntimePlugins())
            {
                LoadPlugin(pluginAssemblyName, pluginLoader, runtimePluginEvents, unitTestProviderConfigration, traceListener);
            }
        }

        protected virtual void LoadPlugin(string pluginDescriptor, IRuntimePluginLoader pluginLoader, RuntimePluginEvents runtimePluginEvents,
            UnitTestProviderConfiguration unitTestProviderConfigration, ITraceListener traceListener)
        {
            var plugin = pluginLoader.LoadPlugin(pluginDescriptor, traceListener);
            var runtimePluginParameters = new RuntimePluginParameters();

            plugin?.Initialize(runtimePluginEvents, runtimePluginParameters, unitTestProviderConfigration);
        }

        protected virtual void RegisterDefaults(ObjectContainer container)
        {
            defaultDependencyProvider.RegisterGlobalContainerDefaults(container);
        }
    }
}