namespace StoryFunTimeApi.Models;

public class StoryTemplate
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Theme { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public List<StoryTemplatePage> Pages { get; set; } = new();
}
