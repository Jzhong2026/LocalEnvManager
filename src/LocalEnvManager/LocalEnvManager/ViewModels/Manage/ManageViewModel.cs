using System.Linq;
using System.Threading.Tasks;
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
        private readonly IWindowManager _windowManager;
        private ManagedProject? _selectedProject;

        public ManageViewModel(IProjectCatalog catalog, IWindowManager windowManager)
        {
            _catalog = catalog;
            _windowManager = windowManager;
            _catalog.Projects.CollectionChanged += (_, _) => NotifyOfPropertyChange(nameof(HasProjects));
        }

        public new string DisplayName { get; } = "项目管理";

        public int Order { get; } = 0;

        public new bool IsActive { get; set; }

        public BindableCollection<ProjectCategory> Categories => _catalog.Categories;

        public bool HasProjects => _catalog.Projects.Count > 0;

        /// <summary>
        /// True when there is a project currently highlighted on the left
        /// list. Drives the right-pane Delete / Edit buttons' <c>IsEnabled</c>
        /// bindings in <c>ManageView.xaml</c>. Returning <c>false</c> when no
        /// project is selected prevents accidental deletes/edits.
        /// </summary>
        public bool HasSelectedProject => _selectedProject is not null;

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
                    // Buttons in the right pane bind to HasSelectedProject — make
                    // sure they re-query after the selection changes.
                    NotifyOfPropertyChange(nameof(HasSelectedProject));
                }
            }
        }

        /// <summary>
        /// Wire-up target for the per-row click in <c>ManageView.xaml</c>
        /// (<c>[Action SelectProject($dataContext)]</c>). Mirrors the exact
        /// pattern used by <see cref="ExecuteViewModel.SelectProject"/> —
        /// without this method, Caliburn.Micro can't resolve the action and
        /// throws <c>ActionNotFoundException</c>, which crashes the app on
        /// the user's first project switch.
        /// </summary>
        public void SelectProject(ManagedProject project) => SelectedProject = project;

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
            SelectedProject = project;
        }

        /// <summary>
        /// Opens the selected project with an explicit view-model.  This keeps the
        /// button action resolvable instead of letting Caliburn attempt to invoke
        /// a missing action at click time.
        /// </summary>
        public async Task EditProject()
        {
            var project = SelectedProject;
            if (project is null) return;

            var dependencies = _catalog.Projects
                .Where(candidate => !ReferenceEquals(candidate, project))
                .ToList();
            await _windowManager.ShowDialogAsync(
                new EditProjectViewModel(project, dependencies, _windowManager));
        }

        /// <summary>
        /// Removes the selected project.  Resolving its category here avoids the
        /// nested category-row binding context that previously made the right-pane
        /// command pass the wrong arguments.
        /// </summary>
        public void RemoveSelectedProject()
        {
            var project = SelectedProject;
            if (project is null) return;

            var category = _catalog.Categories
                .FirstOrDefault(candidate => candidate.Projects.Contains(project));
            if (category is null) return;

            category.Projects.Remove(project);
            if (ReferenceEquals(SelectedProject, project))
            {
                SelectedProject = _catalog.Projects.FirstOrDefault();
            }
        }
    }
}
