using Caliburn.Micro;
using LocalEnvManager.Models;
using LocalEnvManager.Services;

namespace LocalEnvManager.ViewModels
{
    /// <summary>
    /// Owns the "项目管理" (project configuration) tab. The data itself lives
    /// in <see cref="IProjectCatalog"/> — this view-model just exposes the
    /// collections the UI binds to and the commands that mutate them. The
    /// catalog handles persistence automatically (any CollectionChanged event
    /// triggers a debounced save).
    /// </summary>
    public sealed class ManageViewModel : Screen, IPageViewModel
    {
        private readonly IProjectCatalog _catalog;
        private ManagedProject? _selectedProject;

        public ManageViewModel(IProjectCatalog catalog)
        {
            _catalog = catalog;
            _catalog.Projects.CollectionChanged += (_, _) => NotifyOfPropertyChange(nameof(HasProjects));
        }

        public new string DisplayName { get; } = "项目管理";

        public int Order { get; } = 0;

        public new bool IsActive { get; set; }

        public BindableCollection<ProjectCategory> Categories => _catalog.Categories;

        public bool HasProjects => _catalog.Projects.Count > 0;

        public ManagedProject? SelectedProject
        {
            get => _selectedProject;
            set
            {
                if (Set(ref _selectedProject, value))
                {
                    // Mirror to ManagedProject.IsSelected so the row DataTrigger fires.
                    foreach (var project in _catalog.Projects)
                    {
                        var isSelected = project == value;
                        if (project.IsSelected != isSelected)
                        {
                            project.IsSelected = isSelected;
                        }
                    }
                }
            }
        }

        public void OnActivated() { /* nothing lazy yet */ }

        public void AddCategory()
        {
            var category = new ProjectCategory { Name = "新分类" };
            _catalog.Categories.Add(category);
        }

        public void RemoveCategory(ProjectCategory category)
        {
            if (category is null) return;
            _catalog.Categories.Remove(category);
            if (SelectedProject is { } selected && !_catalog.Projects.Contains(selected))
            {
                SelectedProject = _catalog.Projects.FirstOrDefault();
            }
        }

        public void AddProject(ProjectCategory category)
        {
            if (category is null) return;
            var project = new ManagedProject
            {
                Name = "新项目",
                DeploymentMode = DeploymentMode.DotNetPublish,
                PullLatest = true
            };
            category.Projects.Add(project);
        }

        public void RemoveProject(ProjectCategory category, ManagedProject project)
        {
            if (category is null || project is null) return;
            category.Projects.Remove(project);
            if (SelectedProject == project)
            {
                SelectedProject = _catalog.Projects.FirstOrDefault();
            }
        }
    }
}