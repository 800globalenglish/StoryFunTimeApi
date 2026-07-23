using System.Text.Json.Serialization;

namespace StoryFunTimeApi.Models;

public class StoryTemplatePage
{
    public Guid Id { get; set; }
    public Guid StoryTemplateId { get; set; }
    [JsonIgnore]
    public StoryTemplate? StoryTemplate { get; set; }
    public int PageNumber { get; set; }
    public string TemplateText { get; set; } = string.Empty;
}
