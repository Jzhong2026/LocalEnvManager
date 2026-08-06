using Caliburn.Micro;

namespace LocalEnvManager.Models
{
    /// <summary>
    /// A grouping label for projects. Carries no path information — projects hold their own
    /// absolute paths so there is no implicit base path resolution.
    /// </summary>
    public sealed class ProjectCategory : PropertyChangedBase
    {
        private string _name = string.Empty;

        public string Name
        {
            get => _name;
            set => Set(ref _name, value);
        }

        public BindableCollection<ManagedProject> Projects { get; } = [];
    }
}