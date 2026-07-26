using Microsoft.EntityFrameworkCore;
using StoryFunTimeApi.Models;

namespace StoryFunTimeApi.Data;

public class StoryFunTimeDbContext : DbContext
{
    public StoryFunTimeDbContext(DbContextOptions<StoryFunTimeDbContext> options)
        : base(options) { }

    public DbSet<Book> Books => Set<Book>();
    public DbSet<Page> Pages => Set<Page>();
    public DbSet<Character> Characters => Set<Character>();
    public DbSet<CharacterAvatarHistory> AvatarHistory { get; set; }
    public DbSet<UserStats> UserStats { get; set; }
    public DbSet<StoryTemplate> StoryTemplates { get; set; }
    public DbSet<StoryTemplatePage> StoryTemplatePages { get; set; }
    public DbSet<User> Users => Set<User>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Email)
            .IsUnique();
    }
}