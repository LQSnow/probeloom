using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProbeLoom.Core;

public sealed class ProjectFileException : Exception
{
    public ProjectFileException(string message)
        : base(message)
    {
    }

    public ProjectFileException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class ProjectFileStore
{
    public const string FileExtension = ".probeloom.json";
    private const string FormatName = "ProbeLoom.Project";
    private const int CurrentVersion = 4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task SaveAsync(string path, ProjectDocument project, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ProjectFileException("未提供项目文件路径。");
        }

        ValidateDocument(project);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ProjectFileException("项目文件路径无效。");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        var envelope = new ProjectFileEnvelope
        {
            Format = FormatName,
            Version = CurrentVersion,
            Project = project
        };

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, envelope, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, fullPath, true);
            project.MarkSaved();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ProjectFileException($"无法保存项目文件：{exception.Message}", exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<ProjectDocument> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            throw new ProjectFileException("项目文件不存在。");
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous);
            var envelope = await JsonSerializer.DeserializeAsync<ProjectFileEnvelope>(stream, JsonOptions, cancellationToken);

            if (envelope is null || envelope.Format != FormatName)
            {
                throw new ProjectFileException("这不是有效的 ProbeLoom 项目文件。");
            }

            if (envelope.Version is < 1 or > CurrentVersion)
            {
                throw new ProjectFileException(
                    $"不支持项目文件版本 {envelope.Version}；当前支持版本为 1–{CurrentVersion}。");
            }

            if (envelope.Project is null || string.IsNullOrWhiteSpace(envelope.Project.Name))
            {
                throw new ProjectFileException("项目文件缺少必要的项目信息。");
            }

