using System.Text.Json;

namespace ProbeLoom.Core;

public sealed record ProjectTransition(
    ProjectDocument? PreviousProject,
    ProjectDocument? CurrentProject);

public sealed record ProjectTransitionResult(
    ProjectTransition Transition,
    string? Warning = null);

public sealed record ProjectRestoreResult(
    ProjectTransition Transition,
    string? Error = null,
    string? Warning = null);

public sealed record ProjectSaveResult(
    string FilePath,
    string? Warning = null);

public sealed class ProjectLifecycleService
{
    private readonly ProjectFileStore _fileStore;
    private readonly string _sessionFilePath;

    public ProjectLifecycleService()
        : this(
            new ProjectFileStore(),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ProbeLoom",
                "session.json"))
    {
    }

    public ProjectLifecycleService(ProjectFileStore fileStore, string sessionFilePath)
    {
        ArgumentNullException.ThrowIfNull(fileStore);
        if (string.IsNullOrWhiteSpace(sessionFilePath))
        {
            throw new ArgumentException("Session file path is required.", nameof(sessionFilePath));
        }

        _fileStore = fileStore;
        _sessionFilePath = Path.GetFullPath(sessionFilePath);
    }

    public ProjectDocument? Project { get; private set; }

    public string? ProjectFilePath { get; private set; }

    public async Task<ProjectRestoreResult> RestoreLastProjectAsync(
        CancellationToken cancellationToken = default)
    {
        var previousProject = Project;
        try
        {
            if (!File.Exists(_sessionFilePath))
            {
                return new ProjectRestoreResult(new ProjectTransition(previousProject, Project));
            }

            var json = await File.ReadAllTextAsync(_sessionFilePath, cancellationToken);
            var state = JsonSerializer.Deserialize<SessionState>(json);
            if (string.IsNullOrWhiteSpace(state?.LastProjectPath) ||
                !File.Exists(state.LastProjectPath))
            {
                var warning = await ClearRememberedProjectBestEffortAsync(cancellationToken);
                return new ProjectRestoreResult(
                    new ProjectTransition(previousProject, Project),
                    Warning: warning);
            }

            var loadedProject = await _fileStore.LoadAsync(state.LastProjectPath, cancellationToken);
            Project = loadedProject;
            ProjectFilePath = Path.GetFullPath(state.LastProjectPath);
            return new ProjectRestoreResult(new ProjectTransition(previousProject, Project));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                              JsonException or ProjectFileException)
        {
            var warning = await ClearRememberedProjectBestEffortAsync(cancellationToken);
            return new ProjectRestoreResult(
                new ProjectTransition(previousProject, Project),
                $"无法恢复上次打开的项目：{exception.Message}",
                warning);
        }
    }

    public async Task<ProjectTransitionResult> CreateProjectAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var project = ProjectOperations.CreateProject(name);
        var warning = await ClearRememberedProjectBestEffortAsync(cancellationToken);
        var previousProject = Project;
        Project = project;
        ProjectFilePath = null;
        return new ProjectTransitionResult(
            new ProjectTransition(previousProject, Project),
            warning);
    }

    public async Task<ProjectTransitionResult> OpenProjectAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var loadedProject = await _fileStore.LoadAsync(path, cancellationToken);
        var fullPath = Path.GetFullPath(path);

        try
        {
            await RememberProjectAsync(fullPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ProjectFileException(
                $"项目文件有效，但无法记录最近项目：{exception.Message}",
                exception);
        }

        var previousProject = Project;
        Project = loadedProject;
        ProjectFilePath = fullPath;
        return new ProjectTransitionResult(new ProjectTransition(previousProject, Project));
    }

    public async Task<ProjectSaveResult> SaveProjectAsync(
        string? path = null,
        CancellationToken cancellationToken = default)
    {
        if (Project is null)
        {
            throw new ProjectFileException("当前没有可保存的项目。");
        }

        var targetPath = path ?? ProjectFilePath;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ProjectFileException("请先选择项目文件的保存位置。");
        }

        var fullPath = Path.GetFullPath(targetPath);
        await _fileStore.SaveAsync(fullPath, Project, cancellationToken);
        ProjectFilePath = fullPath;

        string? warning = null;
        try
        {
            await RememberProjectAsync(fullPath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warning = $"项目已经保存，但无法记录为最近项目：{exception.Message}";
        }

        return new ProjectSaveResult(fullPath, warning);
    }

    public async Task<ProjectTransitionResult> DeleteCurrentProjectAsync(
        CancellationToken cancellationToken = default)
    {
        if (Project is null)
        {
            return new ProjectTransitionResult(new ProjectTransition(null, null));
        }

        var previousProject = Project;
        var savedPath = ProjectFilePath;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(savedPath) && File.Exists(savedPath))
            {
                File.Delete(savedPath);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ProjectFileException($"无法删除项目文件：{exception.Message}", exception);
        }

        var warning = await ClearRememberedProjectBestEffortAsync(cancellationToken);
        Project = null;
        ProjectFilePath = null;
        return new ProjectTransitionResult(
            new ProjectTransition(previousProject, null),
            warning);
    }

    private async Task RememberProjectAsync(
        string projectFilePath,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_sessionFilePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_sessionFilePath}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(
            new SessionState { LastProjectPath = projectFilePath },
            new JsonSerializerOptions { WriteIndented = true });

        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            File.Move(temporaryPath, _sessionFilePath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task<string?> ClearRememberedProjectBestEffortAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_sessionFilePath))
            {
                await Task.Run(() => File.Delete(_sessionFilePath), cancellationToken);
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"无法清除最近项目记录：{exception.Message}";
        }
    }

    private sealed class SessionState
    {
        public string? LastProjectPath { get; set; }
    }
}
