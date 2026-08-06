using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Threading;
using Caliburn.Micro;
using LocalEnvManager.Messages;
using LocalEnvManager.Models;

namespace LocalEnvManager.Services
{
    /// <summary>
    /// Single source of truth for project data shared across the three tab view-models.
    /// Injected as a singleton so each tab view-model sees the same instances of
    /// <see cref="ManagedProject"/> without holding direct references to each other.
    ///
    /// The catalog owns persistence too: any mutation to <see cref="Categories"/> (or
    /// to the per-project fields the catalog knows are persisted) is auto-saved with a
    /// 400ms debounce, and <see cref="FlushPendingSave"/> forces an immediate write
    /// at shutdown. This keeps individual view-models free of storage concerns.
    /// </summary>
    public interface IProjectCatalog
    {
        BindableCollection<ProjectCategory> Categories { get; }
        BindableCollection<ManagedProject> Projects { get; }

        /// <summary>Correlation id for use in IMessage payloads.</summary>
        int GetProjectId(ManagedProject? project);

        /// <summary>Reverse lookup — null when no project holds this id.</summary>
        ManagedProject? FindById(int projectId);

        /// <summary>Force any pending debounced save to flush to disk.</summary>
        void FlushPendingSave();
    }

    public sealed class ProjectCatalog : IProjectCatalog
    {
        private readonly IProjectStore _store;
        private readonly IRuntimeLogger _logger;
        private readonly IEventAggregator _events;
        private readonly DispatcherTimer _saveDebounceTimer;
        private readonly Dictionary<ManagedProject, int> _projectIds = new();
        private int _nextProjectId = 1;

        public ProjectCatalog(IProjectStore store, IRuntimeLogger logger, IEventAggregator events)
        {
            _store = store;
            _logger = logger;
            _events = events;

            Categories = new BindableCollection<ProjectCategory>();
            Projects = new BindableCollection<ManagedProject>();

            _saveDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _saveDebounceTimer.Tick += (_, _) =>
            {
                _saveDebounceTimer.Stop();
                SaveToStoreNow();
            };

            // Auto-save on any structural or persisted-field change. The catalog
            // subscribes itself, so callers (ManageViewModel) just mutate the
            // observable collections and persistence happens transparently.
            Categories.CollectionChanged += OnCategoriesChanged;

            Load();
        }

        public BindableCollection<ProjectCategory> Categories { get; }
        public BindableCollection<ManagedProject> Projects { get; }

        public int GetProjectId(ManagedProject? project)
        {
            if (project is null) return 0;
            if (!_projectIds.TryGetValue(project, out var id))
            {
                id = _nextProjectId++;
                _projectIds[project] = id;
            }
            return id;
        }

        public ManagedProject? FindById(int projectId)
        {
            foreach (var pair in _projectIds)
            {
                if (pair.Value == projectId) return pair.Key;
            }
            return null;
        }

        public void FlushPendingSave()
        {
            if (!_saveDebounceTimer.IsEnabled) return;
            _saveDebounceTimer.Stop();
            SaveToStoreNow();
        }

        private void OnCategoriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // A new category was added: subscribe to its inner Projects collection so
            // future additions/removals inside the category also get persisted.
            if (e.NewItems is not null)
            {
                foreach (ProjectCategory category in e.NewItems)
                {
                    category.Projects.CollectionChanged += OnCategoryProjectsChanged;
                    category.PropertyChanged += OnCategoryPropertyChanged;
                    foreach (var project in category.Projects)
                    {
                        project.PropertyChanged += OnProjectPropertyChanged;
                    }
                }
            }
            if (e.OldItems is not null)
            {
                foreach (ProjectCategory category in e.OldItems)
                {
                    category.Projects.CollectionChanged -= OnCategoryProjectsChanged;
                    category.PropertyChanged -= OnCategoryPropertyChanged;
                    foreach (var project in category.Projects)
                    {
                        project.PropertyChanged -= OnProjectPropertyChanged;
                        _projectIds.Remove(project);
                    }
                }
            }
            RebuildProjectList();
            RequestSave();
        }

