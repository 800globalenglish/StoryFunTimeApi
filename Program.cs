using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using StoryFunTimeApi.Data;
using StoryFunTimeApi.Models;
using StoryFunTimeApi.Services;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using static OwnershipHelpers;

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
builder.Services.AddHttpClient<EmailService>();

builder.Services.AddHttpClient<ReplicateService>();
builder.Services.AddSingleton<VideoService>();
builder.Services.AddSingleton<TranscriptionService>();

builder.Services.AddSingleton<PhotoFilterService>();

builder.Services.AddSingleton<PasswordHasher<User>>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"],
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"] ?? ""))
        };
    });
builder.Services.AddAuthorization();

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
app.UseCors("AllowFlutterApp");
app.UseAuthentication();
app.UseAuthorization();
app.UseStaticFiles();

// --- Uploaded file storage ---
// Everything the app writes for users - character photos, generated avatars,
// scenes, audio, videos - lives under one configurable root. Locally (or if
// unset) this defaults to wwwroot/uploads exactly like before. On the server,
// set "Storage:UploadsRootPath" in appsettings.json to a folder on a drive
// with real space (e.g. E:\StoryFunTimeUploads) so uploads stop filling up C:.
var uploadsRootConfig = app.Configuration["Storage:UploadsRootPath"];
var uploadsBasePath = string.IsNullOrWhiteSpace(uploadsRootConfig)
    ? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads")
    : uploadsRootConfig;
Directory.CreateDirectory(uploadsBasePath);

app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uploadsBasePath),
    RequestPath = "/uploads"
});

// Turns a stored URL like "/uploads/characters/xxx.png" into the real
// physical file path under uploadsBasePath, regardless of where that is.
string ResolveUploadPath(string url)
{
    var relative = url.TrimStart('/');
    if (relative.StartsWith("uploads/", StringComparison.OrdinalIgnoreCase))
        relative = relative["uploads/".Length..];
    return Path.Combine(uploadsBasePath, relative.Replace('/', Path.DirectorySeparatorChar));
}

app.UseHttpsRedirection();

// --- Books ---

// --- Auth ---

app.MapPost("/auth/signup", async (SignupRequest request, StoryFunTimeDbContext db, PasswordHasher<User> hasher, IConfiguration config, EmailService emailService) =>
{
    var email = request.Email.Trim().ToLowerInvariant();
    var username = request.Username.Trim();

    if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(request.Password))
        return Results.BadRequest(new { error = "Email and password are required." });
    if (string.IsNullOrWhiteSpace(username))
        return Results.BadRequest(new { error = "Username is required." });
    if (request.Password.Length < 6)
        return Results.BadRequest(new { error = "Password must be at least 6 characters." });

    if (await db.Users.AnyAsync(u => u.Email == email))
        return Results.Conflict(new { error = "An account with that email already exists." });
    if (await db.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower()))
        return Results.Conflict(new { error = "That username is already taken." });

    Guid? referredByUserId = null;
    User? referrer = null;
    if (!string.IsNullOrWhiteSpace(request.ReferredByUsername))
    {
        referrer = await db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == request.ReferredByUsername!.Trim().ToLower());
        if (referrer is not null) referredByUserId = referrer.Id;
    }

    var user = new User
    {
        Id = Guid.NewGuid(),
        Email = email,
        Username = username,
        ReferredByUserId = referredByUserId,
        CreatedAt = DateTime.UtcNow,
        EmailVerificationToken = Guid.NewGuid().ToString("N"),
        EmailVerificationTokenExpiresAt = DateTime.UtcNow.AddDays(3)
    };
    user.PasswordHash = hasher.HashPassword(user, request.Password);

    db.Users.Add(user);

    if (referrer is not null)
    {
        referrer.BonusCredits += 1;
    }

    await db.SaveChangesAsync();

    // Best-effort: don't fail signup if the verification email can't be sent.
    try
    {
        await emailService.SendVerificationEmailAsync(user.Email, user.Username, user.EmailVerificationToken!);
    }
    catch
    {
        // Swallow - the person can request a resend later from inside the app.
    }

    var token = JwtHelper.CreateToken(user, config);
    return Results.Created($"/users/{user.Id}", new
    {
        token,
        userId = user.Id,
        email = user.Email,
        username = user.Username,
        emailVerified = user.EmailVerified,
        createdAt = user.CreatedAt
    });
})
.WithName("Signup");

