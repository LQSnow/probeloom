using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;

namespace ProbeLoom.Core;

public sealed record PreparedRequestResult(
    RequestValidationResult Validation,
    HttpRequestPlan? Plan,
    HttpRequestPlan? SafePlan,
    VariableResolutionResult VariableResolution,
    RequestUrlBreakdown DisplayUrlBreakdown,
    AuthenticationKind AuthenticationKind,
    bool RequiresTokenRefresh,
    string AuthenticationSummary,
    string AuthenticationHeaderName,
    string AuthenticationQueryName)
{
    public bool Succeeded => Validation.IsValid && Plan is not null;
}

public static class RequestPreparationService
{
    public static async Task<PreparedRequestResult> PrepareAsync(
        ProjectDocument project,
        ProjectEnvironment? environment,
        ProjectNode node,
        TimeSpan timeout,
        ISecureValueStore secureValueStore,
        TokenSession? tokenSession,
        DateTimeOffset? now = null,
        CancellationToken cancellationToken = default)
    {
        var systemVariables = BuildTokenVariables(tokenSession);
        var variables = await VariableResolver.ResolveAsync(
            project,
            environment,
            node,
            secureValueStore,
            systemVariables,
            cancellationToken).ConfigureAwait(false);
        var replacementIssues = new List<VariableResolutionIssue>();
        var actual = BuildResolvedContext(project, environment, node, variables, false, replacementIssues);
        var masked = BuildResolvedContext(project, environment, node, variables, true, []);

        var planResult = HttpRequestPlanner.Create(actual.Project, actual.Environment, actual.Node, timeout);
        var maskedPlanResult = HttpRequestPlanner.Create(
            masked.Project,
            masked.Environment,
            masked.Node,
            timeout);
        var variableIssues = variables.Issues.Concat(replacementIssues)
            .Distinct()
            .Select(issue => new RequestValidationIssue(ValidationTarget.Variables, issue.Message))
            .ToArray();
        var authenticationTemplateIssues = ValidateAuthenticationTemplates(
            node.Request!.Authentication,
            variables);
        var validation = planResult.Validation;
        if (variableIssues.Length > 0 || authenticationTemplateIssues.Count > 0)
        {
            validation = validation with
            {
                IsValid = false,
                Issues = validation.Issues
                    .Concat(variableIssues)
                    .Concat(authenticationTemplateIssues)
                    .ToArray()
            };
        }

        if (planResult.Plan is null)
        {
            return new PreparedRequestResult(
                validation,
                null,
                null,
                variables,
                maskedPlanResult.Validation.UrlBreakdown,
                actual.Node.Request!.Authentication.Kind,
                false,
                "请求尚未通过校验",
                string.Empty,
                string.Empty);
        }

        var actualAuth = ApplyAuthentication(planResult.Plan, actual.Node.Request!, tokenSession, maskSecrets: false);
        var maskedAuth = maskedPlanResult.Plan is null
            ? null
            : ApplyAuthentication(maskedPlanResult.Plan, masked.Node.Request!, tokenSession, maskSecrets: true).Plan;
        if (actualAuth.Issues.Count > 0)
        {
            validation = validation with
            {
                IsValid = false,
                Issues = validation.Issues.Concat(actualAuth.Issues).ToArray()
            };
        }

        var plan = validation.IsValid
            ? actualAuth.Plan with { SafeDisplayUrl = maskedAuth?.Uri.AbsoluteUri ?? actualAuth.Plan.DisplayUrl }
            : null;
        var displayUrlBreakdown = maskedPlanResult.Validation.UrlBreakdown;
        if (maskedAuth is not null)
        {
            displayUrlBreakdown = displayUrlBreakdown with
            {
                FinalUrl = maskedAuth.Uri.AbsoluteUri
            };
        }
        var authentication = actual.Node.Request!.Authentication;
        var usesSessionBearer = authentication.Kind == AuthenticationKind.BearerToken &&
                                string.IsNullOrWhiteSpace(authentication.BearerToken);
        var requiresRefresh = usesSessionBearer &&
                              tokenSession?.IsExpired(now ?? DateTimeOffset.Now) == true;
        return new PreparedRequestResult(
            validation,
            plan,
            validation.IsValid ? maskedAuth : null,
            variables,
            displayUrlBreakdown,
            authentication.Kind,
            requiresRefresh,
            AuthenticationSummary(authentication.Kind, usesSessionBearer),
            authentication.Kind switch
            {
                AuthenticationKind.BearerToken or AuthenticationKind.Basic => "Authorization",
                AuthenticationKind.ApiKey when authentication.ApiKeyLocation == ApiKeyLocation.Header =>
                    authentication.ApiKeyName,
                _ => string.Empty
            },
            authentication.Kind == AuthenticationKind.ApiKey &&
            authentication.ApiKeyLocation == ApiKeyLocation.Query
                ? authentication.ApiKeyName
                : string.Empty);
    }

