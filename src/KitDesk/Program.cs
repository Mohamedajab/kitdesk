using System.Security.Claims;
using System.Threading.RateLimiting;
using KitDesk;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 16 * 1024);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<DeskDb>(options => options.UseNpgsql(
    builder.Configuration.GetConnectionString("Desk") ?? throw new InvalidOperationException("Set ConnectionStrings__Desk to a PostgreSQL connection string.")));
builder.Services.AddDataProtection().SetApplicationName("KitDesk").PersistKeysToDbContext<DeskDb>();
builder.Services.AddIdentity<Operator, IdentityRole>(options =>
{
    options.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_:";
}).AddEntityFrameworkStores<DeskDb>().AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "KitDesk.Session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing") ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.SlidingExpiration = false;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
    options.Events.OnValidatePrincipal = async context =>
    {
        var id = context.Principal?.FindFirstValue("workspace");
        var db = context.HttpContext.RequestServices.GetRequiredService<DeskDb>();
        var clock = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
        if (!Guid.TryParse(id, out var workspace) || !await db.Workspaces.AnyAsync(x => x.Id == workspace && x.ExpiresAt > clock.GetUtcNow()))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        }
    };
});
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.SecurePolicy = builder.Environment.IsProduction() ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
});
builder.Services.AddAuthorization(options => options.AddPolicy("Manager", policy => policy.RequireRole("Manager")));
builder.Services.AddScoped<LoanService>();
builder.Services.AddScoped<DemoService>();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await Results.Problem(statusCode: 429, title: "Too many requests. Try again in a minute.").ExecuteAsync(context.HttpContext);
    };
    options.AddPolicy("demo", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("Limits:DemoStartsPerMinute", 5), Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy("writes", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirstValue("workspace") ?? "anonymous", _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
});
var app = builder.Build();

if (args.Contains("--migrate"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<DeskDb>();
    await db.Database.MigrateAsync();
    var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    foreach (var role in new[] { "Manager", "Staff" })
        if (!await roles.RoleExistsAsync(role)) await roles.CreateAsync(new IdentityRole(role));
    return;
}

app.UseExceptionHandler();
if (app.Environment.IsProduction() && app.Configuration.GetValue<bool>("RENDER"))
    app.Use((context, next) =>
    {
        // Render redirects public HTTP to HTTPS before terminating TLS; do not trust forwarded client headers.
        context.Request.Scheme = "https";
        return next(context);
    });
app.UseStatusCodePages(async status =>
{
    if (status.HttpContext.Request.Path.StartsWithSegments("/api"))
        await Session.Problem(status.HttpContext.Response.StatusCode, "The request could not be completed.").ExecuteAsync(status.HttpContext);
});
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'";
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    if (context.Request.Path.StartsWithSegments("/api")) context.Response.Headers.CacheControl = "no-cache, no-store";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments("/api"))
    {
        try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException)
        {
            await Session.Problem(400, "Your form session changed. Refresh and try again.").ExecuteAsync(context);
            return;
        }
    }
    await next();
});
app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));
app.MapOpenApi();
app.MapGet("/api/session", async (HttpContext context, IAntiforgery antiforgery, DeskDb db) =>
{
    var token = antiforgery.GetAndStoreTokens(context).RequestToken;
    if (context.User.Identity?.IsAuthenticated != true) return Results.Ok(new { authenticated = false, csrfToken = token });
    var workspace = await db.Workspaces.AsNoTracking().SingleAsync(x => x.Id == Session.Workspace(context.User));
    return Results.Ok(new { authenticated = true, csrfToken = token, role = Session.Actor(context.User), workspace.ExpiresAt });
});
app.MapPost("/api/demo/start", (DemoService demos, CancellationToken ct) => demos.Start(ct)).RequireRateLimiting("demo");
app.MapPost("/api/demo/role", (ClaimsPrincipal user, SwitchRole input, DemoService demos, CancellationToken ct) => demos.Switch(user, input, ct)).RequireAuthorization().RequireRateLimiting("writes");
app.MapPost("/api/logout", async (HttpContext context) => { await context.SignOutAsync(IdentityConstants.ApplicationScheme); return Results.NoContent(); }).RequireAuthorization();
var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/equipment", async (ClaimsPrincipal user, DeskDb db, CancellationToken ct) => Results.Ok(await db.Equipment.AsNoTracking()
    .Where(x => x.WorkspaceId == Session.Workspace(user)).OrderBy(x => x.AssetTag)
    .Select(x => new { x.Id, x.AssetTag, x.Name, available = !db.Loans.Any(l => l.EquipmentId == x.Id && l.ReturnedAt == null) }).ToListAsync(ct)));
