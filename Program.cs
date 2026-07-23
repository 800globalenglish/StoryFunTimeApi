using Microsoft.EntityFrameworkCore;
using StoryFunTimeApi.Data;
using StoryFunTimeApi.Models;
using StoryFunTimeApi.Services;
using Microsoft.AspNetCore.Http;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddDbContext<StoryFunTimeDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFlutterApp", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

builder.Services.AddHttpClient<GrokService>();

builder.Services.AddHttpClient<ReplicateService>();

builder.Services.AddSingleton<PhotoFilterService>();

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
app.UseCors("AllowFlutterApp");
app.UseStaticFiles();
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
        .Include(b => b.Characters)
        .FirstOrDefaultAsync(b => b.Id == id);

    return book is not null ? Results.Ok(book) : Results.NotFound();
})
.WithName("GetBook");

app.MapPut("/pages/{id}", async (Guid id, UpdatePageTextRequest request, StoryFunTimeDbContext db) =>
{
    var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == id);
    if (page is null) return Results.NotFound($"Page {id} not found");

    page.ScriptText = request.ScriptText;
    await db.SaveChangesAsync();

    return Results.Ok(page);
})
.WithName("UpdatePageText");

app.MapPost("/pages/{id}/regenerate-text", async (Guid id, StoryFunTimeDbContext db, GrokService grok) =>
{
    var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == id);
    if (page is null) return Results.NotFound($"Page {id} not found");

    var book = await db.Books.FirstOrDefaultAsync(b => b.Id == page.BookId);
    if (book is null) return Results.NotFound($"Book for page {id} not found");

    var characters = await db.Characters.Where(c => c.BookId == page.BookId).ToListAsync();
    var characterDescriptions = characters.Select(c => $"{c.Name} ({c.Role})").ToList();

    try
    {
        var newPages = await grok.GenerateStoryPages(book.Title, book.Theme, 1, characterDescriptions);
        page.ScriptText = newPages.FirstOrDefault() ?? page.ScriptText;
        await db.SaveChangesAsync();

        return Results.Ok(page);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to regenerate text: {ex.Message}");
    }
})
.WithName("RegeneratePageText");

app.MapDelete("/books/{id}/pages", async (Guid id, StoryFunTimeDbContext db) =>
{
    var pages = await db.Pages.Where(p => p.BookId == id).ToListAsync();
    db.Pages.RemoveRange(pages);
    await db.SaveChangesAsync();

    return Results.NoContent();
})
.WithName("DeleteAllPagesForBook");

app.MapPost("/pages/{id}/revert-scene", async (Guid id, StoryFunTimeDbContext db) =>
{
    var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == id);
    if (page is null) return Results.NotFound($"Page {id} not found");
    if (page.PreviousCartoonImageUrl is null) return Results.BadRequest("No previous scene to revert to");

    (page.CartoonImageUrl, page.PreviousCartoonImageUrl) = (page.PreviousCartoonImageUrl, page.CartoonImageUrl);
    await db.SaveChangesAsync();

    return Results.Ok(page);
})
.WithName("RevertPageScene");

app.MapDelete("/books/{id}", async (Guid id, StoryFunTimeDbContext db) =>
{
    var book = await db.Books.FirstOrDefaultAsync(b => b.Id == id);
    if (book is null) return Results.NotFound($"Book {id} not found");

    db.Books.Remove(book);
    await db.SaveChangesAsync();

    return Results.NoContent();
})
.WithName("DeleteBook");

