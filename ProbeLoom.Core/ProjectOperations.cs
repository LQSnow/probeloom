namespace ProbeLoom.Core;

public static class ProjectOperations
{
    public static ProjectDocument CreateProject(string name)
    {
        var normalizedName = NormalizeRequiredName(name, "项目名称");
        var environment = new ProjectEnvironment
        {
            Name = "本地",
            BaseUrl = "http://localhost:5080"
        };
        var document = new ProjectDocument
        {
            Name = normalizedName,
            Environments = [environment],
            RouteParts =
            [
                new RoutePart { Name = "API Prefix", Value = "/api", IsEnabled = false },
                new RoutePart { Name = "API Version", Value = "/v1", IsEnabled = false }
            ],
            SelectedEnvironmentId = environment.Id
        };
        document.AttachTracking();
        document.MarkDirty();
        return document;
    }

    public static OperationResult<string> RenameProject(ProjectDocument project, string name)
    {
        var validation = ValidateName(name, "项目名称");
        if (validation is not null)
        {
            return OperationResult<string>.Failure(validation);
        }

        project.Name = name.Trim();
        return OperationResult<string>.Success(project.Name);
    }

    public static OperationResult<ProjectEnvironment> AddEnvironment(
        ProjectDocument project,
        string name,
        string baseUrl)
    {
        var error = ValidateUniqueName(project.Environments.Select(item => item.Name), name, "Environment");
        if (error is not null)
        {
            return OperationResult<ProjectEnvironment>.Failure(error);
        }

        var environment = new ProjectEnvironment { Name = name.Trim(), BaseUrl = baseUrl.Trim() };
        project.Environments.Add(environment);
        project.SelectedEnvironmentId = environment.Id;
        return OperationResult<ProjectEnvironment>.Success(environment);
    }

    public static OperationResult<ProjectEnvironment> UpdateEnvironment(
        ProjectDocument project,
        Guid environmentId,
        string name,
        string baseUrl)
    {
        var environment = project.Environments.FirstOrDefault(item => item.Id == environmentId);
        if (environment is null)
        {
            return OperationResult<ProjectEnvironment>.Failure("找不到要修改的 Environment。");
        }

        var error = ValidateUniqueName(
            project.Environments.Where(item => item.Id != environmentId).Select(item => item.Name),
            name,
            "Environment");
        if (error is not null)
        {
            return OperationResult<ProjectEnvironment>.Failure(error);
        }

        environment.Name = name.Trim();
        environment.BaseUrl = baseUrl.Trim();
        return OperationResult<ProjectEnvironment>.Success(environment);
    }

    public static OperationResult<Guid?> DeleteEnvironment(ProjectDocument project, Guid environmentId)
    {
        var environment = project.Environments.FirstOrDefault(item => item.Id == environmentId);
        if (environment is null)
        {
            return OperationResult<Guid?>.Failure("找不到要删除的 Environment。");
        }

        project.Environments.Remove(environment);
        var nextId = project.Environments.FirstOrDefault()?.Id;
        if (project.SelectedEnvironmentId == environmentId)
        {
            project.SelectedEnvironmentId = nextId;
        }

        return OperationResult<Guid?>.Success(nextId);
    }

    public static OperationResult<ProjectNode> AddGroup(
        ProjectDocument project,
        Guid? parentGroupId,
        string name)
    {
        var siblingsResult = GetGroupChildren(project, parentGroupId);
        if (!siblingsResult.Succeeded || siblingsResult.Value is null)
        {
            return OperationResult<ProjectNode>.Failure(siblingsResult.Error!);
        }

        var error = ValidateUniqueName(siblingsResult.Value.Select(item => item.Name), name, "分组");
        if (error is not null)
        {
            return OperationResult<ProjectNode>.Failure(error);
        }

        var group = new ProjectNode { Kind = ProjectNodeKind.Group, Name = name.Trim() };
        siblingsResult.Value.Add(group);
        project.SelectedNodeId = group.Id;
        return OperationResult<ProjectNode>.Success(group);
    }

    public static OperationResult<ProjectNode> AddEndpoint(
        ProjectDocument project,
        Guid groupId,
        string name)
    {
        var group = FindNode(project, groupId);
        if (group?.Kind != ProjectNodeKind.Group)
        {
            return OperationResult<ProjectNode>.Failure("Endpoint 必须创建在分组中。");
        }

        var error = ValidateUniqueName(group.Children.Select(item => item.Name), name, "Endpoint");
        if (error is not null)
        {
            return OperationResult<ProjectNode>.Failure(error);
        }

        var endpoint = new ProjectNode
        {
            Kind = ProjectNodeKind.Endpoint,
            Name = name.Trim(),
            Request = CreateDefaultRequest()
        };
        group.Children.Add(endpoint);
        project.SelectedNodeId = endpoint.Id;
        return OperationResult<ProjectNode>.Success(endpoint);
    }

    public static OperationResult<ProjectNode> AddRequestCase(
        ProjectDocument project,
        Guid endpointId,
        string name,
        RequestDefinition? source = null)
    {
        var endpoint = FindNode(project, endpointId);
        if (endpoint?.Kind != ProjectNodeKind.Endpoint)
        {
            return OperationResult<ProjectNode>.Failure("Request Case 必须创建在 Endpoint 中。");
        }

        var error = ValidateUniqueName(endpoint.Children.Select(item => item.Name), name, "Request Case");
        if (error is not null)
        {
            return OperationResult<ProjectNode>.Failure(error);
        }

        var requestCase = new ProjectNode
        {
            Kind = ProjectNodeKind.RequestCase,
            Name = name.Trim(),
            Request = source?.Clone() ?? endpoint.Request?.Clone() ?? CreateDefaultRequest()
        };
        endpoint.Children.Add(requestCase);
        project.SelectedNodeId = requestCase.Id;
        return OperationResult<ProjectNode>.Success(requestCase);
    }

