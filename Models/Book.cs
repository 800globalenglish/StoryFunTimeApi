namespace StoryFunTimeApi.Models;

public class Book
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Theme { get; set; } = string.Empty;
    public string Status { get; set; } = "draft";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<Page> Pages { get; set; } = new();
    public List<Character> Characters { get; set; } = new();
}
