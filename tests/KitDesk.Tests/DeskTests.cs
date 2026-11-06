using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using KitDesk;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace KitDesk.Tests;

public sealed class Clock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class DeskFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string schema = "test_" + Guid.NewGuid().ToString("N");
    private readonly string connection = Environment.GetEnvironmentVariable("KITDESK_TEST_DATABASE")
        ?? throw new InvalidOperationException("KITDESK_TEST_DATABASE is required; database tests cannot be skipped.");
    public Clock Clock { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Limits:DemoStartsPerMinute", "1000");
        var scoped = new NpgsqlConnectionStringBuilder(connection) { SearchPath = schema };
        builder.UseSetting("ConnectionStrings:Desk", scoped.ConnectionString);
        builder.ConfigureServices(services => { services.RemoveAll<TimeProvider>(); services.AddSingleton<TimeProvider>(Clock); });
    }
    public async Task InitializeAsync()
    {
        await using var sql = new NpgsqlConnection(connection);
        await sql.OpenAsync();
        await new NpgsqlCommand($"CREATE SCHEMA {schema}", sql).ExecuteNonQueryAsync();
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DeskDb>().Database.MigrateAsync();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        foreach (var role in new[] { "Manager", "Staff" }) Assert.True((await roles.CreateAsync(new IdentityRole(role))).Succeeded);
    }
    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await using var sql = new NpgsqlConnection(connection);
        await sql.OpenAsync();
        // Only this fixture's newly created schema is disposable; never reset a supplied database.
        Assert.Matches("^test_[a-f0-9]{32}$", schema);
        await new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", sql).ExecuteNonQueryAsync();
    }
    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    public async Task<HttpClient> Demo()
    {
        var client = Client();
        var result = await Post(client, "/api/demo/start", new { });
        Assert.Equal(HttpStatusCode.Created, result.StatusCode);
        return client;
    }
    public static async Task<HttpResponseMessage> Post(HttpClient client, string path, object value)
    {
        var session = await client.GetFromJsonAsync<JsonObject>("/api/session");
        using var message = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(value) };
        message.Headers.Add("X-CSRF-TOKEN", session!["csrfToken"]!.GetValue<string>());
        return await client.SendAsync(message);
    }
    public static async Task<Guid> Available(HttpClient client)
    {
        var equipment = await client.GetFromJsonAsync<JsonArray>("/api/equipment");
        return equipment!.First(x => x!["available"]!.GetValue<bool>())!["id"]!.GetValue<Guid>();
    }
    public static object Issue(Guid id, string borrower = "Test borrower", DateOnly? due = null) =>
        new { equipmentId = id, borrower, dueDate = due ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2) };
}

public sealed class DeskTests(DeskFactory app) : IClassFixture<DeskFactory>
{
    [Fact]
    public async Task RenderTlsTerminationKeepsCookiesSecureAndAllowsDemo()
    {
        await using var hosted = app.WithWebHostBuilder(builder => builder.UseEnvironment("Production").UseSetting("RENDER", "true"));
        using var client = hosted.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "http");
        var session = await client.GetAsync("/api/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        Assert.All(session.Headers.GetValues("Set-Cookie"), cookie => Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase));
        var started = await DeskFactory.Post(client, "/api/demo/start", new { });
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        Assert.All(started.Headers.GetValues("Set-Cookie"), cookie => Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/equipment")).StatusCode);
    }

    [Fact]
    public async Task OtherHostsCannotSpoofHttpsWithAForwardingHeader()
    {
        await using var hosted = app.WithWebHostBuilder(builder => builder.UseEnvironment("Production").UseSetting("RENDER", "false"));
        using var client = hosted.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        Assert.Equal(HttpStatusCode.InternalServerError, (await client.GetAsync("/api/session")).StatusCode);
    }

    [Fact]
    public async Task MigrationsAndOpenApiAreReal()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeskDb>();
        Assert.Single(await db.Database.GetAppliedMigrationsAsync());
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/openapi/v1.json")).StatusCode);
    }

