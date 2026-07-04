namespace MyApi.Contracts;

/// <summary>
/// Plain data object for JWT configuration.
/// Contains no infrastructure references (IConfiguration, etc.).
/// </summary>
public record JwtConfig(
    string Key,
    string Issuer,
    string Audience,
    int ExpiresMinutes);