app.MapPost("/auth/login", async (LoginRequest request, StoryFunTimeDbContext db, PasswordHasher<User> hasher, IConfiguration config) =>
{
    var identifier = request.EmailOrUsername.Trim().ToLowerInvariant();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == identifier || u.Username.ToLower() == identifier);
    if (user is null)
        return Results.Json(new { error = "Invalid email/username or password." }, statusCode: 401);

    if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password) == PasswordVerificationResult.Failed)
        return Results.Json(new { error = "Invalid email/username or password." }, statusCode: 401);

    var token = JwtHelper.CreateToken(user, config);
    return Results.Ok(new
    {
        token,
        userId = user.Id,
        email = user.Email,
        username = user.Username,
        emailVerified = user.EmailVerified,
        createdAt = user.CreatedAt
    });
})
.WithName("Login");

app.MapPost("/auth/forgot-password", async (ForgotPasswordRequest request, StoryFunTimeDbContext db, EmailService emailService) =>
{
    var identifier = request.EmailOrUsername.Trim().ToLowerInvariant();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == identifier || u.Username.ToLower() == identifier);

    // Always return the same response whether or not an account was found,
    // so this endpoint can't be used to check which emails/usernames exist.
    if (user is not null)
    {
        user.PasswordResetToken = Guid.NewGuid().ToString("N");
        user.PasswordResetTokenExpiresAt = DateTime.UtcNow.AddHours(1);
        await db.SaveChangesAsync();

        try
        {
            await emailService.SendPasswordResetEmailAsync(user.Email, user.Username, user.PasswordResetToken);
        }
        catch
        {
            // Best-effort - don't reveal email delivery failures to the caller.
        }
    }

    return Results.Ok(new { message = "If an account exists, a password reset email has been sent." });
})
.WithName("ForgotPassword");

app.MapPost("/auth/reset-password", async (ResetPasswordRequest request, StoryFunTimeDbContext db, PasswordHasher<User> hasher) =>
{
    if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 6)
        return Results.BadRequest(new { error = "Password must be at least 6 characters." });

    var user = await db.Users.FirstOrDefaultAsync(u => u.PasswordResetToken == request.Token);
    if (user is null)
        return Results.BadRequest(new { error = "This reset link is invalid. Please request a new one." });

    if (user.PasswordResetTokenExpiresAt < DateTime.UtcNow)
        return Results.BadRequest(new { error = "This reset link has expired. Please request a new one." });

    user.PasswordHash = hasher.HashPassword(user, request.NewPassword);
    user.PasswordResetToken = null;
    user.PasswordResetTokenExpiresAt = null;
    await db.SaveChangesAsync();

    return Results.Ok(new { message = "Password updated successfully." });
})
.WithName("ResetPassword");

app.MapGet("/auth/verify-email", async (string token, StoryFunTimeDbContext db) =>
{
    var user = await db.Users.FirstOrDefaultAsync(u => u.EmailVerificationToken == token);
    var html = (string title, string message) => Results.Content(
        $"<html><body style='font-family:sans-serif;text-align:center;padding:60px;'><h2>{title}</h2><p>{message}</p></body></html>",
        "text/html");

    if (user is null)
        return html("Link not valid", "This verification link is invalid. You can request a new one from inside the app.");

    if (user.EmailVerificationTokenExpiresAt < DateTime.UtcNow)
        return html("Link expired", "This verification link has expired. You can request a new one from inside the app.");

    user.EmailVerified = true;
    user.EmailVerificationToken = null;
    user.EmailVerificationTokenExpiresAt = null;
    await db.SaveChangesAsync();

    return html("Email verified!", "Thanks - your email is confirmed. You can close this tab and go back to the app.");
})
.WithName("VerifyEmail");

app.MapPost("/auth/resend-verification", async (StoryFunTimeDbContext db, EmailService emailService, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();

    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
    if (user is null) return Results.NotFound();
    if (user.EmailVerified) return Results.Ok(new { message = "Email already verified." });

    user.EmailVerificationToken = Guid.NewGuid().ToString("N");
    user.EmailVerificationTokenExpiresAt = DateTime.UtcNow.AddDays(3);
    await db.SaveChangesAsync();

    try
    {
        await emailService.SendVerificationEmailAsync(user.Email, user.Username, user.EmailVerificationToken!);
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = $"Couldn't send email: {ex.Message}" }, statusCode: 500);
    }

    return Results.Ok(new { message = "Verification email sent." });
})
.RequireAuthorization()
.WithName("ResendVerification");

app.MapGet("/auth/me", async (StoryFunTimeDbContext db, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();

    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
    if (user is null) return Results.NotFound();

    return Results.Ok(new
    {
        userId = user.Id,
        email = user.Email,
        username = user.Username,
        emailVerified = user.EmailVerified,
        createdAt = user.CreatedAt,
        bonusCredits = user.BonusCredits
    });
})
.RequireAuthorization()
.WithName("GetCurrentUser");

