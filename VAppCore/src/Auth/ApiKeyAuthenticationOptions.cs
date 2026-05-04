using Microsoft.AspNetCore.Authentication;

namespace VAppCore;

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>HTTP header carrying the API key. Default: <c>X-Api-Key</c>.</summary>
    public string HeaderName { get; set; } = "X-Api-Key";
}
