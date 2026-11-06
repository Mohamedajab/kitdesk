using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace KitDesk;

public sealed record AddEquipment(string AssetTag, string Name);
public sealed record IssueLoan(Guid EquipmentId, string Borrower, DateOnly DueDate);
public sealed record SwitchRole(string Role);

public static class Session
{
    public static Guid Workspace(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue("workspace")!);
    public static string Actor(ClaimsPrincipal user) => user.IsInRole("Manager") ? "Manager" : "Staff";
    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
    public static IResult Problem(int status, string message) => Results.Problem(statusCode: status, title: message);
    public static bool Unique(DbUpdateException exception, string constraint) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg && pg.ConstraintName == constraint;
}

public sealed class LoanService(DeskDb db, TimeProvider clock)
{
    public async Task<IResult> Issue(ClaimsPrincipal user, IssueLoan input, CancellationToken ct)
    {
        var workspace = Session.Workspace(user);
        var borrower = input.Borrower?.Trim() ?? "";
        var today = Session.Today(clock);
        if (borrower.Length is < 1 or > 80 || input.DueDate < today || input.DueDate > today.AddDays(90))
            return Session.Problem(400, "Enter a borrower label (1–80 characters) and a due date from today to 90 days ahead.");

        // Lock the workspace, not the whole table: limits and writes must commit together.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Workspaces.FromSqlInterpolated($"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {workspace} FOR UPDATE").SingleAsync(ct);
        if (!await db.Equipment.AnyAsync(x => x.WorkspaceId == workspace && x.Id == input.EquipmentId, ct))
            return Session.Problem(404, "Equipment not found in this workspace.");
        if (await db.Loans.CountAsync(x => x.WorkspaceId == workspace, ct) >= 100)
            return Session.Problem(409, "This demo has reached its 100-loan limit. Start a new workspace.");
        var loan = new Loan
        {
            WorkspaceId = workspace,
            EquipmentId = input.EquipmentId,
            Borrower = borrower,
            DueDate = input.DueDate,
            IssuedAt = clock.GetUtcNow(),
            IssuedBy = Session.Actor(user)
        };
        db.Loans.Add(loan);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Created($"/api/loans/{loan.Id}", new { loan.Id });
        }
        catch (DbUpdateException exception) when (Session.Unique(exception, "one_active_loan"))
        {
            return Session.Problem(409, "Someone has already checked out this item. Your form has been kept; choose another item or wait for its return.");
        }
    }

    public async Task<IResult> Return(ClaimsPrincipal user, Guid id, CancellationToken ct)
    {
        var workspace = Session.Workspace(user);
        var now = clock.GetUtcNow();
        await db.Loans.Where(x => x.WorkspaceId == workspace && x.Id == id && x.ReturnedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.ReturnedAt, now).SetProperty(x => x.ReturnedBy, Session.Actor(user)), ct);
        var loan = await db.Loans.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspaceId == workspace && x.Id == id, ct);
        return loan is null ? Session.Problem(404, "Loan not found in this workspace.") : Results.Ok(new { loan.Id, loan.ReturnedAt });
    }
}
