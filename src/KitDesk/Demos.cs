using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace KitDesk;

public sealed class DemoService(DeskDb db, UserManager<Operator> users, SignInManager<Operator> signIn, TimeProvider clock)
{
    public async Task<IResult> Start(CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // All app instances share this admission lock; a count followed by an insert alone races.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(712031)", ct);
        await db.Workspaces.Where(x => x.ExpiresAt <= clock.GetUtcNow()).ExecuteDeleteAsync(ct);
        if (await db.Workspaces.CountAsync(ct) >= 200)
            return Session.Problem(429, "The demo is busy. Please try again later.");
        var workspace = new Workspace { ExpiresAt = clock.GetUtcNow().AddHours(1) };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync(ct);
        Operator? manager = null;
        foreach (var role in new[] { "Manager", "Staff" })
        {
            var user = new Operator { WorkspaceId = workspace.Id, UserName = $"{workspace.Id:N}:{role}" };
            var created = await users.CreateAsync(user);
            if (!created.Succeeded) throw new InvalidOperationException("Could not create demo operator.");
            var assigned = await users.AddToRoleAsync(user, role);
            if (!assigned.Succeeded) throw new InvalidOperationException("Could not assign demo role.");
            manager ??= user;
        }
        var names = new[] { "Sony A6400 camera", "Manfrotto tripod", "Zoom H1n recorder", "LED panel light", "Rode VideoMic", "Epson projector", "USB-C dock", "Wireless presenter" };
        var equipment = names.Select((name, index) => new Equipment { WorkspaceId = workspace.Id, Name = name, AssetTag = $"KD-{index + 1:000}" }).ToArray();
        db.Equipment.AddRange(equipment);
        db.Loans.AddRange(
            new Loan { WorkspaceId = workspace.Id, EquipmentId = equipment[0].Id, Equipment = equipment[0], Borrower = "Sample borrower 01", IssuedAt = clock.GetUtcNow().AddDays(-3), DueDate = Session.Today(clock).AddDays(-1), IssuedBy = "Staff" },
            new Loan { WorkspaceId = workspace.Id, EquipmentId = equipment[2].Id, Equipment = equipment[2], Borrower = "Sample borrower 02", IssuedAt = clock.GetUtcNow().AddDays(-5), DueDate = Session.Today(clock).AddDays(-2), ReturnedAt = clock.GetUtcNow().AddDays(-3), IssuedBy = "Manager", ReturnedBy = "Staff" });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await Login(manager!, workspace.ExpiresAt);
        return Results.Created("/api/session", new { workspace.ExpiresAt });
    }

    public async Task<IResult> Switch(ClaimsPrincipal principal, SwitchRole input, CancellationToken ct)
    {
        if (input.Role is not ("Manager" or "Staff")) return Session.Problem(400, "Choose Staff or Manager.");
        var workspace = await db.Workspaces.SingleAsync(x => x.Id == Session.Workspace(principal), ct);
        var user = await users.FindByNameAsync($"{workspace.Id:N}:{input.Role}");
        await Login(user!, workspace.ExpiresAt);
        return Results.NoContent();
    }

    private Task Login(Operator user, DateTimeOffset expiry) => signIn.SignInWithClaimsAsync(user,
        new AuthenticationProperties { IsPersistent = true, ExpiresUtc = expiry, AllowRefresh = false },
        [new Claim("workspace", user.WorkspaceId.ToString())]);
}
