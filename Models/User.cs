namespace StoryFunTimeApi.Models;

public class User
{
    public Guid Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool EmailVerified { get; set; } = false;
    public string? EmailVerificationToken { get; set; }
    public DateTime? EmailVerificationTokenExpiresAt { get; set; }
    public Guid? ReferredByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}