app.MapGet("/books", async (string userId, StoryFunTimeDbContext db) =>
{
    var books = await db.Books
        .Where(b => b.UserId == userId && !b.IsLibrary)
        .Include(b => b.Characters)
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

// --- Audio Upload ---
// --- Characters ---

app.MapPost("/books/{id}/characters", async (Guid id, HttpRequest request, StoryFunTimeDbContext db, ReplicateService replicate) =>
{
    var book = await db.Books.FirstOrDefaultAsync(b => b.Id == id);
    if (book is null) return Results.NotFound($"Book {id} not found");
    if (!request.HasFormContentType) return Results.BadRequest("Expected form data");
    var form = await request.ReadFormAsync();
    var name = form["name"].ToString();
    var role = form["role"].ToString();
    var gender = form["gender"].ToString();
    var ageRange = form["ageRange"].ToString();
    var extraInstructions = form["extraInstructions"].ToString();
    var file = form.Files.GetFile("photo");
    if (string.IsNullOrWhiteSpace(name)) return Results.BadRequest("Name is required");
    if (file is null || file.Length == 0) return Results.BadRequest("No photo file provided");
    var character = new Character
    {
        Id = Guid.NewGuid(),
        BookId = id,
        Name = name,
        Role = role,
        Gender = gender,
        AgeRange = ageRange
    };
    var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "characters");
    Directory.CreateDirectory(uploadsDir);
    var originalFileName = $"{character.Id}_original.jpg";
    var originalPath = Path.Combine(uploadsDir, originalFileName);
    using (var stream = new FileStream(originalPath, FileMode.Create))
    {
        await file.CopyToAsync(stream);
    }
    character.OriginalPhotoUrl = $"/uploads/characters/{originalFileName}";
    try
    {
        var imageBytes = await File.ReadAllBytesAsync(originalPath);
        var cartoonUrl = await replicate.GenerateAvatarWithNanoBanana(imageBytes, file.ContentType ?? "image/jpeg", gender, role, ageRange, extraInstructions);

        using var httpClient = new HttpClient();
        var cartoonBytes = await httpClient.GetByteArrayAsync(cartoonUrl);
        var cartoonFileName = $"{character.Id}_{Guid.NewGuid()}.jpg";
        var cartoonPath = Path.Combine(uploadsDir, cartoonFileName);
        await File.WriteAllBytesAsync(cartoonPath, cartoonBytes);
        character.CartoonAvatarUrl = $"/uploads/characters/{cartoonFileName}";

        db.AvatarHistory.Add(new CharacterAvatarHistory
        {
            Id = Guid.NewGuid(),
            CharacterId = character.Id,
            Url = $"/uploads/characters/{cartoonFileName}",
            CreatedAt = DateTime.UtcNow
        });

        var stats = await db.UserStats.FirstOrDefaultAsync(s => s.UserId == book.UserId);
        if (stats is null)
        {
            stats = new UserStats { UserId = book.UserId, TotalCharactersCreated = 1 };
            db.UserStats.Add(stats);
        }
        else
        {
            stats.TotalCharactersCreated++;
        }
    }
    catch (Exception ex)
    {
        db.Characters.Add(character);
        await db.SaveChangesAsync();
        return Results.Problem($"Photo saved, but cartoonizing avatar failed: {ex.Message}");
    }
    db.Characters.Add(character);
    await db.SaveChangesAsync();
    return Results.Created($"/books/{id}/characters/{character.Id}", character);
})
.WithName("AddCharacter");

app.MapGet("/books/{id}/characters", async (Guid id, StoryFunTimeDbContext db) =>
{
    var characters = await db.Characters.Where(c => c.BookId == id).ToListAsync();
    return Results.Ok(characters);
})
.WithName("GetCharactersForBook");

app.MapDelete("/characters/{id}", async (Guid id, StoryFunTimeDbContext db) =>
{
    var character = await db.Characters.FirstOrDefaultAsync(c => c.Id == id);
    if (character is null) return Results.NotFound($"Character {id} not found");

    db.Characters.Remove(character);
    await db.SaveChangesAsync();

    return Results.NoContent();
})
.WithName("DeleteCharacter");

app.MapPost("/characters/{id}/regenerate-avatar", async (Guid id, RegenerateAvatarRequest? request, StoryFunTimeDbContext db, ReplicateService replicate) =>
{
    var character = await db.Characters.FirstOrDefaultAsync(c => c.Id == id);
    if (character is null) return Results.NotFound($"Character {id} not found");
    if (character.OriginalPhotoUrl is null) return Results.BadRequest("No original photo to regenerate from");
    try
    {
        var originalPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", character.OriginalPhotoUrl.TrimStart('/'));
        var imageBytes = await File.ReadAllBytesAsync(originalPath);
        var cartoonUrl = await replicate.GenerateAvatarWithNanoBanana(imageBytes, "image/jpeg", character.Gender, character.Role, character.AgeRange, request?.ExtraInstructions);

        using var httpClient = new HttpClient();
        var cartoonBytes = await httpClient.GetByteArrayAsync(cartoonUrl);

        var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "characters");
        var cartoonFileName = $"{character.Id}_{Guid.NewGuid()}.jpg";
        var cartoonPath = Path.Combine(uploadsDir, cartoonFileName);
        await File.WriteAllBytesAsync(cartoonPath, cartoonBytes);
        var relativeUrl = $"/uploads/characters/{cartoonFileName}";

        character.CartoonAvatarUrl = relativeUrl;
        db.AvatarHistory.Add(new CharacterAvatarHistory
        {
            Id = Guid.NewGuid(),
            CharacterId = character.Id,
            Url = relativeUrl,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var regenBook = await db.Books.FirstOrDefaultAsync(b => b.Id == character.BookId);
        if (regenBook is not null)
        {
            var regenStats = await db.UserStats.FirstOrDefaultAsync(s => s.UserId == regenBook.UserId);
            if (regenStats is null)
            {
                regenStats = new UserStats { UserId = regenBook.UserId, TotalCharactersCreated = 1 };
                db.UserStats.Add(regenStats);
            }
            else
            {
                regenStats.TotalCharactersCreated++;
            }
            await db.SaveChangesAsync();
        }

        await TrimAvatarHistoryAsync(character.Id, db, uploadsDir);

        return Results.Ok(character);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to regenerate avatar: {ex.Message}");
    }
})
.WithName("RegenerateCharacterAvatar");



// --- Photo Upload + Cartoonize ---

app.MapPost("/pages/{id}/photo", async (Guid id, HttpRequest request, StoryFunTimeDbContext db, GrokService grok) =>
{
    var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == id);
    if (page is null) return Results.NotFound($"Page {id} not found");

    if (!request.HasFormContentType) return Results.BadRequest("Expected form data");

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("photo");
    if (file is null || file.Length == 0) return Results.BadRequest("No photo file provided");

    var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "photos");
    Directory.CreateDirectory(uploadsDir);

    // Save the original
    var originalFileName = $"{id}_original.jpg";
    var originalPath = Path.Combine(uploadsDir, originalFileName);
    using (var stream = new FileStream(originalPath, FileMode.Create))
    {
        await file.CopyToAsync(stream);
    }

    page.OriginalPhotoUrl = $"/uploads/photos/{originalFileName}";

    try
    {
        // Read the bytes back for sending to Grok
        var imageBytes = await File.ReadAllBytesAsync(originalPath);
        var cartoonUrl = await grok.CartoonizeImage(imageBytes, file.ContentType ?? "image/jpeg", "", "", "", "");

        // Download the cartoonized result and save it locally too
        using var httpClient = new HttpClient();
        var cartoonBytes = await httpClient.GetByteArrayAsync(cartoonUrl);
        var cartoonFileName = $"{id}_cartoon.jpg";
        var cartoonPath = Path.Combine(uploadsDir, cartoonFileName);
        await File.WriteAllBytesAsync(cartoonPath, cartoonBytes);

        page.CartoonImageUrl = $"/uploads/photos/{cartoonFileName}";
    }
    catch (Exception ex)
    {
        // Original photo is still saved even if cartoonizing fails
        await db.SaveChangesAsync();
        return Results.Problem($"Photo saved, but cartoonizing failed: {ex.Message}");
    }

    await db.SaveChangesAsync();
    return Results.Ok(page);
})
.WithName("UploadPagePhoto");

