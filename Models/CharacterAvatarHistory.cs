namespace StoryFunTimeApi.Models;

public class CharacterAvatarHistory
{
    public Guid Id { get; set; }
    public Guid CharacterId { get; set; }
    public string Url { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}