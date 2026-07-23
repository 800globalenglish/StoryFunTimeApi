using System.Text;
using System.Text.Json;

namespace StoryFunTimeApi.Services;

public class ReplicateService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    // This is the specific InstantID model version on Replicate.
    // If this ever needs updating, check https://replicate.com/zsxkib/instant-id/versions
    private const string InstantIdVersion = "f1ca369da43885a347690a98f6b710afbf5f167cb9bf13bd5af512ba4a9f7b63";

    public ReplicateService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _apiKey = configuration["Replicate:ApiKey"] ?? throw new Exception("Replicate API key not configured");
    }

    public async Task<string> CartoonizeImage(byte[] imageBytes, string contentType, string gender, string role, string ageRange, string? extraInstructions = null)
    {
        var base64 = Convert.ToBase64String(imageBytes);
        var dataUri = $"data:{contentType};base64,{base64}";

        var extraNote = !string.IsNullOrWhiteSpace(extraInstructions) ? $" {extraInstructions}." : "";

        var prompt = $"children's storybook illustration portrait, warm cartoon art style, bold clean lines, soft colors, solid plain single-color background, no patterns{extraNote}";
        var negativePrompt = "photo, photorealistic, realistic, text, caption, watermark, logo, extra limbs, deformed, blurry, dark, scary, low quality, background clutter, animals, characters, pattern, busy background";

        // 1. Kick off the prediction
        var requestBody = new
        {
            version = InstantIdVersion,
            input = new
            {
                image = dataUri,
                prompt = prompt,
                negative_prompt = negativePrompt,
                ip_adapter_scale = 0.8,
                controlnet_conditioning_scale = 0.8,
                guidance_scale = 5,
                num_inference_steps = 30
            }
        };

        var startRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.replicate.com/v1/predictions");
        startRequest.Headers.Add("Authorization", $"Bearer {_apiKey}");
        startRequest.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        var startResponse = await _httpClient.SendAsync(startRequest);
        var startBody = await startResponse.Content.ReadAsStringAsync();

        if (!startResponse.IsSuccessStatusCode)
        {
            throw new Exception($"Replicate API error starting prediction ({startResponse.StatusCode}): {startBody}");
        }

        using var startDoc = JsonDocument.Parse(startBody);
        var getUrl = startDoc.RootElement.GetProperty("urls").GetProperty("get").GetString()
            ?? throw new Exception("No polling URL returned from Replicate");

        // 2. Poll until the prediction finishes (success or failure), with a timeout.
        // Replicate models can take 1-3 minutes to "cold start" if not recently used,
        // even though a "warm" model typically finishes in under 20 seconds.
        var maxAttempts = 180; // ~3 minutes at 1s intervals
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            await Task.Delay(1000);

            var pollRequest = new HttpRequestMessage(HttpMethod.Get, getUrl);
            pollRequest.Headers.Add("Authorization", $"Bearer {_apiKey}");
            var pollResponse = await _httpClient.SendAsync(pollRequest);
            var pollBody = await pollResponse.Content.ReadAsStringAsync();

            using var pollDoc = JsonDocument.Parse(pollBody);
            var status = pollDoc.RootElement.GetProperty("status").GetString();

            if (status == "succeeded")
            {
                var output = pollDoc.RootElement.GetProperty("output");
                // Output can be a single URL string, or an array of URL strings depending on the model version
                if (output.ValueKind == JsonValueKind.Array && output.GetArrayLength() > 0)
                {
                    return output[0].GetString() ?? throw new Exception("Empty output URL from Replicate");
                }
                if (output.ValueKind == JsonValueKind.String)
                {
                    return output.GetString() ?? throw new Exception("Empty output URL from Replicate");
                }
                throw new Exception($"Unexpected output shape from Replicate: {pollBody}");
            }

            if (status == "failed" || status == "canceled")
            {
                var error = pollDoc.RootElement.TryGetProperty("error", out var errProp) ? errProp.GetString() : "unknown error";
                throw new Exception($"Replicate prediction {status}: {error}");
            }
            // otherwise status is "starting" or "processing" - keep polling
        }

        throw new Exception("Replicate prediction timed out after 3 minutes");
    }
}