    private static ResolvedContext BuildResolvedContext(
        ProjectDocument sourceProject,
        ProjectEnvironment? sourceEnvironment,
        ProjectNode sourceNode,
        VariableResolutionResult variables,
        bool maskSecrets,
        ICollection<VariableResolutionIssue> issues)
    {
        string Replace(string value, string field)
        {
            var result = maskSecrets
                ? variables.ReplaceMasked(value, field)
                : variables.Replace(value, field);
            foreach (var issue in result.Issues)
            {
                issues.Add(issue);
            }
            return result.Value;
        }

        var project = new ProjectDocument
        {
            Id = sourceProject.Id,
            Name = sourceProject.Name,
            RefreshRequestNodeId = sourceProject.RefreshRequestNodeId,
            RouteParts = new ObservableCollection<RoutePart>(
                sourceProject.RouteParts.Select(part => new RoutePart
                {
                    Id = part.Id,
                    IsEnabled = part.IsEnabled,
                    Name = part.Name,
                    Value = Replace(part.Value, $"Project Route Part {part.Name}")
                }))
        };
        var environment = sourceEnvironment is null
            ? null
            : new ProjectEnvironment
            {
                Id = sourceEnvironment.Id,
                Name = sourceEnvironment.Name,
                BaseUrl = Replace(sourceEnvironment.BaseUrl, "Environment Base URL")
            };

        var chain = ProjectOperations.GetAncestors(sourceProject, sourceNode.Id)
            .Append(sourceNode)
            .ToArray();
        ProjectNode? root = null;
        ProjectNode? parent = null;
        ProjectNode? resolvedNode = null;
        foreach (var original in chain)
        {
            var clone = new ProjectNode
            {
                Id = original.Id,
                Kind = original.Kind,
                Name = original.Name,
                IsRoutePrefixEnabled = original.IsRoutePrefixEnabled,
                RoutePrefix = Replace(original.RoutePrefix, $"Group {original.Name} Route Prefix")
            };
            if (original.Id == sourceNode.Id)
            {
                clone.Request = ResolveRequest(original.Request!, Replace);
                resolvedNode = clone;
            }

            if (parent is null)
            {
                root = clone;
            }
            else
            {
                parent.Children.Add(clone);
            }
            parent = clone;
        }

        if (root is null || resolvedNode is null)
        {
            throw new InvalidOperationException("无法构建请求的工作区上下文。");
        }

        project.Items.Add(root);
        return new ResolvedContext(project, environment, resolvedNode);
    }