app.MapPost("/pages/{id}/audio", async (Guid id, HttpRequest request, StoryFunTimeDbContext db) =>
{
    var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == id);
    if (page is null) return Results.NotFound($"Page {id} not found");

    if (!request.HasFormContentType) return Results.BadRequest("Expected form data");

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("audio");
    if (file is null || file.Length == 0) return Results.BadRequest("No audio file provided");

    var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "audio");
    Directory.CreateDirectory(uploadsDir);

    var fileName = $"{id}.webm";
    var filePath = Path.Combine(uploadsDir, fileName);

    using (var stream = new FileStream(filePath, FileMode.Create))
    {
        await file.CopyToAsync(stream);
    }

    page.AudioUrl = $"/uploads/audio/{fileName}";
    await db.SaveChangesAsync();

    return Results.Ok(page);
})
.WithName("UploadPageAudio");

// --- Scene Generation ---

app.MapPost("/pages/{id}/generate-scene", async (Guid id, GenerateSceneRequest? request, StoryFunTimeDbContext db, ReplicateService replicate) =>
{
    var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == id);
    if (page is null) return Results.NotFound($"Page {id} not found");

    var characters = await db.Characters.Where(c => c.BookId == page.BookId).ToListAsync();
    var avatarsWithPhotos = characters.Where(c => c.CartoonAvatarUrl != null).ToList();

    if (avatarsWithPhotos.Count == 0)
    {
        return Results.BadRequest("No character avatars found for this book. Add characters with photos first.");
    }

    try
    {
        var avatarImages = new List<(byte[] Bytes, string ContentType, string Name, string Gender)>();
        foreach (var character in avatarsWithPhotos)
        {
            var avatarPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", character.CartoonAvatarUrl!.TrimStart('/'));
            var bytes = await File.ReadAllBytesAsync(avatarPath);
            avatarImages.Add((bytes, "image/jpeg", character.Name, character.Gender));
        }

        var sceneUrl = await replicate.GenerateSceneWithCharacters(avatarImages, page.ScriptText, request?.ExtraInstructions);

        using var httpClient = new HttpClient();
        var sceneBytes = await httpClient.GetByteArrayAsync(sceneUrl);

        var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "scenes");
        Directory.CreateDirectory(uploadsDir);

        var currentPath = Path.Combine(uploadsDir, $"{id}_scene.jpg");
        var previousPath = Path.Combine(uploadsDir, $"{id}_scene_previous.jpg");
        if (File.Exists(currentPath))
        {
            File.Copy(currentPath, previousPath, overwrite: true);
            page.PreviousCartoonImageUrl = $"/uploads/scenes/{id}_scene_previous.jpg";
        }

        await File.WriteAllBytesAsync(currentPath, sceneBytes);
        page.CartoonImageUrl = $"/uploads/scenes/{id}_scene.jpg";
        await db.SaveChangesAsync();

        return Results.Ok(page);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Scene generation failed: {ex.Message}");
    }
})
.WithName("GenerateSceneForPage");

