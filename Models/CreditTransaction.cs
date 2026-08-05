using StoryFunTimeApi.Models;

public class CreditTransaction
{
    public int Id { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string Type { get; set; } = "";          // "Purchase", "Deduction", "BonusGrant"
    public int CreditsDelta { get; set; }           // positive = granted, negative = spent
    public string Description { get; set; } = "";
    public string? StripeSessionId { get; set; }    // null for non-Stripe transactions (e.g. deductions)
    public DateTime CreatedAt { get; set; }
}