app.MapGet("/users/me/referrals", async (StoryFunTimeDbContext db, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();

    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
    if (user is null) return Results.NotFound();

    var referrals = await db.Users
        .Where(u => u.ReferredByUserId == userId)
        .OrderByDescending(u => u.CreatedAt)
        .Select(u => new { username = u.Username, joinedAt = u.CreatedAt })
        .ToListAsync();

    return Results.Ok(new
    {
        bonusCredits = user.BonusCredits,
        referrals
    });
})
.RequireAuthorization()
.WithName("GetMyReferrals");

app.MapPost("/books", async (CreateBookRequest request, StoryFunTimeDbContext db, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();

    var book = new Book
    {
        Id = Guid.NewGuid(),
        UserId = userId.ToString()!,
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
.RequireAuthorization()
.WithName("CreateBook");

app.MapGet("/books/{id}", async (Guid id, StoryFunTimeDbContext db, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();
    if (!await UserOwnsBookAsync(id, userId.Value, db)) return Results.NotFound();

    var book = await db.Books
        .Include(b => b.Pages.OrderBy(p => p.PageNumber))
        .Include(b => b.Characters)
        .FirstOrDefaultAsync(b => b.Id == id);

    return book is not null ? Results.Ok(book) : Results.NotFound();
})
.RequireAuthorization()
.WithName("GetBook");

app.MapPut("/books/{id}", async (Guid id, UpdateBookRequest request, StoryFunTimeDbContext db, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();
    if (!await UserOwnsBookAsync(id, userId.Value, db)) return Results.NotFound();

    var book = await db.Books.FirstOrDefaultAsync(b => b.Id == id);
    if (book is null) return Results.NotFound();

    book.Title = request.Title;
    book.Theme = request.Theme;
    book.UpdatedAt = DateTime.UtcNow;
    await db.SaveChangesAsync();

    return Results.Ok(book);
})
.RequireAuthorization()
.WithName("UpdateBook");

app.MapPut("/pages/{id}", async (Guid id, UpdatePageTextRequest request, StoryFunTimeDbContext db, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();
    if (!await UserOwnsPageAsync(id, userId.Value, db)) return Results.NotFound();

    var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == id);
    if (page is null) return Results.NotFound($"Page {id} not found");

    page.ScriptText = request.ScriptText;
    await db.SaveChangesAsync();

    return Results.Ok(page);
})
.RequireAuthorization()
.WithName("UpdatePageText");

app.MapPost("/pages/{id}/regenerate-text", async (Guid id, RegenerateTextRequest? request, StoryFunTimeDbContext db, GrokService grok, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();
    if (!await UserOwnsPageAsync(id, userId.Value, db)) return Results.NotFound();

    var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == id);
    if (page is null) return Results.NotFound($"Page {id} not found");

    var book = await db.Books.FirstOrDefaultAsync(b => b.Id == page.BookId);
    if (book is null) return Results.NotFound($"Book for page {id} not found");

    var characters = await db.Characters.Where(c => c.BookId == page.BookId).ToListAsync();
    var characterDescriptions = characters.Select(c => $"{c.Name} ({c.Role})").ToList();

    try
    {
        var newPages = await grok.GenerateStoryPages(book.Title, book.Theme, 1, characterDescriptions, request?.ExtraInstructions);
        page.ScriptText = newPages.FirstOrDefault() ?? page.ScriptText;
        await db.SaveChangesAsync();

        return Results.Ok(page);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Failed to regenerate text: {ex.Message}");
    }
})
.RequireAuthorization()
.WithName("RegeneratePageText");

app.MapDelete("/books/{id}/pages", async (Guid id, StoryFunTimeDbContext db, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();
    if (!await UserOwnsBookAsync(id, userId.Value, db)) return Results.NotFound();

    var pages = await db.Pages.Where(p => p.BookId == id).ToListAsync();
    db.Pages.RemoveRange(pages);
    await db.SaveChangesAsync();

    return Results.NoContent();
})
.RequireAuthorization()
.WithName("DeleteAllPagesForBook");

app.MapPost("/pages/{id}/revert-scene", async (Guid id, StoryFunTimeDbContext db, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();
    if (!await UserOwnsPageAsync(id, userId.Value, db)) return Results.NotFound();

    var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == id);
    if (page is null) return Results.NotFound($"Page {id} not found");
    if (page.PreviousCartoonImageUrl is null) return Results.BadRequest("No previous scene to revert to");

    (page.CartoonImageUrl, page.PreviousCartoonImageUrl) = (page.PreviousCartoonImageUrl, page.CartoonImageUrl);
    await db.SaveChangesAsync();

    return Results.Ok(page);
})
.RequireAuthorization()
.WithName("RevertPageScene");