// --- Story Generation ---

app.MapPost("/books/{id}/generate-script", async (Guid id, GenerateScriptRequest request, StoryFunTimeDbContext db, GrokService grok) =>
{
    var book = await db.Books.FirstOrDefaultAsync(b => b.Id == id);
    if (book is null) return Results.NotFound($"Book {id} not found");

    var characters = await db.Characters.Where(c => c.BookId == id).ToListAsync();
    var characterDescriptions = characters.Select(c => $"{c.Name} ({c.Role})").ToList();

    var pageCount = request.PageCount ?? 5;

    try
    {
        var pages = await grok.GenerateStoryPages(book.Title, book.Theme, pageCount, characterDescriptions);
        return Results.Ok(new { pages });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Story generation failed: {ex.Message}");
    }
})
.WithName("GenerateScript");

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

app.MapGet("/characters/{id}/avatar-history", async (Guid id, StoryFunTimeDbContext db) =>
{
    var history = await db.AvatarHistory
        .Where(h => h.CharacterId == id)
        .OrderByDescending(h => h.CreatedAt)
        .ToListAsync();
    return Results.Ok(history);
})
.WithName("GetAvatarHistory");

app.MapPost("/characters/{id}/select-avatar", async (Guid id, SelectAvatarRequest request, StoryFunTimeDbContext db) =>
{
    var character = await db.Characters.FirstOrDefaultAsync(c => c.Id == id);
    if (character is null) return Results.NotFound($"Character {id} not found");

    var belongsToCharacter = await db.AvatarHistory.AnyAsync(h => h.CharacterId == id && h.Url == request.Url);
    if (!belongsToCharacter) return Results.BadRequest("That avatar does not belong to this character");

    character.CartoonAvatarUrl = request.Url;
    await db.SaveChangesAsync();
    return Results.Ok(character);
})
.WithName("SelectCharacterAvatar");

app.MapDelete("/characters/{id}/avatar-history/{historyId}", async (Guid id, Guid historyId, StoryFunTimeDbContext db) =>
{
    var history = await db.AvatarHistory.FirstOrDefaultAsync(h => h.Id == historyId && h.CharacterId == id);
    if (history is null) return Results.NotFound("Avatar history entry not found");

    var character = await db.Characters.FirstOrDefaultAsync(c => c.Id == id);
    if (character is not null && character.CartoonAvatarUrl == history.Url)
    {
        return Results.BadRequest("Can't delete the currently selected avatar. Choose a different one first.");
    }

    var uploadsDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "characters");
    var fileName = Path.GetFileName(history.Url);
    var filePath = Path.Combine(uploadsDir, fileName);
    if (File.Exists(filePath))
    {
        File.Delete(filePath);
    }

    db.AvatarHistory.Remove(history);

    if (character is not null)
    {
        var book = await db.Books.FirstOrDefaultAsync(b => b.Id == character.BookId);
        if (book is not null)
        {
            var stats = await db.UserStats.FirstOrDefaultAsync(s => s.UserId == book.UserId);
            if (stats is null)
            {
                stats = new UserStats { UserId = book.UserId, TotalCharactersDeleted = 1 };
                db.UserStats.Add(stats);
            }
            else
            {
                stats.TotalCharactersDeleted++;
            }
        }
    }
    await db.SaveChangesAsync();
    return Results.NoContent();
})
.WithName("DeleteAvatarHistoryEntry");