        private void OnCategoryProjectsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is not null)
            {
                foreach (ManagedProject project in e.NewItems)
                {
                    project.PropertyChanged += OnProjectPropertyChanged;
                }
            }
            if (e.OldItems is not null)
            {
                foreach (ManagedProject project in e.OldItems)
                {
                    project.PropertyChanged -= OnProjectPropertyChanged;
                    _projectIds.Remove(project);
                }
            }
            RebuildProjectList();
            RequestSave();
        }

        private void OnCategoryPropertyChanged(object? sender, PropertyChangedEventArgs e) => RequestSave();

        private void OnProjectPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ManagedProject.Name)
                or nameof(ManagedProject.SourcePath)
                or nameof(ManagedProject.Command)
                or nameof(ManagedProject.DeploymentMode)
                or nameof(ManagedProject.TargetPath)
                or nameof(ManagedProject.BackupFolder)
                or nameof(ManagedProject.IisAppPoolName)
                or nameof(ManagedProject.PullLatest)
                or nameof(ManagedProject.DependsOn)
                or nameof(ManagedProject.LogsDirectory))
            {
                RequestSave();
            }
        }

        private void RebuildProjectList()
        {
            Projects.Clear();
            foreach (var p in Categories.SelectMany(c => c.Projects))
            {
                Projects.Add(p);
            }
            _events.PublishOnCurrentThread(new ProjectsChangedMessage());
        }

        private void RequestSave()
        {
            _saveDebounceTimer.Stop();
            _saveDebounceTimer.Start();
        }

        private void SaveToStoreNow()
        {
            var snapshot = new ProjectStoreSnapshot
            {
                Categories = Categories.Select(c => new CategoryRecord
                {
                    Name = c.Name,
                    Projects = c.Projects.Select(p => new ProjectRecord
                    {
                        Name = p.Name,
                        SourcePath = p.SourcePath,
                        Command = p.Command,
                        DeploymentMode = p.DeploymentMode,
                        TargetPath = p.TargetPath,
                        BackupFolder = p.BackupFolder,
                        IisAppPoolName = p.IisAppPoolName,
                        PullLatest = p.PullLatest,
                        DependsOn = p.DependsOn,
                        LogsDirectory = p.LogsDirectory
                    }).ToList()
                }).ToList()
            };
            try
            {
                _store.Save(snapshot);
            }
            catch (Exception ex)
            {
                _logger.Append(LogEntry.LogLevel.Error, $"项目配置保存失败：{ex}");
            }
        }

        private void Load()
        {
            Categories.Clear();

            ProjectStoreSnapshot snapshot;
            try
            {
                snapshot = _store.Load();
            }
            catch (Exception ex)
            {
                _logger.Append(LogEntry.LogLevel.Error, $"项目配置读取失败：{ex.Message}");
                snapshot = new ProjectStoreSnapshot();
            }

            if (snapshot.Categories.Count == 0)
            {
                foreach (var category in CreateSeedCategories())
                {
                    Categories.Add(category);
                }
                SaveToStoreNow();
            }
            else
            {
                foreach (var category in snapshot.Categories.Select(CreateCategory))
                {
                    Categories.Add(category);
                }
            }

            RebuildProjectList();
        }

        private static ProjectCategory CreateCategory(CategoryRecord record)
        {
            var category = new ProjectCategory { Name = record.Name };
            foreach (var project in record.Projects.Select(CreateProject))
                category.Projects.Add(project);
            return category;
        }

        private static ManagedProject CreateProject(ProjectRecord record)
        {
            return new ManagedProject
            {
                Name = record.Name,
                SourcePath = record.SourcePath,
                Command = record.Command,
                DeploymentMode = record.DeploymentMode,
                TargetPath = record.TargetPath,
                BackupFolder = record.BackupFolder,
                IisAppPoolName = record.IisAppPoolName ?? string.Empty,
                PullLatest = record.PullLatest,
                DependsOn = record.DependsOn ?? string.Empty,
                LogsDirectory = record.LogsDirectory ?? string.Empty
            };
        }

        private static List<ProjectCategory> CreateSeedCategories()
        {
            return
            [
                new ProjectCategory
                {
                    Name = "AI-Research",
                    Projects =
                    {
                        new ManagedProject
                        {
                            Name = "ChatWidget",
                            DeploymentMode = DeploymentMode.DotNetPublish,
                            SourcePath = @"D:\Projects\AI-Research\ChatWidget",
                            Command = "publish -c Release -o out",
                            TargetPath = @"D:\Projects\AI-Research\ChatWidget\bin\Release\publish",
                            BackupFolder = @"D:\Projects\AI-Research\ChatWidget\deploy-overlay"
                        },
                        new ManagedProject
                        {
                            Name = "LLMAgentApi",
                            DeploymentMode = DeploymentMode.PowerShellCommand,
                            SourcePath = @"D:\Projects\AI-Research\LLMAgentApi",
                            Command = "Compress-Archive -Path .\\publish -DestinationPath .\\dist\\api.zip -Force",
                            TargetPath = @"D:\Projects\AI-Research\LLMAgentApi\dist",
                            BackupFolder = @"D:\Projects\AI-Research\LLMAgentApi\deploy-overlay"
                        },
                        new ManagedProject
                        {
                            Name = "PyAgents",
                            DeploymentMode = DeploymentMode.Uvicorn,
                            SourcePath = @"D:\Projects\AI-Research\PyAgents",
                            Command = "main:app --host 127.0.0.1 --port 8000",
                            TargetPath = @"D:\Projects\AI-Research\PyAgents",
                            BackupFolder = @"D:\Projects\AI-Research\PyAgents\deploy-overlay"
                        }
                    }
                },
                new ProjectCategory { Name = "Plus" }
            ];
        }
    }
}