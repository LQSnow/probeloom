using System.Text;
using System.Text.RegularExpressions;

namespace ProbeLoom.Core;

public enum VariableScopeKind
{
    Project,
    Environment,
    Group,
    Endpoint,
    RequestCase,
    TokenSession
}

public enum VariableIssueKind
{
    InvalidName,
    Duplicate,
    Missing,
    MissingSecret,
    CircularReference,
    InvalidPlaceholder
}

public sealed record VariableSource(
    VariableScopeKind Scope,
    string ScopeName,
    Guid? DefinitionId);

public sealed record ResolvedVariable(
    string Name,
    string Value,
    bool IsSecret,
    VariableSource Source,
    IReadOnlyList<VariableSource> OverriddenSources);

public sealed record VariableResolutionIssue(
    VariableIssueKind Kind,
    string VariableName,
    string Message);

public sealed record TemplateReplacementResult(
    string Value,
    IReadOnlyList<VariableResolutionIssue> Issues);

public sealed class VariableResolutionResult(
    IReadOnlyDictionary<string, ResolvedVariable> variables,
    IReadOnlyList<VariableResolutionIssue> issues)
{
    public IReadOnlyDictionary<string, ResolvedVariable> Variables { get; } = variables;

    public IReadOnlyList<VariableResolutionIssue> Issues { get; } = issues;

    public bool Succeeded => Issues.Count == 0;

    public TemplateReplacementResult Replace(string? template, string fieldName) =>
        VariableResolver.ReplaceTemplate(template ?? string.Empty, fieldName, Variables, maskSecrets: false);

    public TemplateReplacementResult ReplaceMasked(string? template, string fieldName) =>
        VariableResolver.ReplaceTemplate(template ?? string.Empty, fieldName, Variables, maskSecrets: true);
}

public sealed record SystemVariable(string Name, string Value, bool IsSecret, string Source);

public static partial class VariableResolver
{
    public static async Task<VariableResolutionResult> ResolveAsync(
        ProjectDocument project,
        ProjectEnvironment? environment,
        ProjectNode node,
        ISecureValueStore secureValueStore,
        IEnumerable<SystemVariable>? systemVariables = null,
        CancellationToken cancellationToken = default)
    {
        var issues = new List<VariableResolutionIssue>();
        var bindings = new Dictionary<string, Binding>(StringComparer.OrdinalIgnoreCase);
        await AddScopeAsync(project.Variables, VariableScopeKind.Project, project.Name);
        if (environment is not null)
        {
            await AddScopeAsync(environment.Variables, VariableScopeKind.Environment, environment.Name);
        }

        foreach (var ancestor in ProjectOperations.GetAncestors(project, node.Id))
        {
            await AddScopeAsync(ancestor.Variables, ScopeFor(ancestor.Kind), ancestor.Name);
        }
        await AddScopeAsync(node.Variables, ScopeFor(node.Kind), node.Name);

        foreach (var systemVariable in systemVariables ?? [])
        {
            var source = new VariableSource(
                VariableScopeKind.TokenSession,
                systemVariable.Source,
                null);
            AddBinding(new Binding(
                systemVariable.Name,
                systemVariable.Value,
                systemVariable.IsSecret,
                source,
                [],
                false));
        }

        var states = new Dictionary<string, ResolutionState>(StringComparer.OrdinalIgnoreCase);
        var resolved = new Dictionary<string, ResolvedVariable>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in bindings.Keys)
        {
            ResolveVariable(name, []);
        }

        return new VariableResolutionResult(resolved, issues);

        async Task AddScopeAsync(
            IEnumerable<VariableDefinition> variables,
            VariableScopeKind scope,
            string scopeName)
        {
            var enabled = variables.Where(variable => variable.IsEnabled).ToArray();
            foreach (var duplicate in enabled
                         .Where(variable => !string.IsNullOrWhiteSpace(variable.Name))
                         .GroupBy(variable => variable.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() > 1))
            {
                issues.Add(new VariableResolutionIssue(
                    VariableIssueKind.Duplicate,
                    duplicate.Key,
                    $"{scopeName} 中存在重复变量“{duplicate.Key}”。"));
            }

            foreach (var variable in enabled)
            {
                var name = variable.Name.Trim();
                if (!VariableNamePattern().IsMatch(name) ||
                    name.StartsWith("token.", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new VariableResolutionIssue(
                        VariableIssueKind.InvalidName,
                        name,
                        string.IsNullOrWhiteSpace(name)
                            ? $"{scopeName} 中存在未命名变量。"
                            : $"变量名“{name}”无效或属于保留的 token.* 命名空间。"));
                    continue;
                }

                string? value;
                var missingSecret = false;
                if (variable.IsSecret)
                {
                    value = await secureValueStore
                        .GetAsync(SecureValueKeys.Variable(project.Id, variable.Id), cancellationToken)
                        .ConfigureAwait(false);
                    missingSecret = value is null;
                    value ??= string.Empty;
                }
                else
                {
                    value = variable.Value;
                }

                AddBinding(new Binding(
                    name,
                    value,
                    variable.IsSecret,
                    new VariableSource(scope, scopeName, variable.Id),
                    [],
                    missingSecret));
            }
        }

