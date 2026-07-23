using System.Text.Json.Serialization;

namespace StoryFunTimeApi.Models;

public class Character
{
    public Guid Id { get; set; }
    public Guid BookId { get; set; }

    [JsonIgnore]
    public Book? Book { get; set; }

    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty; // "boy", "girl", "man", "woman"
    public string AgeRange { get; set; } = string.Empty;
    public string? OriginalPhotoUrl { get; set; }
    public string? CartoonAvatarUrl { get; set; }
}