    private static RequestDefinition ResolveRequest(
        RequestDefinition source,
        Func<string, string, string> replace) =>
        new()
        {
            Method = replace(source.Method, "HTTP Method"),
            Route = replace(source.Route, "Endpoint Route"),
            RawJsonBody = replace(source.RawJsonBody, "Raw JSON Body"),
            PathParameters = ResolveFields(source.PathParameters, "Path Parameter", replace),
            QueryParameters = ResolveFields(source.QueryParameters, "Query Parameter", replace),
            Headers = ResolveFields(source.Headers, "Header", replace),
            Authentication = new AuthenticationConfiguration
            {
                Kind = source.Authentication.Kind,
                BearerToken = replace(source.Authentication.BearerToken, "Bearer Token"),
                Username = replace(source.Authentication.Username, "Basic Auth Username"),
                Password = replace(source.Authentication.Password, "Basic Auth Password"),
                ApiKeyName = replace(source.Authentication.ApiKeyName, "API Key Name"),
                ApiKeyValue = replace(source.Authentication.ApiKeyValue, "API Key Value"),
                ApiKeyLocation = source.Authentication.ApiKeyLocation
            },
            TokenCapture = source.TokenCapture.Clone()
        };

    private static ObservableCollection<RequestField> ResolveFields(
        IEnumerable<RequestField> fields,
        string label,
        Func<string, string, string> replace) =>
        new(fields.Select(field => new RequestField(
            replace(field.Name, $"{label} Name"),
            replace(field.Value, $"{label} Value"),
            field.IsEnabled)));

    private static AuthenticationResult ApplyAuthentication(
        HttpRequestPlan plan,
        RequestDefinition request,
        TokenSession? tokenSession,
        bool maskSecrets)
    {
        var authentication = request.Authentication;
        var issues = new List<RequestValidationIssue>();
        var headers = plan.Headers.ToList();
        var uri = plan.Uri;

        switch (authentication.Kind)
        {
            case AuthenticationKind.None:
                break;
            case AuthenticationKind.BearerToken:
            {
                if (headers.Any(header =>
                        string.Equals(header.Name, "Authorization", StringComparison.OrdinalIgnoreCase)))
                {
                    issues.Add(new RequestValidationIssue(
                        ValidationTarget.Authentication,
                        "已配置结构化 Authentication，请移除手写的 Authorization Header。"));
                    break;
                }

                var token = string.IsNullOrWhiteSpace(authentication.BearerToken)
                    ? tokenSession?.AccessToken ?? string.Empty
                    : authentication.BearerToken;
                if (string.IsNullOrWhiteSpace(token))
                {
                    issues.Add(new RequestValidationIssue(
                        ValidationTarget.Authentication,
                        "Bearer Token 未设置；请登录、手动设置 Token，或填写 Bearer Token 模板。"));
                    break;
                }

                headers.Add(new HttpHeaderValue("Authorization", $"Bearer {(maskSecrets ? "••••••" : token)}"));
                break;
            }
            case AuthenticationKind.Basic:
            {
                if (string.IsNullOrWhiteSpace(authentication.Username) ||
                    string.IsNullOrEmpty(authentication.Password))
                {
                    issues.Add(new RequestValidationIssue(
                        ValidationTarget.Authentication,
                        "Basic Auth 需要 Username 和 Password。"));
                    break;
                }

                var value = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    $"{authentication.Username}:{authentication.Password}"));
                headers.RemoveAll(header =>
                    string.Equals(header.Name, "Authorization", StringComparison.OrdinalIgnoreCase));
                headers.Add(new HttpHeaderValue("Authorization", $"Basic {(maskSecrets ? "••••••" : value)}"));
                break;
            }
            case AuthenticationKind.ApiKey:
            {
                if (string.IsNullOrWhiteSpace(authentication.ApiKeyName) ||
                    string.IsNullOrEmpty(authentication.ApiKeyValue))
                {
                    issues.Add(new RequestValidationIssue(
                        ValidationTarget.Authentication,
                        "API Key 需要名称和值。"));
                    break;
                }

                var value = maskSecrets ? "••••••" : authentication.ApiKeyValue;
                if (authentication.ApiKeyLocation == ApiKeyLocation.Header)
                {
                    if (headers.Any(header => string.Equals(
                            header.Name,
                            authentication.ApiKeyName,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        issues.Add(new RequestValidationIssue(
                            ValidationTarget.Authentication,
                            $"Header 中已存在“{authentication.ApiKeyName}”，无法重复注入 API Key。"));
                        break;
                    }
                    headers.Add(new HttpHeaderValue(authentication.ApiKeyName, value));
                }
                else
                {
                    uri = AppendQuery(uri, authentication.ApiKeyName, value);
                }
                break;
            }
        }

        return new AuthenticationResult(plan with { Uri = uri, Headers = headers }, issues);
    }