    [Theory]
    [InlineData("/api/equipment")]
    [InlineData("/api/loans")]
    public async Task AnonymousCannotReadDesk(string path)
    {
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task CsrfIsRequiredEvenForStartingDemo()
    {
        using var client = app.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/demo/start", new { })).StatusCode);
    }

    [Fact]
    public async Task ManagerCanAddEquipmentAndStaffCannot()
    {
        using var client = await app.Demo();
        Assert.Equal(HttpStatusCode.Created, (await DeskFactory.Post(client, "/api/equipment", new { assetTag = "new-1", name = "Recorder" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await DeskFactory.Post(client, "/api/equipment", new { assetTag = "NEW-1", name = "Another" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await DeskFactory.Post(client, "/api/demo/role", new { role = "Staff" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DeskFactory.Post(client, "/api/equipment", new { assetTag = "NEW-2", name = "Recorder" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await DeskFactory.Post(client, "/api/loans", DeskFactory.Issue(await DeskFactory.Available(client)))).StatusCode);
    }

    [Fact]
    public async Task CompetingCheckoutsCreateExactlyOneActiveLoan()
    {
        using var client = await app.Demo();
        var id = await DeskFactory.Available(client);
        var replies = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => DeskFactory.Post(client, "/api/loans", DeskFactory.Issue(id))));
        Assert.Single(replies, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Equal(7, replies.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeskDb>();
        Assert.Equal(1, await db.Loans.CountAsync(x => x.EquipmentId == id && x.ReturnedAt == null));
    }

    [Fact]
    public async Task RepeatedReturnKeepsOriginalTimestampAndAllowsNewLoan()
    {
        using var client = await app.Demo();
        var item = await DeskFactory.Available(client);
        var issued = await DeskFactory.Post(client, "/api/loans", DeskFactory.Issue(item));
        var id = (await issued.Content.ReadFromJsonAsync<JsonObject>())!["id"]!.GetValue<Guid>();
        var first = await DeskFactory.Post(client, $"/api/loans/{id}/return", new { });
        var second = await DeskFactory.Post(client, $"/api/loans/{id}/return", new { });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, (await DeskFactory.Post(client, "/api/loans", DeskFactory.Issue(item, "Next borrower"))).StatusCode);
        var records = await client.GetFromJsonAsync<JsonArray>("/api/loans");
        Assert.Equal(2, records!.Count(x => x!["equipmentId"]!.GetValue<Guid>() == item));
    }

    [Fact]
    public async Task WorkspacesCannotReadOrChangeEachOthersRecords()
    {
        using var first = await app.Demo();
        using var second = await app.Demo();
        var item = await DeskFactory.Available(first);
        Assert.Equal(HttpStatusCode.NotFound, (await DeskFactory.Post(second, "/api/loans", DeskFactory.Issue(item))).StatusCode);
        var records = await first.GetFromJsonAsync<JsonArray>("/api/loans");
        var loanId = records![0]!["id"]!.GetValue<Guid>();
        Assert.Equal(HttpStatusCode.NotFound, (await DeskFactory.Post(second, $"/api/loans/{loanId}/return", new { })).StatusCode);
        var items = await second.GetFromJsonAsync<JsonArray>("/api/equipment");
        Assert.DoesNotContain(items!, x => x!["id"]!.GetValue<Guid>() == item);
        Assert.Equal(HttpStatusCode.BadRequest, (await DeskFactory.Post(first, "/api/demo/role", new { role = "Administrator" })).StatusCode);
    }

    [Fact]
    public async Task ExpiredWorkspaceCannotAccessDataAndIsCleanedOnNextStart()
    {
        using var client = await app.Demo();
        var item = await DeskFactory.Available(client);
        Guid workspace;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeskDb>();
            workspace = (await db.Equipment.FindAsync(item))!.WorkspaceId;
            await db.Workspaces.Where(x => x.Id == workspace).ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, app.Clock.GetUtcNow().AddMinutes(-1)));
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/equipment")).StatusCode);
        using var next = await app.Demo();
        using var verification = app.Services.CreateScope();
        var context = verification.ServiceProvider.GetRequiredService<DeskDb>();
        Assert.False(await context.Workspaces.AnyAsync(x => x.Id == workspace));
        Assert.False(await context.Users.AnyAsync(x => x.WorkspaceId == workspace));
        Assert.False(await context.Equipment.AnyAsync(x => x.WorkspaceId == workspace));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyBorrowerRejected(string borrower)
    {
        using var client = await app.Demo();
        Assert.Equal(HttpStatusCode.BadRequest, (await DeskFactory.Post(client, "/api/loans", DeskFactory.Issue(await DeskFactory.Available(client), borrower))).StatusCode);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(91)]
    public async Task DueDateOutsideWindowRejected(int offset)
    {
        using var client = await app.Demo();
        var due = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(offset);
        Assert.Equal(HttpStatusCode.BadRequest, (await DeskFactory.Post(client, "/api/loans", DeskFactory.Issue(await DeskFactory.Available(client), due: due))).StatusCode);
    }

    [Fact]
    public async Task OverdueFlagUsesActiveLoansAndUtcDate()
    {
        using var client = await app.Demo();
        var loans = await client.GetFromJsonAsync<JsonArray>("/api/loans");
        Assert.Contains(loans!, x => x!["overdue"]!.GetValue<bool>() && x["returnedAt"] is null);
        Assert.DoesNotContain(loans!, x => x!["overdue"]!.GetValue<bool>() && x["returnedAt"] is not null);
    }

    [Fact]
    public async Task EquipmentLimitCannotBeBypassedByConcurrentRequests()
    {
        using var client = await app.Demo();
        var id = await DeskFactory.Available(client);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeskDb>();
            var workspace = (await db.Equipment.FindAsync(id))!.WorkspaceId;
            db.Equipment.AddRange(Enumerable.Range(9, 41).Select(i => new Equipment { WorkspaceId = workspace, AssetTag = $"FILL-{i}", Name = "Sample equipment" }));
            await db.SaveChangesAsync();
        }
        var results = await Task.WhenAll(new[] { "A", "B" }.Select(tag => DeskFactory.Post(client, "/api/equipment", new { assetTag = tag, name = "Final item" })));
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Single(results, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(50, (await client.GetFromJsonAsync<JsonArray>("/api/equipment"))!.Count);
    }

    [Fact]
    public async Task LoanLimitIncludesHistory()
    {
        using var client = await app.Demo();
        var item = await DeskFactory.Available(client);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeskDb>();
        var workspace = (await db.Equipment.FindAsync(item))!.WorkspaceId;
        db.Loans.AddRange(Enumerable.Range(0, 98).Select(_ => new Loan
        {
            WorkspaceId = workspace,
            EquipmentId = item,
            Borrower = "Historical sample",
            IssuedAt = app.Clock.GetUtcNow().AddDays(-1),
            DueDate = Session.Today(app.Clock),
            ReturnedAt = app.Clock.GetUtcNow(),
            IssuedBy = "Staff",
            ReturnedBy = "Staff"
        }));
        await db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await DeskFactory.Post(client, "/api/loans", DeskFactory.Issue(item))).StatusCode);
        Assert.Equal(100, await db.Loans.CountAsync(x => x.WorkspaceId == workspace));
    }

    [Fact]
    public async Task DatabaseRejectsCrossWorkspaceEquipmentReference()
    {
        using var first = await app.Demo();
        using var second = await app.Demo();
        var firstItem = await DeskFactory.Available(first);
        var secondItem = await DeskFactory.Available(second);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeskDb>();
        var otherWorkspace = (await db.Equipment.FindAsync(secondItem))!.WorkspaceId;
        db.Loans.Add(new Loan { WorkspaceId = otherWorkspace, EquipmentId = firstItem, Borrower = "Invalid link", IssuedAt = app.Clock.GetUtcNow(), DueDate = Session.Today(app.Clock), IssuedBy = "Staff" });
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ((PostgresException)exception.InnerException!).SqlState);
    }

    [Fact]
    public async Task CapacityStopsAdmissionAndExpiredEntriesMakeRoom()
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeskDb>();
        var fill = Enumerable.Range(0, 200 - await db.Workspaces.CountAsync()).Select(_ => new Workspace { ExpiresAt = app.Clock.GetUtcNow().AddHours(1) }).ToArray();
        db.Workspaces.AddRange(fill);
        await db.SaveChangesAsync();
        using var client = app.Client();
        try { Assert.Equal(HttpStatusCode.TooManyRequests, (await DeskFactory.Post(client, "/api/demo/start", new { })).StatusCode); }
        finally
        {
            var ids = fill.Select(x => x.Id).ToArray();
            await db.Workspaces.Where(x => ids.Contains(x.Id)).ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, app.Clock.GetUtcNow().AddMinutes(-1)));
        }
        using var next = await app.Demo();
        Assert.True(await db.Workspaces.CountAsync() < 200);
    }

    [Fact]
    public async Task AdmissionLimiterReturnsProblemDetailsAndRetryHeader()
    {
        await using var limited = app.WithWebHostBuilder(builder => builder.UseSetting("Limits:DemoStartsPerMinute", "1"));
        using var client = limited.CreateClient();
        Assert.Equal(HttpStatusCode.Created, (await DeskFactory.Post(client, "/api/demo/start", new { })).StatusCode);
        var reply = await DeskFactory.Post(client, "/api/demo/start", new { });
        Assert.Equal(HttpStatusCode.TooManyRequests, reply.StatusCode);
        Assert.Equal("60", reply.Headers.RetryAfter!.ToString());
        Assert.Equal("application/problem+json", reply.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task CsrfRejectsMissingTokenOnAuthenticatedWrites()
    {
        using var client = await app.Demo();
        var item = await DeskFactory.Available(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/loans", DeskFactory.Issue(item))).StatusCode);
    }

    [Fact]
    public async Task CreatedLocationResolvesAndAnotherVisitorCannotReadIt()
    {
        using var client = await app.Demo();
        using var other = await app.Demo();
        var created = await DeskFactory.Post(client, "/api/loans", DeskFactory.Issue(await DeskFactory.Available(client)));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(created.Headers.Location)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(created.Headers.Location)).StatusCode);
    }

    [Fact]
    public async Task IndependentHostReadsPersistedSessionKeys()
    {
        using var original = app.Client();
        var started = await DeskFactory.Post(original, "/api/demo/start", new { });
        var cookies = string.Join("; ", started.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]));
        using (var scope = app.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<DeskDb>().DataProtectionKeys.AnyAsync());
        await using var replacement = app.WithWebHostBuilder(_ => { });
        using var next = replacement.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/equipment");
        request.Headers.Add("Cookie", cookies);
        Assert.Equal(HttpStatusCode.OK, (await next.SendAsync(request)).StatusCode);
    }
}
