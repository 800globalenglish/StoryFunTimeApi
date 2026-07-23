using System.Text.Json.Serialization;

namespace StoryFunTimeApi.Models;

public class Page
{
    public Guid Id { get; set; }
    public Guid BookId { get; set; }

    [JsonIgnore]
    public Book? Book { get; set; }

    public int PageNumber { get; set; }
    public string ScriptText { get; set; } = string.Empty;
    public string? OriginalPhotoUrl { get; set; }
    public string? CartoonImageUrl { get; set; }
    public string? PreviousCartoonImageUrl { get; set; }
    public string? AudioUrl { get; set; }
}
