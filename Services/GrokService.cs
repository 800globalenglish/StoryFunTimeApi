using System.Text;
using System.Text.Json;
using static System.Net.Mime.MediaTypeNames;

namespace StoryFunTimeApi.Services;

public class GrokService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public GrokService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _apiKey = configuration["Grok:ApiKey"] ?? throw new Exception("Grok API key not configured");
    }

    public async Task<List<string>> GenerateStoryPages(string title, string theme, int pageCount, List<string> characterDescriptions, string storyType = "bedtime", string? extraInstructions = null)
    {
        var charactersText = characterDescriptions.Count > 0
            ? $"The story features these real people, who should be called by name throughout: {string.Join(", ", characterDescriptions)}. Every single one of them must play an active, meaningful part in the story from beginning to end — do not just mention a character's name once and drop them. Do NOT invent or add any additional characters, people, or animals beyond the ones listed here."
            : "";

        var styleText = storyType switch
        {
            "adventure" => "an exciting adventure story full of action and discovery",
            "sillyFunny" => "a silly, funny story full of humor and playful situations",
            "fairyTale" => "a classic fairy tale, with a magical, once-upon-a-time feel",
            "superhero" => "an exciting superhero story about using powers to help others",
            "mystery" => "a gentle mystery story where the characters solve a fun puzzle or clue",
            "spaceAdventure" => "a space adventure story with rockets, planets, and exploration",
            "animalFriends" => "a heartwarming story about animal friends",
            "learning" => "a gentle, educational story that teaches something new in a fun way",
            "ownTheme" => "a story that's true to its own title and theme",
            _ => "a gentle, warm bedtime story"
        };

        var endingNote = storyType == "bedtime"
            ? "End the story with the character winding down, feeling sleepy, and settling in for bed — a calm, cozy bedtime conclusion."
            : "IMPORTANT: this is NOT a bedtime story. Do NOT end it with anyone falling asleep, yawning, getting tucked in, or going to bed — no nighttime or bedtime imagery of any kind. End it in whatever way naturally fits THIS story instead, such as a triumphant finish, a happy resolution, solving the mystery, or a satisfying discovery.";

        var prompt = $@"Write a short story titled ""{title}"" about {theme}.
{charactersText}
Break it into exactly {pageCount} pages, each 1-3 sentences, simple enough to read aloud.
This should be {styleText}.
{endingNote}
{(!string.IsNullOrWhiteSpace(extraInstructions) ? $"\nAdditional instructions: {extraInstructions}." : "")}
Respond with ONLY a JSON array of strings, one string per page, in order. No other text, no markdown formatting, just the raw JSON array.
Example format: [""Page 1 text here."", ""Page 2 text here.""]";

        var requestBody = new
        {
            model = "grok-4-0709",
            messages = new[]
            {
            new { role = "user", content = prompt }
        }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.x.ai/v1/chat/completions");
        request.Headers.Add("Authorization", $"Bearer {_apiKey}");
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Grok API error ({response.StatusCode}): {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "[]";

        content = content.Trim();
        if (content.StartsWith("```"))
        {
            content = content.Substring(content.IndexOf('\n') + 1);
            content = content.Substring(0, content.LastIndexOf("```"));
        }

        var pages = JsonSerializer.Deserialize<List<string>>(content.Trim());
        return pages ?? new List<string>();
    }

    public async Task<string> CartoonizeImage(byte[] imageBytes, string contentType, string gender, string role, string ageRange, string? extraInstructions = null)
    {
        var base64 = Convert.ToBase64String(imageBytes);
        var dataUri = $"data:{contentType};base64,{base64}";

        var isAnimal = role == "Pet";

        var extraNote = !string.IsNullOrWhiteSpace(extraInstructions)
            ? $" Additional instructions: {extraInstructions}."
            : "";

        string prompt;
        if (isAnimal)
        {
            prompt = $"This is a photo of a real animal. Convert this exact photo into a children's storybook cartoon illustration of this specific animal — same fur or coat color and pattern, same markings, same ear and face shape, same breed characteristics. IMPORTANT: This must remain a naturalistic animal on all four legs. Do NOT turn it into a human, do NOT give it a human face, human hair, glasses, human clothing, or any human-like features. It is an animal, drawn in a warm cartoon storybook style, not an anthropomorphized character.{extraNote}";
        }
        else
        {
            var genderNote = !string.IsNullOrWhiteSpace(gender)
                ? $" This is a {gender}."
                : "";
            var ageDescription = ageRange switch
            {
                "0-2" => "an infant or toddler — large head relative to body, round chubby cheeks, no facial hair, very short or no hair",
                "3-5" => "a young preschool-aged child — small child proportions, soft round face",
                "6-9" => "a young school-aged child",
                "10-13" => "a preteen, noticeably taller and less baby-faced than a young child",
                "14-18" => "a teenager, with more adult-like facial proportions",
                "40-50" => "a middle-aged adult in their 40s",
                "51-65" => "an adult in their 50s or early 60s, possibly with some gray or graying hair",
                "66-80" => "an older adult, with gray or white hair and visible age lines",
                "81+" => "an elderly adult, with white hair and pronounced signs of aging",
                _ => ""
            };
            var ageNote = !string.IsNullOrWhiteSpace(ageDescription)
                ? $" This is {ageDescription}. Keep their proportions and features true to that specific age — do not draw them older or younger than this."
                : "";
            prompt = $"Redraw this exact photo as a simple children's storybook illustration portrait. Keep the same hair color and hair style, same face shape, same eye color and eye shape, same skin tone, same expression. Only the art style should change — from photo to warm illustrated line art with soft colors. Do NOT add any text, captions, words, background scenery, props, balloons, or decorations of any kind. Just this exact person's face and shoulders, illustrated, on a plain simple background.{genderNote}{ageNote}{extraNote}";
        }
        var requestBody = new
        {
            prompt = prompt,
            model = "grok-imagine-image-quality",
            image_url = dataUri,
            resolution = "2k"
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.x.ai/v1/images/edits");
        request.Headers.Add("Authorization", $"Bearer {_apiKey}");
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Grok image API error ({response.StatusCode}): {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        if (root.TryGetProperty("data", out var dataArray) && dataArray.GetArrayLength() > 0)
        {
            return dataArray[0].GetProperty("url").GetString() ?? throw new Exception("No URL in response");
        }
        if (root.TryGetProperty("url", out var urlProp))
        {
            return urlProp.GetString() ?? throw new Exception("No URL in response");
        }

        throw new Exception($"Unexpected response shape: {responseBody}");
    }

    public async Task<string> GenerateSceneImage(List<(byte[] Bytes, string ContentType, string Name, string Gender)> characterAvatars, string sceneDescription, string? extraInstructions = null)
    {
        var imageUrls = characterAvatars
            .Select(img => $"data:{img.ContentType};base64,{Convert.ToBase64String(img.Bytes)}")
            .ToList();

        var characterNotes = string.Join(" ", characterAvatars.Select((c, i) =>
            $"Reference image {i + 1} shows {c.Name}, who is a {(!string.IsNullOrWhiteSpace(c.Gender) ? c.Gender : "person")} — their gender must stay a {(!string.IsNullOrWhiteSpace(c.Gender) ? c.Gender : "unchanged")} in the scene, and they must wear the exact same outfit (same colors, same style of clothing) shown in their reference image."));

        var extraNote = !string.IsNullOrWhiteSpace(extraInstructions)
            ? $" Additional instructions for this scene: {extraInstructions}."
            : "";

        var prompt = $"Using these exact reference characters, people, and animals, recreate the following scene as a warm children's storybook illustration: {sceneDescription}. The reference images show the specific individuals who must appear — do not invent new or different-looking characters. {characterNotes} For each person or animal shown in a reference image, precisely match their reference: same hair color/style, same eye color, same skin tone or fur color, same face shape and distinguishing features, same gender.{extraNote} Simply place these exact characters into the described scene, illustrated in a consistent storybook art style. Only include objects, props, and background elements explicitly mentioned in the scene description — do not add extra unrelated items, tools, or props that aren't part of the story.";

        var requestBody = new
        {
            prompt = prompt,
            model = "grok-imagine-image-quality",
            image_urls = imageUrls,
            resolution = "2k"
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.x.ai/v1/images/edits");
        request.Headers.Add("Authorization", $"Bearer {_apiKey}");
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Grok image API error ({response.StatusCode}): {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;

        if (root.TryGetProperty("data", out var dataArray) && dataArray.GetArrayLength() > 0)
        {
            return dataArray[0].GetProperty("url").GetString() ?? throw new Exception("No URL in response");
        }
        if (root.TryGetProperty("url", out var urlProp))
        {
            return urlProp.GetString() ?? throw new Exception("No URL in response");
        }

        throw new Exception($"Unexpected response shape: {responseBody}");
    }

    public async Task<List<string>> SplitTextIntoScenes(string userText, int pageCount)
    {
        var prompt = $@"Here is a story someone wrote:

""{userText}""

Break this story into exactly {pageCount} pages for a children's storybook. Each page 
MUST end at the close of a complete sentence — never cut off mid-sentence. Distribute 
the story as evenly as possible across the {pageCount} pages. Don't summarize or 
shorten the story — every part of the original text should be represented across the 
pages.
Preserve the original story, characters, and plot exactly as written — do not invent 
new events or change what happens.
You may lightly smooth the wording for flow and readability where needed, but stay 
faithful to the original text's content and meaning.
Respond with ONLY a JSON array of strings, one string per page, in order. No other 
text, no markdown formatting, just the raw JSON array.
Example format: [""Page 1 text here."", ""Page 2 text here.""]";

        var requestBody = new
        {
            model = "grok-4-0709",
            messages = new[]
            {
            new { role = "user", content = prompt }
        }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.x.ai/v1/chat/completions");
        request.Headers.Add("Authorization", $"Bearer {_apiKey}");
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            throw new Exception($"Grok API error ({response.StatusCode}): {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "[]";

        content = content.Trim();
        if (content.StartsWith("```"))
        {
            content = content.Substring(content.IndexOf('\n') + 1);
            content = content.Substring(0, content.LastIndexOf("```"));
        }

        var pages = JsonSerializer.Deserialize<List<string>>(content.Trim());
        return pages ?? new List<string>();
    }

    public List<string> SplitTextExact(string userText, int pageCount)
    {
        var sentences = System.Text.RegularExpressions.Regex
            .Matches(userText.Trim(), @"[^.!?]+[.!?]+(?:\s+|$)")
            .Select(m => m.Value.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();

        if (sentences.Count == 0) sentences = new List<string> { userText.Trim() };
        pageCount = Math.Max(1, Math.Min(pageCount, sentences.Count));

        var wordCounts = sentences
            .Select(s => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length)
            .ToList();
        var totalWords = wordCounts.Sum();
        var targetPerPage = (double)totalWords / pageCount;

        var pages = new List<string>();
        var currentPageSentences = new List<string>();
        var currentWordCount = 0;
        var pagesCreated = 0;

        for (int i = 0; i < sentences.Count; i++)
        {
            currentPageSentences.Add(sentences[i]);
            currentWordCount += wordCounts[i];

            var remainingSentences = sentences.Count - i - 1;
            var remainingPages = pageCount - pagesCreated - 1;

            if (remainingPages > 0 && currentWordCount >= targetPerPage && remainingSentences >= remainingPages)
            {
                pages.Add(string.Join(" ", currentPageSentences));
                currentPageSentences = new List<string>();
                currentWordCount = 0;
                pagesCreated++;
            }
        }

        if (currentPageSentences.Count > 0)
        {
            pages.Add(string.Join(" ", currentPageSentences));
        }

        return pages;
    }
    
}