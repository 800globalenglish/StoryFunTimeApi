using System.Text;
using System.Text.Json;

namespace StoryFunTimeApi.Services;

public class EmailService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly string _senderEmail;
    private readonly string _senderName;
    private readonly string _apiBaseUrl;
    private readonly string _webAppBaseUrl;

    public EmailService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _apiKey = configuration["Brevo:ApiKey"] ?? throw new Exception("Brevo API key not configured");
        _senderEmail = configuration["Brevo:SenderEmail"] ?? throw new Exception("Brevo sender email not configured");
        _senderName = configuration["Brevo:SenderName"] ?? "StoryFunTime";
        _apiBaseUrl = configuration["Api:BaseUrl"] ?? throw new Exception("Api:BaseUrl not configured");
        _webAppBaseUrl = configuration["WebApp:BaseUrl"] ?? _apiBaseUrl;
    }

    public async Task SendVerificationEmailAsync(string toEmail, string toUsername, string token)
    {
        var verifyLink = $"{_apiBaseUrl}/auth/verify-email?token={token}";

        var htmlContent = $@"
            <div style=""font-family: sans-serif; max-width: 480px; margin: 0 auto;"">
                <h2>Welcome to StoryFunTime, {toUsername}!</h2>
                <p>Please confirm your email address by clicking the button below.</p>
                <p style=""margin: 24px 0;"">
                    <a href=""{verifyLink}"" style=""background: #6750A4; color: white; padding: 12px 24px; border-radius: 8px; text-decoration: none;"">
                        Verify my email
                    </a>
                </p>
                <p>Or copy and paste this link into your browser:<br>{verifyLink}</p>
            </div>";

        var payload = new
        {
            sender = new { email = _senderEmail, name = _senderName },
            to = new[] { new { email = toEmail, name = toUsername } },
            subject = "Please verify your StoryFunTime email",
            htmlContent
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("api-key", _apiKey);

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new Exception($"Brevo email send failed ({response.StatusCode}): {body}");
        }
    }

    public async Task SendPasswordResetEmailAsync(string toEmail, string toUsername, string token)
    {
        var resetLink = $"{_webAppBaseUrl}/?resetToken={token}";

        var htmlContent = $@"
            <div style=""font-family: sans-serif; max-width: 480px; margin: 0 auto;"">
                <h2>Reset your StoryFunTime password</h2>
                <p>Hi {toUsername}, click the button below to set a new password. This link expires in 1 hour.</p>
                <p style=""margin: 24px 0;"">
                    <a href=""{resetLink}"" style=""background: #6750A4; color: white; padding: 12px 24px; border-radius: 8px; text-decoration: none;"">
                        Reset my password
                    </a>
                </p>
                <p>Or copy and paste this link into your browser:<br>{resetLink}</p>
                <p>If you didn't request this, you can safely ignore this email.</p>
            </div>";

        var payload = new
        {
            sender = new { email = _senderEmail, name = _senderName },
            to = new[] { new { email = toEmail, name = toUsername } },
            subject = "Reset your StoryFunTime password",
            htmlContent
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.brevo.com/v3/smtp/email")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("api-key", _apiKey);

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new Exception($"Brevo email send failed ({response.StatusCode}): {body}");
        }
    }
}