api.MapPost("/equipment", async (ClaimsPrincipal user, AddEquipment input, DeskDb db, CancellationToken ct) =>
{
    var tag = input.AssetTag?.Trim().ToUpperInvariant() ?? "";
    var name = input.Name?.Trim() ?? "";
    if (tag.Length is < 1 or > 24 || !tag.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') || name.Length is < 1 or > 100)
        return Session.Problem(400, "Use an asset tag of 1–24 letters, numbers or hyphens, and a name of 1–100 characters.");
    var workspace = Session.Workspace(user);
    await using var tx = await db.Database.BeginTransactionAsync(ct);
    await db.Workspaces.FromSqlInterpolated($"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {workspace} FOR UPDATE").SingleAsync(ct);
    if (await db.Equipment.CountAsync(x => x.WorkspaceId == workspace, ct) >= 50) return Session.Problem(409, "This demo has reached its 50-item limit.");
    var item = new Equipment { WorkspaceId = workspace, AssetTag = tag, Name = name };
    db.Equipment.Add(item);
    try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Results.Created($"/api/equipment/{item.Id}", new { item.Id }); }
    catch (DbUpdateException exception) when (Session.Unique(exception, "unique_asset_tag")) { return Session.Problem(409, "That asset tag is already in use in this workspace."); }
}).RequireAuthorization("Manager").RequireRateLimiting("writes");
api.MapGet("/equipment/{id:guid}", async (ClaimsPrincipal user, Guid id, DeskDb db, CancellationToken ct) =>
{
    var item = await db.Equipment.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == Session.Workspace(user) && x.Id == id, ct);
    return item is null ? Session.Problem(404, "Equipment not found in this workspace.") : Results.Ok(new { item.Id, item.AssetTag, item.Name });
});
api.MapGet("/loans", async (ClaimsPrincipal user, DeskDb db, TimeProvider clock, CancellationToken ct) => Results.Ok(await db.Loans.AsNoTracking()
    .Where(x => x.WorkspaceId == Session.Workspace(user)).OrderByDescending(x => x.IssuedAt)
    .Select(x => new { x.Id, x.EquipmentId, x.Equipment.AssetTag, x.Equipment.Name, x.Borrower, x.IssuedAt, x.DueDate, x.ReturnedAt, x.IssuedBy, x.ReturnedBy, overdue = x.ReturnedAt == null && x.DueDate < Session.Today(clock) }).ToListAsync(ct)));
api.MapPost("/loans", (ClaimsPrincipal user, IssueLoan input, LoanService loans, CancellationToken ct) => loans.Issue(user, input, ct)).RequireRateLimiting("writes");
api.MapGet("/loans/{id:guid}", async (ClaimsPrincipal user, Guid id, DeskDb db, CancellationToken ct) =>
{
    var loan = await db.Loans.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == Session.Workspace(user) && x.Id == id, ct);
    return loan is null ? Session.Problem(404, "Loan not found in this workspace.") : Results.Ok(new { loan.Id, loan.EquipmentId, loan.Borrower, loan.DueDate, loan.IssuedAt, loan.ReturnedAt, loan.IssuedBy, loan.ReturnedBy });
});
api.MapPost("/loans/{id:guid}/return", (ClaimsPrincipal user, Guid id, LoanService loans, CancellationToken ct) => loans.Return(user, id, ct)).RequireRateLimiting("writes");
app.MapFallback("/api/{**path}", () => Session.Problem(404, "API endpoint not found."));
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;