            Migrate(envelope);
            ValidateDocument(envelope.Project);
            envelope.Project.AttachTracking();
            envelope.Project.MarkSaved();
            return envelope.Project;
        }
        catch (ProjectFileException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new ProjectFileException($"无法打开项目文件：{exception.Message}", exception);
        }
    }

    private static void ValidateDocument(ProjectDocument project)
    {
        var ids = new HashSet<Guid>();
        if (project.Id == Guid.Empty)
        {
            throw new ProjectFileException("项目 ID 无效。");
        }
        ids.Add(project.Id);

        foreach (var environment in project.Environments)
        {
            if (environment.Id == Guid.Empty || !ids.Add(environment.Id) || string.IsNullOrWhiteSpace(environment.Name))
            {
                throw new ProjectFileException("项目包含无效或重复的 Environment。");
            }
        }

        foreach (var routePart in project.RouteParts)
        {
            if (routePart.Id == Guid.Empty || !ids.Add(routePart.Id) || string.IsNullOrWhiteSpace(routePart.Name))
            {
                throw new ProjectFileException("项目包含无效或重复的 Route Part。");
            }
        }

        ValidateVariables(project.Variables, "Project", ids);
        foreach (var environment in project.Environments)
        {
            ValidateVariables(environment.Variables, $"Environment“{environment.Name}”", ids);
        }

        foreach (var node in ProjectOperations.EnumerateNodes(project.Items))
        {
            if (node.Id == Guid.Empty || !ids.Add(node.Id) || string.IsNullOrWhiteSpace(node.Name))
            {
                throw new ProjectFileException("项目包含无效或重复的工作区对象。");
            }

            var requestRequired = node.Kind is ProjectNodeKind.Endpoint or ProjectNodeKind.RequestCase;
            if (requestRequired != (node.Request is not null))
            {
                throw new ProjectFileException($"“{node.Name}”的数据类型与请求内容不一致。");
            }

            if (node.Kind == ProjectNodeKind.RequestCase && node.Children.Count > 0)
            {
                throw new ProjectFileException("Request Case 不能包含子对象。");
            }

            if (node.Kind != ProjectNodeKind.Group &&
                (node.IsRoutePrefixEnabled || !string.IsNullOrWhiteSpace(node.RoutePrefix)))
            {
                throw new ProjectFileException("只有 Group 可以配置 Route Prefix。");
            }

            ValidateVariables(node.Variables, $"“{node.Name}”", ids);
            if (node.Request is not null)
            {
                ValidateAuthenticationSecrets(node.Request.Authentication, node.Name);
            }
        }

        if (project.Environments
            .GroupBy(environment => environment.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
        {
            throw new ProjectFileException("项目包含重名的 Environment。");
        }

        ValidateHierarchy(project.Items, expectedParent: null);

        if (project.RefreshRequestNodeId is Guid refreshId &&
            ProjectOperations.FindNode(project, refreshId) is not { Request: not null })
        {
            throw new ProjectFileException("Refresh 请求引用无效。");
        }

        if (project.SelectedEnvironmentId is Guid environmentId &&
            project.Environments.All(environment => environment.Id != environmentId))
        {
            project.SelectedEnvironmentId = project.Environments.FirstOrDefault()?.Id;
        }

        if (project.SelectedNodeId is Guid nodeId && ProjectOperations.FindNode(project, nodeId) is null)
        {
            project.SelectedNodeId = null;
        }
    }

    private static void Migrate(ProjectFileEnvelope envelope)
    {
        if (envelope.Project is null || envelope.Version >= CurrentVersion)
        {
            return;
        }

        // V1-V3 do not contain documentation metadata. Empty defaults preserve
        // the exact previous request behavior and do not introduce credentials.
        envelope.Project.RouteParts ??= [];
        envelope.Project.Variables ??= [];
        foreach (var environment in envelope.Project.Environments)
        {
            environment.Variables ??= [];
        }
        foreach (var node in ProjectOperations.EnumerateNodes(envelope.Project.Items))
        {
            node.Variables ??= [];
            if (node.Request is not null)
            {
                node.Request.PathParameters ??= [];
                node.Request.Authentication ??= new AuthenticationConfiguration();
                node.Request.TokenCapture ??= new TokenCaptureConfiguration();
            }
        }

        envelope.Version = CurrentVersion;
    }

    private static void ValidateVariables(
        IEnumerable<VariableDefinition> variables,
        string scopeName,
        ISet<Guid> ids)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in variables)
        {
            if (variable.Id == Guid.Empty || !ids.Add(variable.Id))
            {
                throw new ProjectFileException($"{scopeName} 包含无效或重复的变量 ID。");
            }

            var name = variable.Name.Trim();
            if (string.IsNullOrWhiteSpace(name) ||
                !System.Text.RegularExpressions.Regex.IsMatch(
                    name,
                    @"^[A-Za-z_][A-Za-z0-9_.-]*$") ||
                !names.Add(name))
            {
                throw new ProjectFileException($"{scopeName} 包含无效、未命名或重名变量。");
            }

            if (name.StartsWith("token.", StringComparison.OrdinalIgnoreCase))
            {
                throw new ProjectFileException($"{scopeName} 使用了保留的 token.* 变量名。");
            }

            if (variable.IsSecret && !string.IsNullOrEmpty(variable.Value))
            {
                throw new ProjectFileException(
                    $"Secret 变量“{name}”不能在项目文件中保存明文值。");
            }
        }
    }

    private static void ValidateAuthenticationSecrets(
        AuthenticationConfiguration authentication,
        string requestName)
    {
        static bool IsSecretTemplate(string value) =>
            System.Text.RegularExpressions.Regex.IsMatch(
                value.Trim(),
                @"^\{\{\s*[A-Za-z_][A-Za-z0-9_.-]*\s*\}\}$");

        (string Label, string Value)? sensitive = authentication.Kind switch
        {
            AuthenticationKind.BearerToken when !string.IsNullOrWhiteSpace(authentication.BearerToken) =>
                ("Bearer Token", authentication.BearerToken),
            AuthenticationKind.Basic when !string.IsNullOrEmpty(authentication.Password) =>
                ("Basic Auth Password", authentication.Password),
            AuthenticationKind.ApiKey when !string.IsNullOrEmpty(authentication.ApiKeyValue) =>
                ("API Key Value", authentication.ApiKeyValue),
            _ => null
        };
        if (sensitive is { } item && !IsSecretTemplate(item.Value))
        {
            throw new ProjectFileException(
                $"“{requestName}”的 {item.Label} 必须引用 Secret 变量，不能保存明文。");
        }
    }

    private static void ValidateHierarchy(IEnumerable<ProjectNode> nodes, ProjectNodeKind? expectedParent)
    {
        if (nodes
            .GroupBy(node => node.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Any(group => group.Count() > 1))
        {
            throw new ProjectFileException("项目在同一层级中包含重名对象。");
        }

        foreach (var node in nodes)
        {
            var allowed = expectedParent switch
            {
                null => node.Kind == ProjectNodeKind.Group,
                ProjectNodeKind.Group => node.Kind is ProjectNodeKind.Group or ProjectNodeKind.Endpoint,
                ProjectNodeKind.Endpoint => node.Kind == ProjectNodeKind.RequestCase,
                ProjectNodeKind.RequestCase => false,
                _ => false
            };
            if (!allowed)
            {
                throw new ProjectFileException($"“{node.Name}”位于不支持的工作区层级。");
            }

            ValidateHierarchy(node.Children, node.Kind);
        }
    }

    private sealed class ProjectFileEnvelope
    {
        public string Format { get; set; } = FormatName;

        public int Version { get; set; } = CurrentVersion;

        public ProjectDocument? Project { get; set; }
    }
}