app.MapGet("/users/{userId}/stats", async (string userId, StoryFunTimeDbContext db) =>
{
    var stats = await db.UserStats.FirstOrDefaultAsync(s => s.UserId == userId);
    return Results.Ok(new { userId, totalCharactersCreated = stats?.TotalCharactersCreated ?? 0, totalCharactersDeleted = stats?.TotalCharactersDeleted ?? 0 });
})
.WithName("GetUserStats");

app.MapGet("/users/{userId}/library-book", async (string userId, StoryFunTimeDbContext db) =>
{
    var libraryBook = await db.Books.FirstOrDefaultAsync(b => b.UserId == userId && b.IsLibrary);
    if (libraryBook is null)
    {
        libraryBook = new Book
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Title = "My Characters",
            Theme = "",
            Status = "library",
            IsLibrary = true
        };
        db.Books.Add(libraryBook);
        await db.SaveChangesAsync();
    }
    return Results.Ok(new { bookId = libraryBook.Id });
})
.WithName("GetOrCreateLibraryBook");

app.MapGet("/users/{userId}/characters", async (string userId, StoryFunTimeDbContext db) =>
{
    var characters = await db.Characters
        .Where(c => db.Books.Any(b => b.Id == c.BookId && b.UserId == userId))
        .ToListAsync();
    return Results.Ok(characters);
})
.WithName("GetAllCharactersForUser");

app.MapGet("/story-templates", async (StoryFunTimeDbContext db) =>
{
    var templates = await db.StoryTemplates
        .Include(t => t.Pages)
        .OrderBy(t => t.Title)
        .ToListAsync();
    return Results.Ok(templates);
})
.WithName("GetStoryTemplates");

app.MapPost("/story-templates", async (CreateStoryTemplateRequest request, StoryFunTimeDbContext db) =>
{
    var template = new StoryTemplate
    {
        Id = Guid.NewGuid(),
        Title = request.Title,
        Theme = request.Theme
    };
    db.StoryTemplates.Add(template);
    await db.SaveChangesAsync();
    return Results.Created($"/story-templates/{template.Id}", template);
})
.WithName("CreateStoryTemplate");

app.MapPost("/story-templates/{id}/pages", async (Guid id, AddTemplatePageRequest request, StoryFunTimeDbContext db) =>
{
    var template = await db.StoryTemplates.FirstOrDefaultAsync(t => t.Id == id);
    if (template is null) return Results.NotFound($"Template {id} not found");

    var page = new StoryTemplatePage
    {
        Id = Guid.NewGuid(),
        StoryTemplateId = id,
        PageNumber = request.PageNumber,
        TemplateText = request.TemplateText
    };
    db.StoryTemplatePages.Add(page);
    await db.SaveChangesAsync();
    return Results.Created($"/story-templates/{id}/pages/{page.Id}", page);
})
.WithName("AddTemplatePage");

app.MapPut("/story-template-pages/{id}", async (Guid id, UpdateTemplatePageRequest request, StoryFunTimeDbContext db) =>
{
    var page = await db.StoryTemplatePages.FirstOrDefaultAsync(p => p.Id == id);
    if (page is null) return Results.NotFound($"Template page {id} not found");

    page.TemplateText = request.TemplateText;
    await db.SaveChangesAsync();
    return Results.Ok(page);
})
.WithName("UpdateTemplatePage");

app.MapDelete("/story-template-pages/{id}", async (Guid id, StoryFunTimeDbContext db) =>
{
    var page = await db.StoryTemplatePages.FirstOrDefaultAsync(p => p.Id == id);
    if (page is null) return Results.NotFound($"Template page {id} not found");

    db.StoryTemplatePages.Remove(page);
    await db.SaveChangesAsync();
    return Results.NoContent();
})
.WithName("DeleteTemplatePage");