app.MapDelete("/books/{id}", async (Guid id, StoryFunTimeDbContext db, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();
    if (!await UserOwnsBookAsync(id, userId.Value, db)) return Results.NotFound();

    var book = await db.Books.FirstOrDefaultAsync(b => b.Id == id);
    if (book is null) return Results.NotFound($"Book {id} not found");

    db.Books.Remove(book);
    await db.SaveChangesAsync();

    return Results.NoContent();
})
.RequireAuthorization()
.WithName("DeleteBook");

app.MapGet("/books", async (StoryFunTimeDbContext db, HttpContext ctx) =>
{
    var userId = ctx.GetUserId()?.ToString();
    if (userId is null) return Results.Unauthorized();

    var books = await db.Books
        .Where(b => b.UserId == userId && !b.IsLibrary)
        .Include(b => b.Characters)
        .OrderByDescending(b => b.CreatedAt)
        .ToListAsync();

    return Results.Ok(books);
})
.RequireAuthorization()
.WithName("GetBooksForUser");

// --- Pages ---

app.MapPost("/books/{id}/pages", async (Guid id, CreatePageRequest request, StoryFunTimeDbContext db, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();
    if (!await UserOwnsBookAsync(id, userId.Value, db)) return Results.NotFound($"Book {id} not found");

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
.RequireAuthorization()
.WithName("AddPageToBook");

// --- Audio Upload ---
// --- Characters ---

app.MapPost("/books/{id}/characters", async (Guid id, HttpRequest request, StoryFunTimeDbContext db, ReplicateService replicate, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();

    var book = await db.Books.FirstOrDefaultAsync(b => b.Id == id);
    if (book is null) return Results.NotFound($"Book {id} not found");
    if (book.UserId != userId.Value.ToString()) return Results.NotFound($"Book {id} not found");
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
    var uploadsDir = Path.Combine(uploadsBasePath, "characters");
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
.RequireAuthorization()
.WithName("AddCharacter");

app.MapGet("/books/{id}/characters", async (Guid id, StoryFunTimeDbContext db, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();
    if (!await UserOwnsBookAsync(id, userId.Value, db)) return Results.NotFound();

    var characters = await db.Characters.Where(c => c.BookId == id).ToListAsync();
    return Results.Ok(characters);
})
.RequireAuthorization()
.WithName("GetCharactersForBook");

app.MapDelete("/characters/{id}", async (Guid id, StoryFunTimeDbContext db, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();
    if (!await UserOwnsCharacterAsync(id, userId.Value, db)) return Results.NotFound();

    var character = await db.Characters.FirstOrDefaultAsync(c => c.Id == id);
    if (character is null) return Results.NotFound($"Character {id} not found");

    db.Characters.Remove(character);
    await db.SaveChangesAsync();

    return Results.NoContent();
})
.RequireAuthorization()
.WithName("DeleteCharacter");

app.MapPost("/characters/{id}/regenerate-avatar", async (Guid id, RegenerateAvatarRequest? request, StoryFunTimeDbContext db, ReplicateService replicate, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();
    if (!await UserOwnsCharacterAsync(id, userId.Value, db)) return Results.NotFound();

    var character = await db.Characters.FirstOrDefaultAsync(c => c.Id == id);
    if (character is null) return Results.NotFound($"Character {id} not found");
    if (character.OriginalPhotoUrl is null) return Results.BadRequest("No original photo to regenerate from");
    try
    {
        var originalPath = ResolveUploadPath(character.OriginalPhotoUrl);
        var imageBytes = await File.ReadAllBytesAsync(originalPath);
        var cartoonUrl = await replicate.GenerateAvatarWithNanoBanana(imageBytes, "image/jpeg", character.Gender, character.Role, character.AgeRange, request?.ExtraInstructions);

        using var httpClient = new HttpClient();
        var cartoonBytes = await httpClient.GetByteArrayAsync(cartoonUrl);

        var uploadsDir = Path.Combine(uploadsBasePath, "characters");
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
.RequireAuthorization()
.WithName("RegenerateCharacterAvatar");



// --- Photo Upload + Cartoonize ---

app.MapPost("/pages/{id}/photo", async (Guid id, HttpRequest request, StoryFunTimeDbContext db, GrokService grok, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();
    if (!await UserOwnsPageAsync(id, userId.Value, db)) return Results.NotFound($"Page {id} not found");

    var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == id);
    if (page is null) return Results.NotFound($"Page {id} not found");

    if (!request.HasFormContentType) return Results.BadRequest("Expected form data");

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("photo");
    if (file is null || file.Length == 0) return Results.BadRequest("No photo file provided");

    var uploadsDir = Path.Combine(uploadsBasePath, "photos");
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
.RequireAuthorization()
.WithName("UploadPagePhoto");

app.MapPost("/pages/{id}/audio", async (Guid id, HttpRequest request, StoryFunTimeDbContext db, TranscriptionService transcriptionService, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();
    if (!await UserOwnsPageAsync(id, userId.Value, db)) return Results.NotFound($"Page {id} not found");

    var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == id);
    if (page is null) return Results.NotFound($"Page {id} not found");

    if (!request.HasFormContentType) return Results.BadRequest("Expected form data");

    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("audio");
    if (file is null || file.Length == 0) return Results.BadRequest("No audio file provided");

    var uploadsDir = Path.Combine(uploadsBasePath, "audio");
    Directory.CreateDirectory(uploadsDir);

    var fileName = $"{id}.webm";
    var filePath = Path.Combine(uploadsDir, fileName);

    using (var stream = new FileStream(filePath, FileMode.Create))
    {
        await file.CopyToAsync(stream);
    }

    page.AudioUrl = $"/uploads/audio/{fileName}";

    try
    {
        var transcribedText = await transcriptionService.Transcribe(filePath);
        if (!string.IsNullOrWhiteSpace(transcribedText))
        {
            page.ScriptText = transcribedText;
        }
    }
    catch (Exception transcribeEx)
    {
        Console.WriteLine($"[Transcription] FAILED: {transcribeEx.Message}");
    }
    await db.SaveChangesAsync();

    return Results.Ok(page);
})
.RequireAuthorization()
.WithName("UploadPageAudio");

// --- Scene Generation ---

app.MapPost("/pages/{id}/generate-scene", async (Guid id, GenerateSceneRequest? request, StoryFunTimeDbContext db, ReplicateService replicate, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();
    if (!await UserOwnsPageAsync(id, userId.Value, db)) return Results.NotFound($"Page {id} not found");

    var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == id);
    if (page is null) return Results.NotFound($"Page {id} not found");

    var book = await db.Books.FirstOrDefaultAsync(b => b.Id == page.BookId);

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
            var avatarPath = ResolveUploadPath(character.CartoonAvatarUrl!);
            var bytes = await File.ReadAllBytesAsync(avatarPath);
            avatarImages.Add((bytes, "image/jpeg", character.Name, character.Gender));
        }

        var sceneUrl = await replicate.GenerateSceneWithCharacters(avatarImages, page.ScriptText, book?.Theme, request?.ExtraInstructions);

        using var httpClient = new HttpClient();
        var sceneBytes = await httpClient.GetByteArrayAsync(sceneUrl);

        var uploadsDir = Path.Combine(uploadsBasePath, "scenes");
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
.RequireAuthorization()
.WithName("GenerateSceneForPage");

// --- Story Generation ---

app.MapPost("/books/{id}/generate-script", async (Guid id, GenerateScriptRequest request, StoryFunTimeDbContext db, GrokService grok, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();

    var book = await db.Books.FirstOrDefaultAsync(b => b.Id == id);
    if (book is null) return Results.NotFound($"Book {id} not found");
    if (book.UserId != userId.Value.ToString()) return Results.NotFound($"Book {id} not found");

    var characters = await db.Characters.Where(c => c.BookId == id).ToListAsync();
    var characterDescriptions = characters.Select(c => $"{c.Name} ({c.Role})").ToList();

    var pageCount = request.PageCount ?? 5;

    if (!string.IsNullOrWhiteSpace(request.Title)) book.Title = request.Title;
    if (!string.IsNullOrWhiteSpace(request.Theme)) book.Theme = request.Theme;
    await db.SaveChangesAsync();

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
.RequireAuthorization()
.WithName("GenerateScript");

// --- sample endpoint, left as-is for now ---

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast = Enumerable.Range(1, 5).Select(index =>
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

app.MapGet("/characters/{id}/avatar-history", async (Guid id, StoryFunTimeDbContext db, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();
    if (!await UserOwnsCharacterAsync(id, userId.Value, db)) return Results.NotFound();

    var history = await db.AvatarHistory
        .Where(h => h.CharacterId == id)
        .OrderByDescending(h => h.CreatedAt)
        .ToListAsync();
    return Results.Ok(history);
})
.RequireAuthorization()
.WithName("GetAvatarHistory");

app.MapPost("/characters/{id}/select-avatar", async (Guid id, SelectAvatarRequest request, StoryFunTimeDbContext db, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();
    if (!await UserOwnsCharacterAsync(id, userId.Value, db)) return Results.NotFound();

    var character = await db.Characters.FirstOrDefaultAsync(c => c.Id == id);
    if (character is null) return Results.NotFound($"Character {id} not found");

    var belongsToCharacter = await db.AvatarHistory.AnyAsync(h => h.CharacterId == id && h.Url == request.Url);
    if (!belongsToCharacter) return Results.BadRequest("That avatar does not belong to this character");

    character.CartoonAvatarUrl = request.Url;
    await db.SaveChangesAsync();
    return Results.Ok(character);
})
.RequireAuthorization()
.WithName("SelectCharacterAvatar");

app.MapDelete("/characters/{id}/avatar-history/{historyId}", async (Guid id, Guid historyId, StoryFunTimeDbContext db, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();
    if (!await UserOwnsCharacterAsync(id, userId.Value, db)) return Results.NotFound();

    var history = await db.AvatarHistory.FirstOrDefaultAsync(h => h.Id == historyId && h.CharacterId == id);
    if (history is null) return Results.NotFound("Avatar history entry not found");

    var character = await db.Characters.FirstOrDefaultAsync(c => c.Id == id);
    if (character is not null && character.CartoonAvatarUrl == history.Url)
    {
        return Results.BadRequest("Can't delete the currently selected avatar. Choose a different one first.");
    }

    var uploadsDir = Path.Combine(uploadsBasePath, "characters");
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
.RequireAuthorization()
.WithName("DeleteAvatarHistoryEntry");

app.MapGet("/users/{userId}/stats", async (string userId, StoryFunTimeDbContext db, HttpContext ctx) =>
{
    var realUserId = ctx.GetUserId()?.ToString();
    if (realUserId is null) return Results.Unauthorized();

    var stats = await db.UserStats.FirstOrDefaultAsync(s => s.UserId == realUserId);
    return Results.Ok(new { userId = realUserId, totalCharactersCreated = stats?.TotalCharactersCreated ?? 0, totalCharactersDeleted = stats?.TotalCharactersDeleted ?? 0 });
})
.RequireAuthorization()
.WithName("GetUserStats");

app.MapGet("/users/{userId}/library-book", async (string userId, StoryFunTimeDbContext db, HttpContext ctx) =>
{
    var realUserId = ctx.GetUserId()?.ToString();
    if (realUserId is null) return Results.Unauthorized();

    var libraryBook = await db.Books.FirstOrDefaultAsync(b => b.UserId == realUserId && b.IsLibrary);
    if (libraryBook is null)
    {
        libraryBook = new Book
        {
            Id = Guid.NewGuid(),
            UserId = realUserId,
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
.RequireAuthorization()
.WithName("GetOrCreateLibraryBook");

app.MapGet("/users/{userId}/characters", async (string userId, StoryFunTimeDbContext db, HttpContext ctx) =>
{
    var realUserId = ctx.GetUserId()?.ToString();
    if (realUserId is null) return Results.Unauthorized();

    var characters = await db.Characters
        .Where(c => db.Books.Any(b => b.Id == c.BookId && b.UserId == realUserId))
        .ToListAsync();
    return Results.Ok(characters);
})
.RequireAuthorization()
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

app.MapPost("/books/{id}/apply-template/{templateId}", async (Guid id, Guid templateId, ApplyTemplateRequest request, StoryFunTimeDbContext db, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();

    var book = await db.Books.FirstOrDefaultAsync(b => b.Id == id);
    if (book is null) return Results.NotFound($"Book {id} not found");
    if (book.UserId != userId.Value.ToString()) return Results.NotFound($"Book {id} not found");

    var template = await db.StoryTemplates.Include(t => t.Pages).FirstOrDefaultAsync(t => t.Id == templateId);
    if (template is null) return Results.NotFound($"Template {templateId} not found");

    var characterIds = request.RoleToCharacterId.Values.Distinct().ToList();
    var characters = await db.Characters.Where(c => characterIds.Contains(c.Id)).ToListAsync();
    var characterNamesById = characters.ToDictionary(c => c.Id, c => c.Name);

    // A book represents one story at a time - clear any existing pages before applying a template
    var existingPages = await db.Pages.Where(p => p.BookId == id).ToListAsync();
    db.Pages.RemoveRange(existingPages);


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
.RequireAuthorization()
.WithName("ApplyStoryTemplate");

app.MapPost("/books/{id}/generate-from-recording", async (Guid id, HttpRequest request, StoryFunTimeDbContext db, TranscriptionService transcriptionService, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();

    var book = await db.Books.FirstOrDefaultAsync(b => b.Id == id);
    if (book is null) return Results.NotFound($"Book {id} not found");
    if (book.UserId != userId.Value.ToString()) return Results.NotFound($"Book {id} not found");

    if (!request.HasFormContentType) return Results.BadRequest("Expected form data");
    var form = await request.ReadFormAsync();
    var file = form.Files.GetFile("audio");
    if (file is null || file.Length == 0) return Results.BadRequest("No audio file provided");

    // Save the whole-story recording to a temp location - it only needs to exist
    // long enough to transcribe and cut apart, unlike per-page audio which is kept.
    var tempDir = "temp_recordings";
    Directory.CreateDirectory(tempDir);
    var tempFilePath = Path.Combine(tempDir, $"{Guid.NewGuid()}.webm");
    using (var stream = new FileStream(tempFilePath, FileMode.Create))
    {
        await file.CopyToAsync(stream);
    }

    List<TranscriptSegment> segments;
    try
    {
        segments = await transcriptionService.TranscribeWithTimestamps(tempFilePath);
    }
    catch (Exception transcribeEx)
    {
        Console.WriteLine($"[GenerateFromRecording] Transcription FAILED: {transcribeEx.Message}");
        File.Delete(tempFilePath);
        return Results.BadRequest($"TEMP DEBUG: {transcribeEx.Message}"); // remove after diagnosing
    }

    if (segments.Count == 0)
    {
        File.Delete(tempFilePath);
        return Results.BadRequest("No speech detected in the recording.");
    }

    // NEW - decode once to a reliably seekable WAV before cutting any pages
    string wavPath;
    try
    {
        wavPath = await transcriptionService.DecodeToWav(tempFilePath);
    }
    catch (Exception decodeEx)
    {
        Console.WriteLine($"[GenerateFromRecording] WAV decode FAILED: {decodeEx.Message}");
        File.Delete(tempFilePath);
        return Results.BadRequest("Could not process the recording. Please try again.");
    }

    var pageGroups = transcriptionService.GroupIntoPages(segments);

    // A book represents one story at a time - clear any existing pages, same as apply-template
    var existingPages = await db.Pages.Where(p => p.BookId == id).ToListAsync();
    db.Pages.RemoveRange(existingPages);

    var uploadsDir = Path.Combine(uploadsBasePath, "audio");
    Directory.CreateDirectory(uploadsDir);

    var newPages = new List<Page>();
    var pageNumber = 1;
    foreach (var group in pageGroups)
    {
        var page = new Page
        {
            Id = Guid.NewGuid(),
            BookId = id,
            PageNumber = pageNumber,
            ScriptText = group.Text
        };

        var audioFileName = $"{page.Id}.webm";
        var audioFilePath = Path.Combine(uploadsDir, audioFileName);
        try
        {
            // CHANGED - cutting from wavPath now, not tempFilePath
            await transcriptionService.CutAudioSegment(wavPath, group.Start, group.End, audioFilePath);
            page.AudioUrl = $"/uploads/audio/{audioFileName}";
        }
        catch (Exception cutEx)
        {
            Console.WriteLine($"[GenerateFromRecording] Audio cut FAILED for page {pageNumber}: {cutEx.Message}");
        }

        db.Pages.Add(page);
        newPages.Add(page);
        pageNumber++;
    }

    await db.SaveChangesAsync();
    File.Delete(tempFilePath);
    File.Delete(wavPath); // NEW - clean up the intermediate WAV too

    return Results.Ok(newPages);
})
.RequireAuthorization()
.WithName("GenerateFromRecording");

app.MapPost("/books/{id}/generate-video", async (Guid id, StoryFunTimeDbContext db, VideoService videoService, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();

    var book = await db.Books.FirstOrDefaultAsync(b => b.Id == id);
    if (book is null) return Results.NotFound($"Book {id} not found");
    if (book.UserId != userId.Value.ToString()) return Results.NotFound($"Book {id} not found");

    var pages = await db.Pages.Where(p => p.BookId == id).OrderBy(p => p.PageNumber).ToListAsync();
    if (pages.Count == 0) return Results.BadRequest("This book has no pages yet.");

    var missing = new List<string>();
    var pageInputs = new List<(int PageNumber, string ImagePath, string AudioPath)>();

    foreach (var page in pages)
    {
        if (page.CartoonImageUrl is null || page.AudioUrl is null)
        {
            missing.Add($"Page {page.PageNumber}");
            continue;
        }
        pageInputs.Add((
            page.PageNumber,
            ResolveUploadPath(page.CartoonImageUrl),
            ResolveUploadPath(page.AudioUrl)
        ));
    }

    if (missing.Count > 0)
    {
        return Results.BadRequest($"These pages are missing a scene image and/or voice recording, so the video can't be made yet: {string.Join(", ", missing)}");
    }

    try
    {
        var outputDir = Path.Combine(uploadsBasePath, "videos");
        var finalPath = await videoService.GenerateBookVideo(pageInputs, outputDir, id.ToString());

        book.VideoUrl = $"/uploads/videos/{id}.mp4";
        await db.SaveChangesAsync();

        return Results.Ok(book);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Video generation failed: {ex.Message}");
    }
})
.RequireAuthorization()
.WithName("GenerateBookVideo");

app.MapGet("/books/{id}/video/download", async (Guid id, StoryFunTimeDbContext db) =>
{
    var book = await db.Books.FirstOrDefaultAsync(b => b.Id == id);
    if (book is null || book.VideoUrl is null) return Results.NotFound();

    var filePath = ResolveUploadPath(book.VideoUrl);
    if (!File.Exists(filePath)) return Results.NotFound();

    var safeTitle = string.Concat(book.Title.Split(Path.GetInvalidFileNameChars()));
    var downloadName = string.IsNullOrWhiteSpace(safeTitle) ? $"{id}.mp4" : $"{safeTitle}.mp4";

    return Results.File(filePath, "video/mp4", fileDownloadName: downloadName);
})
.WithName("DownloadBookVideo");

app.MapPost("/books/{id}/characters/copy", async (Guid id, CopyCharactersRequest request, StoryFunTimeDbContext db, HttpContext ctx) =>
{
    var userId = ctx.GetUserId();
    if (userId is null) return Results.Unauthorized();

    var book = await db.Books.FirstOrDefaultAsync(b => b.Id == id);
    if (book is null) return Results.NotFound($"Book {id} not found");
    if (book.UserId != userId.Value.ToString()) return Results.NotFound($"Book {id} not found");

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
.RequireAuthorization()
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

record CreateBookRequest(string Title, string Theme);
record UpdateBookRequest(string Title, string Theme);
record CreatePageRequest(int PageNumber, string ScriptText, string? OriginalPhotoUrl, string? CartoonImageUrl, string? AudioUrl);
record GenerateScriptRequest(int? PageCount, string? Title, string? Theme);

record UpdatePageTextRequest(string ScriptText);

record RegenerateAvatarRequest(string? ExtraInstructions);

record GenerateSceneRequest(string? ExtraInstructions);

record SelectAvatarRequest(string Url);
record CopyCharactersRequest(List<Guid> CharacterIds);
record RegenerateTextRequest(string? ExtraInstructions);
record CreateStoryTemplateRequest(string Title, string Theme);
record AddTemplatePageRequest(int PageNumber, string TemplateText);
record UpdateTemplatePageRequest(string TemplateText);
record ApplyTemplateRequest(Dictionary<string, Guid> RoleToCharacterId);

static class UserExtensions
{
    public static Guid? GetUserId(this HttpContext context)
    {
        var sub = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                  ?? context.User.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }
}

static class OwnershipHelpers
{
    public static async Task<bool> UserOwnsBookAsync(Guid bookId, Guid userId, StoryFunTimeDbContext db)
        => await db.Books.AnyAsync(b => b.Id == bookId && b.UserId == userId.ToString());

    public static async Task<bool> UserOwnsPageAsync(Guid pageId, Guid userId, StoryFunTimeDbContext db)
    {
        var page = await db.Pages.FirstOrDefaultAsync(p => p.Id == pageId);
        return page is not null && await UserOwnsBookAsync(page.BookId, userId, db);
    }

    public static async Task<bool> UserOwnsCharacterAsync(Guid characterId, Guid userId, StoryFunTimeDbContext db)
    {
        var character = await db.Characters.FirstOrDefaultAsync(c => c.Id == characterId);
        return character is not null && await UserOwnsBookAsync(character.BookId, userId, db);
    }
}
static class JwtHelper
{
    public static string CreateToken(User user, IConfiguration config)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("username", user.Username),
        };
        var token = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"],
            audience: config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddDays(double.Parse(config["Jwt:ExpiryDays"] ?? "30")),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

record SignupRequest(string Email, string Username, string Password, string? ReferredByUsername);
record LoginRequest(string EmailOrUsername, string Password);
record ForgotPasswordRequest(string EmailOrUsername);
record ResetPasswordRequest(string Token, string NewPassword);