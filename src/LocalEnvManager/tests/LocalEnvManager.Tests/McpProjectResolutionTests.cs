using System.Collections.Generic;
using System.Linq;
using Caliburn.Micro;
using LocalEnvManager.Mcp.Contracts;
using LocalEnvManager.Models;
using LocalEnvManager.Services;
using LocalEnvManager.Services.Mcp;
using Xunit;

namespace LocalEnvManager.Tests;

public sealed class McpProjectResolutionTests
{
    [Fact]
    public void NumericNameResolvesToNamedProjectNotIdCollision()
    {
        // A project literally named "2" (runtime id 1) must not be shadowed by
        // another project whose runtime id happens to be 2.
        var named = new ManagedProject { Name = "2" };
        var other = new ManagedProject { Name = "Beta" };
        var catalog = new FakeCatalog(named, other);

        var resolved = McpCommandServer.ResolveProject(catalog, new ProjectRef("2", null));

        Assert.Same(named, resolved);
    }

    [Fact]
    public void NumericNameFallsBackToIdWhenNoNameMatch()
    {
        var alpha = new ManagedProject { Name = "Alpha" };
        var beta = new ManagedProject { Name = "Beta" };
        var catalog = new FakeCatalog(alpha, beta); // beta holds runtime id 2

        var resolved = McpCommandServer.ResolveProject(catalog, new ProjectRef("2", null));

        Assert.Same(beta, resolved);
    }

    [Fact]
    public void ExplicitIdTakesPrecedenceOverName()
    {
        var alpha = new ManagedProject { Name = "Alpha" };
        var beta = new ManagedProject { Name = "Beta" };
        var catalog = new FakeCatalog(alpha, beta);

        var resolved = McpCommandServer.ResolveProject(catalog, new ProjectRef("Alpha", 2));

        Assert.Same(beta, resolved);
    }

    [Fact]
    public void NameMatchIsCaseInsensitive()
    {
        var alpha = new ManagedProject { Name = "Alpha" };
        var catalog = new FakeCatalog(alpha);

        var resolved = McpCommandServer.ResolveProject(catalog, new ProjectRef("alpha", null));

        Assert.Same(alpha, resolved);
    }

    [Fact]
    public void UnknownReferenceResolvesToNull()
    {
        var alpha = new ManagedProject { Name = "Alpha" };
        var catalog = new FakeCatalog(alpha);

        Assert.Null(McpCommandServer.ResolveProject(catalog, new ProjectRef("Missing", null)));
    }

    private sealed class FakeCatalog : IProjectCatalog
    {
        private readonly Dictionary<ManagedProject, int> _ids = new();

        public FakeCatalog(params ManagedProject[] projects)
        {
            Categories = new BindableCollection<ProjectCategory>();
            Projects = new BindableCollection<ManagedProject>(projects);
            var id = 1;
            foreach (var project in Projects) _ids[project] = id++;
        }

        public BindableCollection<ProjectCategory> Categories { get; }
        public BindableCollection<ManagedProject> Projects { get; }

        public int GetProjectId(ManagedProject? project)
            => project is not null && _ids.TryGetValue(project, out var id) ? id : 0;

        public ManagedProject? FindById(int projectId)
            => _ids.FirstOrDefault(pair => pair.Value == projectId).Key;

        public void FlushPendingSave() { }
    }
}