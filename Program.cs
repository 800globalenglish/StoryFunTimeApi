using Microsoft.EntityFrameworkCore;
using StoryFunTimeApi.Data;
using StoryFunTimeApi.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddDbContext<StoryFunTimeDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// --- Books ---

app.MapPost("/books", async (CreateBookRequest request, StoryFunTimeDbContext db) =>
{
    var book = new Book
    {
        Id = Guid.NewGuid(),
        UserId = request.UserId,
        Title = request.Title,
        Theme = request.Theme,
        Status = "draft",
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    db.Books.Add(book);
    await db.SaveChangesAsync();

    return Results.Created($"/books/{book.Id}", book);
})
.WithName("CreateBook");

app.MapGet("/books/{id}", async (Guid id, StoryFunTimeDbContext db) =>
{
    var book = await db.Books
        .Include(b => b.Pages.OrderBy(p => p.PageNumber))
        .FirstOrDefaultAsync(b => b.Id == id);

    return book is not null ? Results.Ok(book) : Results.NotFound();
})
.WithName("GetBook");

app.MapGet("/books", async (string userId, StoryFunTimeDbContext db) =>
{
    var books = await db.Books
        .Where(b => b.UserId == userId)
        .OrderByDescending(b => b.CreatedAt)
        .ToListAsync();

    return Results.Ok(books);
})
.WithName("GetBooksForUser");

// --- Pages ---

app.MapPost("/books/{id}/pages", async (Guid id, CreatePageRequest request, StoryFunTimeDbContext db) =>
{
    var bookExists = await db.Books.AnyAsync(b => b.Id == id);
    if (!bookExists) return Results.NotFound($"Book {id} not found");

    var page = new Page
    {
        Id = Guid.NewGuid(),
        BookId = id,
        PageNumber = request.PageNumber,
        ScriptText = request.ScriptText,
        OriginalPhotoUrl = request.OriginalPhotoUrl,
        CartoonImageUrl = request.CartoonImageUrl,
        AudioUrl = request.AudioUrl
    };

    db.Pages.Add(page);
    await db.SaveChangesAsync();

    return Results.Created($"/books/{id}/pages/{page.Id}", page);
})
.WithName("AddPageToBook");

// --- sample endpoint, left as-is for now ---

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

record CreateBookRequest(string UserId, string Title, string Theme);
record CreatePageRequest(int PageNumber, string ScriptText, string? OriginalPhotoUrl, string? CartoonImageUrl, string? AudioUrl);