    private static Uri AppendQuery(Uri uri, string name, string value)
    {
        var builder = new UriBuilder(uri);
        var item = $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value)}";
        builder.Query = string.IsNullOrEmpty(builder.Query)
            ? item
            : $"{builder.Query.TrimStart('?')}&{item}";
        return builder.Uri;
    }

    private static IReadOnlyList<SystemVariable> BuildTokenVariables(TokenSession? session)
    {
        if (session is null)
        {
            return [];
        }

        return
        [
            new SystemVariable("token.access", session.AccessToken, true, "Token Session"),
            new SystemVariable("token.refresh", session.RefreshToken, true, "Token Session"),
            new SystemVariable(
                "token.expiresAt",
                session.ExpiresAt?.ToString("O") ?? string.Empty,
                false,
                "Token Session")
        ];
    }

    private static string AuthenticationSummary(AuthenticationKind kind, bool usesSessionBearer) => kind switch
    {
        AuthenticationKind.None => "No Auth",
        AuthenticationKind.BearerToken when usesSessionBearer => "Bearer · Environment Token Session",
        AuthenticationKind.BearerToken => "Bearer · Request Template",
        AuthenticationKind.Basic => "Basic Auth",
        AuthenticationKind.ApiKey => "API Key",
        _ => "No Auth"
    };

    private static IReadOnlyList<RequestValidationIssue> ValidateAuthenticationTemplates(
        AuthenticationConfiguration authentication,
        VariableResolutionResult variables)
    {
        var issues = new List<RequestValidationIssue>();
        switch (authentication.Kind)
        {
            case AuthenticationKind.BearerToken when !string.IsNullOrWhiteSpace(authentication.BearerToken):
                ValidateSecretTemplate(authentication.BearerToken, "Bearer Token", variables, issues);
                break;
            case AuthenticationKind.Basic:
                ValidateSecretTemplate(authentication.Password, "Basic Auth Password", variables, issues);
                break;
            case AuthenticationKind.ApiKey:
                ValidateSecretTemplate(authentication.ApiKeyValue, "API Key Value", variables, issues);
                break;
        }
        return issues;
    }

    private static void ValidateSecretTemplate(
        string template,
        string label,
        VariableResolutionResult variables,
        ICollection<RequestValidationIssue> issues)
    {
        var match = Regex.Match(
            template.Trim(),
            @"^\{\{\s*(?<name>[A-Za-z_][A-Za-z0-9_.-]*)\s*\}\}$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            issues.Add(new RequestValidationIssue(
                ValidationTarget.Authentication,
                $"{label} 必须引用一个 Secret 变量，不能直接写入项目文件。"));
            return;
        }

        var name = match.Groups["name"].Value;
        if (!variables.Variables.TryGetValue(name, out var variable) || !variable.IsSecret)
        {
            issues.Add(new RequestValidationIssue(
                ValidationTarget.Authentication,
                $"{label} 引用的“{name}”必须定义为 Secret。"));
        }
    }

    private sealed record ResolvedContext(
        ProjectDocument Project,
        ProjectEnvironment? Environment,
        ProjectNode Node);

    private sealed record AuthenticationResult(
        HttpRequestPlan Plan,
        IReadOnlyList<RequestValidationIssue> Issues);
}
