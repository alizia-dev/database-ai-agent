using DatabaseAIAgent.Application.Abstractions;
using DatabaseAIAgent.Application.Abstractions.Authorization;
using DatabaseAIAgent.Application.Abstractions.Email;
using DatabaseAIAgent.Application.Abstractions.Persistence;
using DatabaseAIAgent.Application.Common;
using DatabaseAIAgent.Domain.Authorization;
using DatabaseAIAgent.Domain.Users;
using DatabaseAIAgent.Security.Auth;
using DatabaseAIAgent.Security.Email;
using DatabaseAIAgent.Security.Sessions;
using DatabaseAIAgent.Security.Tokens;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DatabaseAIAgent.Security.Users;

public sealed record TeamMemberDto(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    bool IsActive,
    bool EmailConfirmed,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastLoginAt);

public sealed record InvitationDto(Guid Id, string Email, string Role, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

public sealed record InvitationPreviewDto(string Email, string Role, string WorkspaceName);

public sealed record InviteUserCommand(string Email, string Role);

public sealed record AcceptInvitationCommand(string Token, string DisplayName, string Password);

/// <summary>
/// Team management for the single workspace. Rules: an actor can only manage roles at or below their own level;
/// nobody can change or deactivate themselves; Owners can only be managed by Owners; the last active Owner cannot be
/// demoted or deactivated.
/// </summary>
public sealed class UserManagementService(
    UserManager<User> users,
    UserDirectory directory,
    IUserInvitationRepository invitations,
    IOrganizationRepository organizations,
    RefreshTokenService refreshTokens,
    AuthService auth,
    IPermissionService permissions,
    SessionValidator sessions,
    IEmailSender emailSender,
    AccountEmails emails,
    IUnitOfWork unitOfWork,
    IClock clock,
    IOptions<AuthOptions> options,
    ILogger<UserManagementService> logger)
{
    private static readonly Error UserNotFound = Error.NotFound("users.not_found", "That user was not found.");
    private static readonly Error InvitationInvalid = Error.NotFound("invitations.invalid", "This invitation is invalid, expired or has already been used.");
    private static readonly Error CannotManageSelf = Error.Forbidden("users.cannot_manage_self", "You cannot change your own role or deactivate your own account.");
    private static readonly Error RoleNotAllowed = Error.Forbidden("users.role_not_allowed", "You can only assign roles at or below your own.");
    private static readonly Error OwnerProtected = Error.Forbidden("users.owner_protected", "Only an Owner can manage another Owner.");
    private static readonly Error LastOwner = Error.Conflict("users.last_owner", "The workspace must keep at least one active Owner.");

    public async Task<IReadOnlyList<TeamMemberDto>> ListMembersAsync(CancellationToken cancellationToken)
    {
        var members = new List<TeamMemberDto>();
        foreach (var role in SystemRoles.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            members.AddRange((await users.GetUsersInRoleAsync(role)).Select(u => ToDto(u, role)));
        }

        return members.OrderBy(m => UserDirectory.Rank(m.Role)).ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<InvitationDto>> ListInvitationsAsync(CancellationToken cancellationToken) =>
        (await invitations.ListPendingAsync(clock.UtcNow, cancellationToken))
            .Select(i => new InvitationDto(i.Id, i.Email, i.Role, i.CreatedAt, i.ExpiresAt))
            .ToList();

    public async Task<Result<InvitationDto>> InviteAsync(Guid actorId, InviteUserCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var actor = await users.FindByIdAsync(actorId.ToString());
        if (actor is not { IsActive: true })
        {
            return UserNotFound;
        }

        var actorRole = await directory.GetRoleAsync(actor);
        if (!SystemRoles.IsKnown(command.Role) || command.Role == SystemRoles.Owner)
        {
            return Error.Validation("invitations.invalid_role", "Choose Admin, Manager, Analyst or Viewer.");
        }

        if (!UserDirectory.CanAssign(actorRole, command.Role))
        {
            return RoleNotAllowed;
        }

        var email = command.Email.Trim();
        if (await users.FindByEmailAsync(email) is not null)
        {
            return Error.Conflict("invitations.already_member", "This person already has an account in this workspace.");
        }

        var now = clock.UtcNow;
        foreach (var previous in await invitations.ListPendingByEmailAsync(email.ToUpperInvariant(), now, cancellationToken))
        {
            previous.Revoke(now);
        }

        var (token, hash) = OpaqueToken.Create();
        var invitation = new UserInvitation(email, command.Role, hash, actor.Id, now, now.AddDays(options.Value.InvitationDays));
        invitations.Add(invitation);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        var workspace = await organizations.GetAsync(cancellationToken);
        await emailSender.SendAsync(emails.Invitation(invitation.Email, workspace.Name, actor.DisplayName, invitation.Role, token), cancellationToken);
        logger.LogInformation("User {ActorId} invited a new member with role {Role} (invitation {InvitationId})", actor.Id, invitation.Role, invitation.Id);

        return new InvitationDto(invitation.Id, invitation.Email, invitation.Role, invitation.CreatedAt, invitation.ExpiresAt);
    }

    public async Task<Result> RevokeInvitationAsync(Guid invitationId, CancellationToken cancellationToken)
    {
        var invitation = await invitations.FindAsync(invitationId, cancellationToken);
        if (invitation is null || !invitation.IsPending(clock.UtcNow))
        {
            return InvitationInvalid;
        }

        invitation.Revoke(clock.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>Public: shows who the invitation is for, so the accept page can confirm before creating the account.</summary>
    public async Task<Result<InvitationPreviewDto>> PreviewInvitationAsync(string? token, CancellationToken cancellationToken)
    {
        var invitation = await FindPendingAsync(token, cancellationToken);
        if (invitation is null)
        {
            return InvitationInvalid;
        }

        var workspace = await organizations.GetAsync(cancellationToken);
        return new InvitationPreviewDto(invitation.Email, invitation.Role, workspace.Name);
    }

    /// <summary>Public: creates the invited account (email is verified by the invitation link) and signs it in.</summary>
    public async Task<Result<AuthSession>> AcceptInvitationAsync(AcceptInvitationCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var created = await unitOfWork.ExecuteInTransactionAsync<Result<User>>(
            async ct =>
            {
                var invitation = await FindPendingAsync(command.Token, ct);
                if (invitation is null)
                {
                    return InvitationInvalid;
                }

                if (await users.FindByEmailAsync(invitation.Email) is not null)
                {
                    return Error.Conflict("invitations.already_member", "An account with this email already exists. Please sign in.");
                }

                var user = new User
                {
                    Email = invitation.Email,
                    UserName = invitation.Email,
                    DisplayName = command.DisplayName.Trim(),
                    EmailConfirmed = true,
                    CreatedAt = clock.UtcNow,
                };

                var result = await users.CreateAsync(user, command.Password);
                if (result.Succeeded)
                {
                    result = await users.AddToRoleAsync(user, invitation.Role);
                }

                if (!result.Succeeded)
                {
                    return Error.Validation("auth.password_invalid", "Choose a stronger password.", UserDirectory.ToFieldErrors(result.Errors, "password"));
                }

                invitation.Accept(clock.UtcNow);
                return user;
            },
            result => result.IsSuccess,
            cancellationToken);

        if (!created.IsSuccess)
        {
            return created.Error;
        }

        logger.LogInformation("Invitation accepted; created user {UserId}", created.Value.Id);
        return await auth.LoginAsync(new LoginCommand(created.Value.Email!, command.Password), cancellationToken);
    }

    public async Task<Result<TeamMemberDto>> ChangeRoleAsync(Guid actorId, Guid userId, string newRole, CancellationToken cancellationToken)
    {
        if (!SystemRoles.IsKnown(newRole))
        {
            return Error.Validation("users.invalid_role", "Unknown role.");
        }

        var check = await AuthorizeManagementAsync(actorId, userId);
        if (!check.IsSuccess)
        {
            return check.Error;
        }

        var (actorRole, target, targetRole) = check.Value;
        if (!UserDirectory.CanAssign(actorRole, newRole))
        {
            return RoleNotAllowed;
        }

        if (targetRole == newRole)
        {
            return ToDto(target, targetRole);
        }

        if (targetRole == SystemRoles.Owner && target.IsActive && await directory.CountActiveOwnersAsync() <= 1)
        {
            return LastOwner;
        }

        var updated = await unitOfWork.ExecuteInTransactionAsync(
            async _ =>
            {
                var removed = await users.RemoveFromRoleAsync(target, targetRole);
                return removed.Succeeded ? await users.AddToRoleAsync(target, newRole) : removed;
            },
            result => result.Succeeded,
            cancellationToken);

        if (!updated.Succeeded)
        {
            throw new InvalidOperationException("Role change failed: " + string.Join("; ", updated.Errors.Select(e => e.Code)));
        }

        permissions.Invalidate(target.Id);
        logger.LogInformation("User {ActorId} changed role of {UserId} from {OldRole} to {NewRole}", actorId, target.Id, targetRole, newRole);
        return ToDto(target, newRole);
    }

    public async Task<Result<TeamMemberDto>> SetActiveAsync(Guid actorId, Guid userId, bool active, CancellationToken cancellationToken)
    {
        var check = await AuthorizeManagementAsync(actorId, userId);
        if (!check.IsSuccess)
        {
            return check.Error;
        }

        var (_, target, targetRole) = check.Value;
        if (target.IsActive == active)
        {
            return ToDto(target, targetRole);
        }

        if (!active && targetRole == SystemRoles.Owner && await directory.CountActiveOwnersAsync() <= 1)
        {
            return LastOwner;
        }

        target.IsActive = active;
        if (!active)
        {
            await refreshTokens.RevokeAllForUserAsync(target.Id, RefreshTokenRevocationReason.UserDeactivated, cancellationToken);
            UserDirectory.EnsureSucceeded(await users.UpdateSecurityStampAsync(target), "deactivate"); // saves the user and the revocations
        }
        else
        {
            UserDirectory.EnsureSucceeded(await users.UpdateAsync(target), "reactivate");
        }

        sessions.Invalidate(target.Id);
        permissions.Invalidate(target.Id);
        logger.LogInformation("User {ActorId} {Action} user {UserId}", actorId, active ? "reactivated" : "deactivated", target.Id);
        return ToDto(target, targetRole);
    }

    private async Task<Result<(string ActorRole, User Target, string TargetRole)>> AuthorizeManagementAsync(Guid actorId, Guid userId)
    {
        if (actorId == userId)
        {
            return CannotManageSelf;
        }

        var actor = await users.FindByIdAsync(actorId.ToString());
        var target = await users.FindByIdAsync(userId.ToString());
        if (actor is not { IsActive: true } || target is null)
        {
            return UserNotFound;
        }

        var actorRole = await directory.GetRoleAsync(actor);
        var targetRole = await directory.GetRoleAsync(target);
        if (targetRole == SystemRoles.Owner && actorRole != SystemRoles.Owner)
        {
            return OwnerProtected;
        }

        if (!UserDirectory.CanAssign(actorRole, targetRole))
        {
            return RoleNotAllowed;
        }

        return (actorRole, target, targetRole);
    }

    private async Task<UserInvitation?> FindPendingAsync(string? token, CancellationToken cancellationToken)
    {
        if (!OpaqueToken.LooksValid(token))
        {
            return null;
        }

        var invitation = await invitations.FindByTokenHashAsync(OpaqueToken.Hash(token!), cancellationToken);
        return invitation is not null && invitation.IsPending(clock.UtcNow) ? invitation : null;
    }

    private static TeamMemberDto ToDto(User user, string role) =>
        new(user.Id, user.Email!, user.DisplayName, role, user.IsActive, user.EmailConfirmed, user.CreatedAt, user.LastLoginAt);
}
