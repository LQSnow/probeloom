namespace ProbeLoom.Core;

public enum AuthenticationKind
{
    None,
    BearerToken,
    Basic,
    ApiKey
}

public enum ApiKeyLocation
{
    Header,
    Query
}

public sealed class AuthenticationConfiguration : ObservableEntity
{
    private AuthenticationKind _kind;
    private string _bearerToken = string.Empty;
    private string _username = string.Empty;
    private string _password = string.Empty;
    private string _apiKeyName = "X-API-Key";
    private string _apiKeyValue = string.Empty;
    private ApiKeyLocation _apiKeyLocation = ApiKeyLocation.Header;

    public AuthenticationKind Kind
    {
        get => _kind;
        set => SetProperty(ref _kind, value);
    }

    public string BearerToken
    {
        get => _bearerToken;
        set => SetProperty(ref _bearerToken, value ?? string.Empty);
    }

    public string Username
    {
        get => _username;
        set => SetProperty(ref _username, value ?? string.Empty);
    }

    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value ?? string.Empty);
    }

    public string ApiKeyName
    {
        get => _apiKeyName;
        set => SetProperty(ref _apiKeyName, value ?? string.Empty);
    }

    public string ApiKeyValue
    {
        get => _apiKeyValue;
        set => SetProperty(ref _apiKeyValue, value ?? string.Empty);
    }

    public ApiKeyLocation ApiKeyLocation
    {
        get => _apiKeyLocation;
        set => SetProperty(ref _apiKeyLocation, value);
    }

    public AuthenticationConfiguration Clone() =>
        new()
        {
            Kind = Kind,
            BearerToken = BearerToken,
            Username = Username,
            Password = Password,
            ApiKeyName = ApiKeyName,
            ApiKeyValue = ApiKeyValue,
            ApiKeyLocation = ApiKeyLocation
        };
}

public sealed class TokenCaptureConfiguration : ObservableEntity
{
    private bool _isEnabled;
    private string _accessTokenPath = "$.accessToken";
    private string _refreshTokenPath = "$.refreshToken";
    private string _expiresInPath = "$.expiresIn";
    private string _expiresAtPath = string.Empty;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public string AccessTokenPath
    {
        get => _accessTokenPath;
        set => SetProperty(ref _accessTokenPath, value ?? string.Empty);
    }

    public string RefreshTokenPath
    {
        get => _refreshTokenPath;
        set => SetProperty(ref _refreshTokenPath, value ?? string.Empty);
    }

    public string ExpiresInPath
    {
        get => _expiresInPath;
        set => SetProperty(ref _expiresInPath, value ?? string.Empty);
    }

    public string ExpiresAtPath
    {
        get => _expiresAtPath;
        set => SetProperty(ref _expiresAtPath, value ?? string.Empty);
    }

    public TokenCaptureConfiguration Clone() =>
        new()
        {
            IsEnabled = IsEnabled,
            AccessTokenPath = AccessTokenPath,
            RefreshTokenPath = RefreshTokenPath,
            ExpiresInPath = ExpiresInPath,
            ExpiresAtPath = ExpiresAtPath
        };
}
