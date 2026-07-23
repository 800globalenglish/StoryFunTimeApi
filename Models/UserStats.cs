using System.ComponentModel.DataAnnotations;

namespace StoryFunTimeApi.Models;

public class UserStats
{
    [Key]
    public string UserId { get; set; } = "";
    public int TotalCharactersCreated { get; set; }
    public int TotalCharactersDeleted { get; set; }
}