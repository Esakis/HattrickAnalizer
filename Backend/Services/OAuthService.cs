using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace HattrickAnalizer.Services;

public class OAuthService
{
    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;
    
    private const string RequestTokenUrl = "https://chpp.hattrick.org/oauth/request_token.ashx";
    private const string AuthorizeUrl = "https://chpp.hattrick.org/oauth/authorize.aspx";
    private const string AccessTokenUrl = "https://chpp.hattrick.org/oauth/access_token.ashx";
    private const string ProtectedResourceUrl = "https://chpp.hattrick.org/chppxml.ashx";

    public OAuthService(IConfiguration configuration, HttpClient httpClient)
    {
        _configuration = configuration;
        _httpClient = httpClient;
    }

    public async Task<(string Token, string TokenSecret, string AuthUrl)> GetRequestTokenAsync(string callbackUrl = "oob")
    {
        var consumerKey = _configuration["HattrickApi:ConsumerKey"];
        var consumerSecret = _configuration["HattrickApi:ConsumerSecret"];

        var parameters = new Dictionary<string, string>
        {
            { "oauth_callback", callbackUrl },
            { "oauth_consumer_key", consumerKey! },
            { "oauth_nonce", GenerateNonce() },
            { "oauth_signature_method", "HMAC-SHA1" },
            { "oauth_timestamp", GenerateTimestamp() },
            { "oauth_version", "1.0" }
        };

        var signature = GenerateSignature("GET", RequestTokenUrl, parameters, consumerSecret!, "");
        parameters.Add("oauth_signature", signature);

        var authHeader = BuildAuthorizationHeader(parameters);
        
        var request = new HttpRequestMessage(HttpMethod.Get, RequestTokenUrl);
        request.Headers.Add("Authorization", authHeader);

        var response = await _httpClient.SendAsync(request);
        var responseContent = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Failed to get request token: {responseContent}");
        }

        var responseParams = ParseQueryString(responseContent);
        var token = responseParams["oauth_token"];
        var tokenSecret = responseParams["oauth_token_secret"];
        var authUrl = $"{AuthorizeUrl}?oauth_token={token}";

        return (token, tokenSecret, authUrl);
    }

    public async Task<(string AccessToken, string AccessTokenSecret)> ExchangeRequestTokenAsync(
        string requestToken, 
        string requestTokenSecret, 
        string verifier)
    {
        var consumerKey = _configuration["HattrickApi:ConsumerKey"];
        var consumerSecret = _configuration["HattrickApi:ConsumerSecret"];

        var parameters = new Dictionary<string, string>
        {
            { "oauth_consumer_key", consumerKey! },
            { "oauth_nonce", GenerateNonce() },
            { "oauth_signature_method", "HMAC-SHA1" },
            { "oauth_timestamp", GenerateTimestamp() },
            { "oauth_token", requestToken },
            { "oauth_verifier", verifier },
            { "oauth_version", "1.0" }
        };

        var signature = GenerateSignature("GET", AccessTokenUrl, parameters, consumerSecret!, requestTokenSecret);
        parameters.Add("oauth_signature", signature);

        var authHeader = BuildAuthorizationHeader(parameters);
        
        var request = new HttpRequestMessage(HttpMethod.Get, AccessTokenUrl);
        request.Headers.Add("Authorization", authHeader);

        var response = await _httpClient.SendAsync(request);
        var responseContent = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Failed to exchange token: {responseContent}");
        }

        var responseParams = ParseQueryString(responseContent);
        return (responseParams["oauth_token"], responseParams["oauth_token_secret"]);
    }

    public async Task<string> MakeAuthenticatedRequestAsync(
        string accessToken,
        string accessTokenSecret,
        Dictionary<string, string> queryParams)
    {
        var consumerKey = _configuration["HattrickApi:ConsumerKey"];
        var consumerSecret = _configuration["HattrickApi:ConsumerSecret"];

        var oauthParams = new Dictionary<string, string>
        {
            { "oauth_consumer_key", consumerKey! },
            { "oauth_nonce", GenerateNonce() },
            { "oauth_signature_method", "HMAC-SHA1" },
            { "oauth_timestamp", GenerateTimestamp() },
            { "oauth_token", accessToken },
            { "oauth_version", "1.0" }
        };

        var allParams = new Dictionary<string, string>(oauthParams);
        foreach (var param in queryParams)
        {
            allParams[param.Key] = param.Value;
        }

        var signature = GenerateSignature("GET", ProtectedResourceUrl, allParams, consumerSecret!, accessTokenSecret);
        oauthParams.Add("oauth_signature", signature);

        var authHeader = BuildAuthorizationHeader(oauthParams);
        var queryString = string.Join("&", queryParams.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        var fullUrl = $"{ProtectedResourceUrl}?{queryString}";

        var request = new HttpRequestMessage(HttpMethod.Get, fullUrl);
        request.Headers.Add("Authorization", authHeader);

        var response = await _httpClient.SendAsync(request);
        var responseContent = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Failed to access protected resource: {responseContent}");
        }

        return responseContent;
    }

    private string GenerateSignature(
        string httpMethod,
        string url,
        Dictionary<string, string> parameters,
        string consumerSecret,
        string tokenSecret)
    {
        var sortedParams = parameters
            .OrderBy(p => p.Key)
            .Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}");

        var paramString = string.Join("&", sortedParams);
        
        var signatureBaseString = $"{httpMethod.ToUpper()}&{Uri.EscapeDataString(url)}&{Uri.EscapeDataString(paramString)}";
        
        var signingKey = $"{Uri.EscapeDataString(consumerSecret)}&{Uri.EscapeDataString(tokenSecret)}";
        
        using var hmac = new HMACSHA1(Encoding.ASCII.GetBytes(signingKey));
        var hash = hmac.ComputeHash(Encoding.ASCII.GetBytes(signatureBaseString));
        return Convert.ToBase64String(hash);
    }

    private string BuildAuthorizationHeader(Dictionary<string, string> parameters)
    {
        var headerParams = parameters
            .OrderBy(p => p.Key)
            .Select(p => $"{Uri.EscapeDataString(p.Key)}=\"{Uri.EscapeDataString(p.Value)}\"");

        return $"OAuth {string.Join(", ", headerParams)}";
    }

    private string GenerateNonce()
    {
        return Guid.NewGuid().ToString("N");
    }

    private string GenerateTimestamp()
    {
        var timeSpan = DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
        return Convert.ToInt64(timeSpan.TotalSeconds).ToString();
    }

    private Dictionary<string, string> ParseQueryString(string queryString)
    {
        var result = new Dictionary<string, string>();
        var pairs = queryString.Split('&');
        
        foreach (var pair in pairs)
        {
            var parts = pair.Split('=');
            if (parts.Length == 2)
            {
                result[Uri.UnescapeDataString(parts[0])] = Uri.UnescapeDataString(parts[1]);
            }
        }
        
        return result;
    }
}
