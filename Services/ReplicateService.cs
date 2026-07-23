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

    // Takes a scene image (from Grok) and swaps in a specific character's real face/likeness,
    // using their InstantID avatar as the source of truth for what they actually look like.
    // Generates a cartoon avatar using nano-banana instead of InstantID - this model runs "warm"
    // (no cold-start delay) and has proven strong at preserving identity from reference images.
    public async Task<string> GenerateAvatarWithNanoBanana(byte[] imageBytes, string contentType, string gender, string role, string ageRange, string? extraInstructions = null)
    {
        var dataUri = $"data:{contentType};base64,{Convert.ToBase64String(imageBytes)}";
        var isAnimal = role == "Pet";
        var extraNote = !string.IsNullOrWhiteSpace(extraInstructions) ? $" {extraInstructions}." : "";

        string prompt;
        if (isAnimal)
        {
            prompt = $"Using the exact reference photo, illustrate this specific animal as a warm children's storybook cartoon avatar portrait - same fur/coat color and pattern, same markings, same ear and face shape. Keep it a naturalistic animal on all four legs, not a human or anthropomorphized character. Solid plain single-color background, no patterns, no text, no props.{extraNote}";
        }
        else
        {
            var genderNote = !string.IsNullOrWhiteSpace(gender) ? $" This is a {gender}." : "";
            var ageDescription = ageRange switch
            {
                "0-2" => "an infant or toddler - large head relative to body, round chubby cheeks, no facial hair, very short or no hair",
                "3-5" => "a young preschool-aged child - small child proportions, soft round face",
                "6-9" => "a young school-aged child",
                "10-13" => "a preteen, noticeably taller and less baby-faced than a young child",
                "14-18" => "a teenager, with more adult-like facial proportions",
                "40-50" => "a middle-aged adult in their 40s",
                "51-65" => "an adult in their 50s or early 60s, possibly with some gray or graying hair",
                "66-80" => "an older adult, with gray or white hair and visible age lines",
                "81+" => "an elderly adult, with white hair and pronounced signs of aging",
                _ => ""
            };
            var ageNote = !string.IsNullOrWhiteSpace(ageDescription) ? $" This is {ageDescription}. Keep their proportions and features true to that specific age." : "";

            prompt = $"Using the exact reference photo, illustrate this specific person as a warm children's storybook cartoon avatar portrait. Keep their exact face shape, eye color and shape, hair color and style, and skin tone - this must be recognizably the same real person, just illustrated instead of photographed.{genderNote}{ageNote} Solid plain single-color background, no patterns, no text, no props.{extraNote}";
        }

        var requestBody = new
        {
            input = new
            {
                prompt = prompt,
                image_input = new[] { dataUri },
                output_format = "jpg"
            }
        };

        var startRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.replicate.com/v1/models/google/nano-banana/predictions");
        startRequest.Headers.Add("Authorization", $"Bearer {_apiKey}");
        startRequest.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        var startResponse = await _httpClient.SendAsync(startRequest);
        var startBody = await startResponse.Content.ReadAsStringAsync();

        if (!startResponse.IsSuccessStatusCode)
        {
            throw new Exception($"Replicate API error starting avatar generation ({startResponse.StatusCode}): {startBody}");
        }

        using var startDoc = JsonDocument.Parse(startBody);
        var getUrl = startDoc.RootElement.GetProperty("urls").GetProperty("get").GetString()
            ?? throw new Exception("No polling URL returned from Replicate");

        var maxAttempts = 60;
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
                if (output.ValueKind == JsonValueKind.String)
                {
                    return output.GetString() ?? throw new Exception("Empty output URL from Replicate avatar generation");
                }
                if (output.ValueKind == JsonValueKind.Array && output.GetArrayLength() > 0)
                {
                    return output[0].GetString() ?? throw new Exception("Empty output URL from Replicate avatar generation");
                }
                throw new Exception($"Unexpected output shape from Replicate avatar generation: {pollBody}");
            }

            if (status == "failed" || status == "canceled")
            {
                var error = pollDoc.RootElement.TryGetProperty("error", out var errProp) ? errProp.GetString() : "unknown error";
                throw new Exception($"Replicate avatar generation {status}: {error}");
            }
        }

        throw new Exception("Replicate avatar generation timed out after 60 seconds");
    }

    // Generates a full scene directly, using each character's real avatar as a reference image,
    // so the model preserves everyone's actual likeness in one single generation step.
    public async Task<string> GenerateSceneWithCharacters(List<(byte[] Bytes, string ContentType, string Name, string Gender)> characterAvatars, string sceneDescription, string? extraInstructions = null)
    {
        var imageInputs = characterAvatars
            .Select(c => $"data:{c.ContentType};base64,{Convert.ToBase64String(c.Bytes)}")
            .ToList();

        var characterNotes = string.Join(" ", characterAvatars.Select((c, i) =>
            $"Reference image {i + 1} shows {c.Name}. Copy {c.Name}'s exact hair color, hair style, eye color, skin tone, and any distinguishing features (glasses, facial hair, etc) precisely from their reference image - do not default to a generic or average appearance."));

        var extraNote = !string.IsNullOrWhiteSpace(extraInstructions) ? $" {extraInstructions}." : "";

        var prompt = $"Using the exact people shown in the reference images, illustrate this children's storybook scene: {sceneDescription}. {characterNotes} Do not invent different-looking people - use the exact faces and identities from the reference images. Keep each character's face clearly visible and mostly facing toward the viewer - avoid extreme angles, faces looking sharply down, or faces partially hidden/obscured. Each character must look like the exact same person as their reference avatar image - match their face precisely, not just their general age and gender. Warm, colorful children's book illustration style.{extraNote}";

        var requestBody = new
        {
            input = new
            {
                prompt = prompt,
                image_input = imageInputs,
                output_format = "jpg"
            }
        };

        var startRequest = new HttpRequestMessage(HttpMethod.Post, "https://api.replicate.com/v1/models/google/nano-banana/predictions");
        startRequest.Headers.Add("Authorization", $"Bearer {_apiKey}");
        startRequest.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

        var startResponse = await _httpClient.SendAsync(startRequest);
        var startBody = await startResponse.Content.ReadAsStringAsync();

        if (!startResponse.IsSuccessStatusCode)
        {
            throw new Exception($"Replicate API error starting scene generation ({startResponse.StatusCode}): {startBody}");
        }

        using var startDoc = JsonDocument.Parse(startBody);
        var getUrl = startDoc.RootElement.GetProperty("urls").GetProperty("get").GetString()
            ?? throw new Exception("No polling URL returned from Replicate");

        var maxAttempts = 60;
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
                if (output.ValueKind == JsonValueKind.String)
                {
                    return output.GetString() ?? throw new Exception("Empty output URL from Replicate scene generation");
                }
                if (output.ValueKind == JsonValueKind.Array && output.GetArrayLength() > 0)
                {
                    return output[0].GetString() ?? throw new Exception("Empty output URL from Replicate scene generation");
                }
                throw new Exception($"Unexpected output shape from Replicate scene generation: {pollBody}");
            }

            if (status == "failed" || status == "canceled")
            {
                var error = pollDoc.RootElement.TryGetProperty("error", out var errProp) ? errProp.GetString() : "unknown error";
                throw new Exception($"Replicate scene generation {status}: {error}");
            }
        }

        throw new Exception("Replicate scene generation timed out after 60 seconds");
    }
}