    public static OperationResult<ProjectNode> RenameNode(
        ProjectDocument project,
        Guid nodeId,
        string name)
    {
        var node = FindNode(project, nodeId);
        if (node is null)
        {
            return OperationResult<ProjectNode>.Failure("找不到要重命名的对象。");
        }

        var siblings = FindParent(project, nodeId)?.Children ?? project.Items;
        var error = ValidateUniqueName(
            siblings.Where(item => item.Id != nodeId).Select(item => item.Name),
            name,
            KindDisplayName(node.Kind));
        if (error is not null)
        {
            return OperationResult<ProjectNode>.Failure(error);
        }

        node.Name = name.Trim();
        return OperationResult<ProjectNode>.Success(node);
    }

    public static OperationResult<NodeDeleteResult> DeleteNode(ProjectDocument project, Guid nodeId)
    {
        var node = FindNode(project, nodeId);
        if (node is null)
        {
            return OperationResult<NodeDeleteResult>.Failure("找不到要删除的对象。");
        }

        var parent = FindParent(project, nodeId);
        var siblings = parent?.Children ?? project.Items;
        var removedIndex = siblings.IndexOf(node);
        siblings.Remove(node);

        var next = removedIndex < siblings.Count
            ? siblings[removedIndex]
            : siblings.LastOrDefault() ?? parent;

        if (project.SelectedNodeId is Guid selectedId && ContainsNode(node, selectedId))
        {
            project.SelectedNodeId = next?.Id;
        }

        if (project.RefreshRequestNodeId is Guid refreshId && ContainsNode(node, refreshId))
        {
            project.RefreshRequestNodeId = null;
        }

        return OperationResult<NodeDeleteResult>.Success(new NodeDeleteResult(nodeId, next?.Id));
    }

    public static ProjectNode? FindNode(ProjectDocument project, Guid nodeId) =>
        EnumerateNodes(project.Items).FirstOrDefault(node => node.Id == nodeId);

    public static ProjectNode? FindParent(ProjectDocument project, Guid nodeId) =>
        FindParent(project.Items, nodeId);

    public static IReadOnlyList<ProjectNode> GetAncestors(ProjectDocument project, Guid nodeId)
    {
        var ancestors = new List<ProjectNode>();
        FindAncestors(project.Items, nodeId, ancestors);
        return ancestors;
    }

    public static IEnumerable<ProjectNode> EnumerateNodes(IEnumerable<ProjectNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in EnumerateNodes(node.Children))
            {
                yield return child;
            }
        }
    }

    public static RequestDefinition CreateDefaultRequest() =>
        new()
        {
            Method = "GET",
            Route = "/",
            Headers = [new RequestField("Accept", "application/json")]
        };

    private static OperationResult<System.Collections.ObjectModel.ObservableCollection<ProjectNode>> GetGroupChildren(
        ProjectDocument project,
        Guid? parentGroupId)
    {
        if (parentGroupId is null)
        {
            return OperationResult<System.Collections.ObjectModel.ObservableCollection<ProjectNode>>.Success(project.Items);
        }

        var parent = FindNode(project, parentGroupId.Value);
        return parent?.Kind == ProjectNodeKind.Group
            ? OperationResult<System.Collections.ObjectModel.ObservableCollection<ProjectNode>>.Success(parent.Children)
            : OperationResult<System.Collections.ObjectModel.ObservableCollection<ProjectNode>>.Failure("分组只能嵌套在项目或其他分组中。");
    }

    private static ProjectNode? FindParent(IEnumerable<ProjectNode> nodes, Guid nodeId)
    {
        foreach (var node in nodes)
        {
            if (node.Children.Any(child => child.Id == nodeId))
            {
                return node;
            }

            var parent = FindParent(node.Children, nodeId);
            if (parent is not null)
            {
                return parent;
            }
        }

        return null;
    }

    private static bool FindAncestors(
        IEnumerable<ProjectNode> nodes,
        Guid nodeId,
        IList<ProjectNode> ancestors)
    {
        foreach (var node in nodes)
        {
            if (node.Id == nodeId)
            {
                return true;
            }

            ancestors.Add(node);
            if (FindAncestors(node.Children, nodeId, ancestors))
            {
                return true;
            }

            ancestors.RemoveAt(ancestors.Count - 1);
        }

        return false;
    }

    private static bool ContainsNode(ProjectNode root, Guid nodeId) =>
        root.Id == nodeId || EnumerateNodes(root.Children).Any(node => node.Id == nodeId);

    private static string? ValidateUniqueName(IEnumerable<string> existingNames, string name, string kind)
    {
        var validation = ValidateName(name, $"{kind} 名称");
        if (validation is not null)
        {
            return validation;
        }

        return existingNames.Any(existing => string.Equals(existing, name.Trim(), StringComparison.OrdinalIgnoreCase))
            ? $"同一层级中已存在名为“{name.Trim()}”的 {kind}。"
            : null;
    }

    private static string? ValidateName(string name, string label) =>
        string.IsNullOrWhiteSpace(name) ? $"{label}不能为空。" : null;

    private static string NormalizeRequiredName(string name, string label) =>
        ValidateName(name, label) is { } error ? throw new ArgumentException(error, nameof(name)) : name.Trim();

    private static string KindDisplayName(ProjectNodeKind kind) => kind switch
    {
        ProjectNodeKind.Group => "分组",
        ProjectNodeKind.Endpoint => "Endpoint",
        ProjectNodeKind.RequestCase => "Request Case",
        _ => "对象"
    };
}
