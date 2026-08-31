using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using Caliburn.Micro;
using LocalEnvManager.Services;
using LocalEnvManager.ViewModels;

namespace LocalEnvManager
{
    public sealed class AppBootstrapper : BootstrapperBase
    {
        private readonly SimpleContainer _container = new();

        public AppBootstrapper()
        {
            Initialize();
        }

        protected override void Configure()
        {
            _container.Singleton<IWindowManager, WindowManager>();
            _container.Singleton<IEventAggregator, EventAggregator>();
            _container.Singleton<IApplicationWindowService, ApplicationWindowService>();

            // Infrastructure services — all singletons so per-request view-models
            // see the same instance across the app.
            _container.Singleton<IProjectUpdateRunner, ProjectUpdateRunner>();
            _container.Singleton<IGitBranchService, GitBranchService>();
            _container.Singleton<IProjectStore, JsonProjectStore>();
            _container.Singleton<IRuntimeLogger, RuntimeLogger>();
            _container.Singleton<ILiveLogViewModelFactory, LiveLogViewModelFactory>();

            // Project catalog is the single source of truth for project data shared
            // across all three page view-models. Constructed eagerly at startup so
            // its seed data and disk load happen before any page binds to it.
            _container.Singleton<IProjectCatalog, ProjectCatalog>();

            // Per-request view-models — pages and the shell itself.
            _container.PerRequest<ManageViewModel>();
            _container.PerRequest<ExecuteViewModel>();
            _container.PerRequest<RunLogsViewModel>();
            _container.PerRequest<ContentHostViewModel>();
            _container.PerRequest<ShellViewModel>();
        }

        protected override object GetInstance(Type service, string key)
        {
            return _container.GetInstance(service, key);
        }

        protected override IEnumerable<object> GetAllInstances(Type service)
        {
            return _container.GetAllInstances(service);
        }

        protected override void BuildUp(object instance)
        {
            _container.BuildUp(instance);
        }

        protected override async void OnStartup(object sender, StartupEventArgs e)
        {
            await DisplayRootViewForAsync<ShellViewModel>();
        }
    }
}