app.MapDelete("/story-templates/{id}", async (Guid id, StoryFunTimeDbContext db) =>
{
    var template = await db.StoryTemplates.FirstOrDefaultAsync(t => t.Id == id);
    if (template is null) return Results.NotFound($"Template {id} not found");

    var pages = await db.StoryTemplatePages.Where(p => p.StoryTemplateId == id).ToListAsync();
    db.StoryTemplatePages.RemoveRange(pages);
    db.StoryTemplates.Remove(template);
    await db.SaveChangesAsync();
    return Results.NoContent();
})
.WithName("DeleteStoryTemplate");

app.MapPost("/books/{id}/apply-template/{templateId}", async (Guid id, Guid templateId, ApplyTemplateRequest request, StoryFunTimeDbContext db) =>
{
    var book = await db.Books.FirstOrDefaultAsync(b => b.Id == id);
    if (book is null) return Results.NotFound($"Book {id} not found");

    var template = await db.StoryTemplates.Include(t => t.Pages).FirstOrDefaultAsync(t => t.Id == templateId);
    if (template is null) return Results.NotFound($"Template {templateId} not found");

    var characterIds = request.RoleToCharacterId.Values.Distinct().ToList();
    var characters = await db.Characters.Where(c => characterIds.Contains(c.Id)).ToListAsync();
    var characterNamesById = characters.ToDictionary(c => c.Id, c => c.Name);

    var newPages = new List<Page>();
    foreach (var templatePage in template.Pages.OrderBy(p => p.PageNumber))
    {
        var text = templatePage.TemplateText;
        foreach (var mapping in request.RoleToCharacterId)
        {
            if (characterNamesById.TryGetValue(mapping.Value, out var characterName))
            {
                text = text.Replace("{" + mapping.Key + "}", characterName);
            }
        }

        var page = new Page
        {
            Id = Guid.NewGuid(),
            BookId = id,
            PageNumber = templatePage.PageNumber,
            ScriptText = text
        };
        db.Pages.Add(page);
        newPages.Add(page);
    }

    await db.SaveChangesAsync();
    return Results.Ok(newPages);
})
.WithName("ApplyStoryTemplate");

app.MapPost("/books/{id}/characters/copy", async (Guid id, CopyCharactersRequest request, StoryFunTimeDbContext db) =>
{
    var book = await db.Books.FirstOrDefaultAsync(b => b.Id == id);
    if (book is null) return Results.NotFound($"Book {id} not found");

    var sourceCharacters = await db.Characters.Where(c => request.CharacterIds.Contains(c.Id)).ToListAsync();
    var newCharacters = new List<Character>();
    foreach (var source in sourceCharacters)
    {
        var copy = new Character
        {
            Id = Guid.NewGuid(),
            BookId = id,
            Name = source.Name,
            Role = source.Role,
            Gender = source.Gender,
            AgeRange = source.AgeRange,
            OriginalPhotoUrl = source.OriginalPhotoUrl,
            CartoonAvatarUrl = source.CartoonAvatarUrl
        };
        db.Characters.Add(copy);
        newCharacters.Add(copy);
    }
    await db.SaveChangesAsync();
    return Results.Ok(newCharacters);
})
.WithName("CopyCharactersToBook");

async Task TrimAvatarHistoryAsync(Guid characterId, StoryFunTimeDbContext db, string uploadsDir)
{
    var all = await db.AvatarHistory
        .Where(h => h.CharacterId == characterId)
        .OrderByDescending(h => h.CreatedAt)
        .ToListAsync();

    var toDelete = all.Skip(10).ToList();
    foreach (var old in toDelete)
    {
        var fileName = Path.GetFileName(old.Url);
        var filePath = Path.Combine(uploadsDir, fileName);
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
        db.AvatarHistory.Remove(old);
    }
    if (toDelete.Count > 0)
    {
        await db.SaveChangesAsync();
    }
}

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}

record CreateBookRequest(string UserId, string Title, string Theme);
record CreatePageRequest(int PageNumber, string ScriptText, string? OriginalPhotoUrl, string? CartoonImageUrl, string? AudioUrl);
record GenerateScriptRequest(int? PageCount);

record UpdatePageTextRequest(string ScriptText);

record RegenerateAvatarRequest(string? ExtraInstructions);

record GenerateSceneRequest(string? ExtraInstructions);

record SelectAvatarRequest(string Url);
record CopyCharactersRequest(List<Guid> CharacterIds);
record CreateStoryTemplateRequest(string Title, string Theme);
record AddTemplatePageRequest(int PageNumber, string TemplateText);
record UpdateTemplatePageRequest(string TemplateText);
record ApplyTemplateRequest(Dictionary<string, Guid> RoleToCharacterId);