        void AddBinding(Binding binding)
        {
            if (bindings.TryGetValue(binding.Name, out var previous))
            {
                binding.OverriddenSources.Add(previous.Source);
                binding.OverriddenSources.AddRange(previous.OverriddenSources);
            }

            bindings[binding.Name] = binding;
        }

        string ResolveVariable(string name, IReadOnlyList<string> stack)
        {
            if (!bindings.TryGetValue(name, out var binding))
            {
                issues.Add(new VariableResolutionIssue(
                    VariableIssueKind.Missing,
                    name,
                    $"缺少变量“{name}”。"));
                return string.Empty;
            }

            if (states.GetValueOrDefault(name) == ResolutionState.Resolved)
            {
                return resolved[name].Value;
            }

            if (states.GetValueOrDefault(name) == ResolutionState.Resolving)
            {
                var cycle = string.Join(" → ", stack.Append(name));
                issues.Add(new VariableResolutionIssue(
                    VariableIssueKind.CircularReference,
                    name,
                    $"变量存在循环引用：{cycle}。"));
                return string.Empty;
            }

            if (binding.MissingSecret)
            {
                issues.Add(new VariableResolutionIssue(
                    VariableIssueKind.MissingSecret,
                    name,
                    $"Secret 变量“{name}”尚未在 Windows 安全存储中设置。"));
            }

            states[name] = ResolutionState.Resolving;
            var nestedIssues = new List<VariableResolutionIssue>();
            var referencedNames = PlaceholderPattern()
                .Matches(binding.RawValue)
                .Select(match => match.Groups["name"].Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var value = ReplaceCore(
                binding.RawValue,
                $"变量 {name}",
                referenced => ResolveVariable(referenced, stack.Append(name).ToArray()),
                nestedIssues);
            issues.AddRange(nestedIssues);
            states[name] = ResolutionState.Resolved;
            var inheritsSecret = referencedNames.Any(referenced =>
                resolved.TryGetValue(referenced, out var referencedVariable) && referencedVariable.IsSecret);
            resolved[name] = new ResolvedVariable(
                name,
                value,
                binding.IsSecret || inheritsSecret,
                binding.Source,
                binding.OverriddenSources);
            return value;
        }
    }

    public static TemplateReplacementResult ReplaceTemplate(
        string template,
        string fieldName,
        IReadOnlyDictionary<string, ResolvedVariable> variables,
        bool maskSecrets = false)
    {
        var issues = new List<VariableResolutionIssue>();
        var value = ReplaceCore(
            template,
            fieldName,
            name =>
            {
                if (variables.TryGetValue(name, out var variable))
                {
                    return maskSecrets && variable.IsSecret ? "••••••" : variable.Value;
                }

                issues.Add(new VariableResolutionIssue(
                    VariableIssueKind.Missing,
                    name,
                    $"{fieldName} 引用了缺失变量“{name}”。"));
                return string.Empty;
            },
            issues);
        return new TemplateReplacementResult(value, issues);
    }

    private static string ReplaceCore(
        string template,
        string fieldName,
        Func<string, string> resolve,
        ICollection<VariableResolutionIssue> issues)
    {
        var builder = new StringBuilder();
        var cursor = 0;
        foreach (Match match in PlaceholderPattern().Matches(template))
        {
            builder.Append(template, cursor, match.Index - cursor);
            var name = match.Groups["name"].Value;
            builder.Append(resolve(name));
            cursor = match.Index + match.Length;
        }
        builder.Append(template, cursor, template.Length - cursor);

        var result = builder.ToString();
        if (InvalidPlaceholderPattern().IsMatch(result))
        {
            issues.Add(new VariableResolutionIssue(
                VariableIssueKind.InvalidPlaceholder,
                string.Empty,
                $"{fieldName} 包含无效的变量占位符；请使用 {{{{variable.name}}}}。"));
        }

        return result;
    }

    private static VariableScopeKind ScopeFor(ProjectNodeKind kind) => kind switch
    {
        ProjectNodeKind.Group => VariableScopeKind.Group,
        ProjectNodeKind.Endpoint => VariableScopeKind.Endpoint,
        ProjectNodeKind.RequestCase => VariableScopeKind.RequestCase,
        _ => VariableScopeKind.Endpoint
    };

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.-]*$")]
    private static partial Regex VariableNamePattern();

    [GeneratedRegex(@"\{\{\s*(?<name>[A-Za-z_][A-Za-z0-9_.-]*)\s*\}\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"\{\{")]
    private static partial Regex InvalidPlaceholderPattern();

    private enum ResolutionState
    {
        None,
        Resolving,
        Resolved
    }

    private sealed record Binding(
        string Name,
        string RawValue,
        bool IsSecret,
        VariableSource Source,
        List<VariableSource> OverriddenSources,
        bool MissingSecret);
}
