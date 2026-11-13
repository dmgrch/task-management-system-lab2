using ProjectTaskService.Clients.UserAccess;
using ProjectTaskService.Dtos;
using ProjectTaskService.Entities;
using ProjectTaskService.Enums;
using ProjectTaskService.Repositories;

namespace ProjectTaskService.Services;

public class TaskService : ITaskService
{
    private readonly ITaskRepository _tasks;
    private readonly IUserAccessClient _access;

    public TaskService(ITaskRepository tasks, IUserAccessClient access)
    {
        _tasks = tasks;
        _access = access;
    }

    public async Task<TaskResponse> CreateAsync(CreateTaskRequest request, CancellationToken ct)
    {
        RequireUser(request.CreatedByUserId, "CreatedByUserId is required");
        var title = NormalizeTitle(request.Title);
        var description = NormalizeDescription(request.Description);
        var project = await GetProjectAsync(request.ProjectId, ct);
        EnsureNotArchived(project, "Cannot create task in archived project");
        await EnsureAssigneeIsMemberAsync(request.AssigneeUserId, request.ProjectId, ct);
        await CheckAccessAsync(request.CreatedByUserId, request.ProjectId, ProjectAction.CreateTask, ct);

        var now = DateTime.UtcNow;
        var task = new TaskItem
        {
            Id = Guid.NewGuid(),
            ProjectId = request.ProjectId,
            Title = title,
            Description = description,
            Status = TaskItemStatus.TODO,
            AssigneeUserId = request.AssigneeUserId,
            CreatedByUserId = request.CreatedByUserId,
            CreatedAt = now,
            UpdatedAt = now,
            IsDeleted = false
        };
        _tasks.AddTask(task);
        AddHistory(task.Id, request.CreatedByUserId, TaskChangeType.Created, null, null);
        await _tasks.SaveAsync(ct);
        return ToResponse(task);
    }

    public async Task<TaskResponse> GetAsync(Guid id, Guid? actorUserId, CancellationToken ct)
    {
        var task = await GetTaskAsync(id, ct);
        await CheckAccessAsync(actorUserId, task.ProjectId, ProjectAction.ViewTask, ct);
        var labels = await _tasks.GetActiveLabelsAsync(task.Id, ct);
        return ToResponse(task, labels);
    }

    public async Task<IReadOnlyList<TaskResponse>> GetUnassignedAsync(
        Guid projectId, Guid? actorUserId, CancellationToken ct)
    {
        await GetProjectAsync(projectId, ct);
        await CheckAccessAsync(actorUserId, projectId, ProjectAction.ViewTask, ct);
        var tasks = await _tasks.GetUnassignedTasksAsync(projectId, ct);
        return tasks.Select(task => ToResponse(task)).ToList();
    }

    public async Task UpdateAsync(Guid id, UpdateTaskRequest request, Guid actorUserId, CancellationToken ct)
    {
        RequireUser(actorUserId, "X-User-Id is required");
        var task = await GetTaskAsync(id, ct);
        await CheckAccessAsync(actorUserId, task.ProjectId, ProjectAction.EditTask, ct,
            allowCreatorOverride: task.CreatedByUserId == actorUserId);
        await EnsureProjectNotArchivedAsync(task.ProjectId, "Cannot edit task in archived project", ct);

        var title = NormalizeTitle(request.Title);
        var description = NormalizeDescription(request.Description);
        var oldTitle = task.Title;
        var oldDescription = task.Description;
        task.Title = title;
        task.Description = description;
        task.UpdatedAt = DateTime.UtcNow;

        if (oldTitle != title)
            AddHistory(task.Id, actorUserId, TaskChangeType.TitleChanged, oldTitle, title);
        if (oldDescription != description)
            AddHistory(task.Id, actorUserId, TaskChangeType.DescriptionChanged, oldDescription, description);
        await _tasks.SaveAsync(ct);
    }

    public async Task ChangeStatusAsync(Guid id, ChangeStatusRequest request, CancellationToken ct)
    {
        RequireUser(request.ActorUserId, "ActorUserId is required");
        var task = await GetTaskAsync(id, ct);
        await EnsureProjectNotArchivedAsync(task.ProjectId, "Cannot change status in archived project", ct);
        if (!await _access.IsMemberAsync(request.ActorUserId, task.ProjectId, ct))
            Fail(TaskErrorKind.Forbidden, "USER_NOT_MEMBER", "User is not a member of this project");
        await CheckAccessAsync(request.ActorUserId, task.ProjectId, ProjectAction.ChangeTaskStatus, ct);

        if (!IsValidTransition(task.Status, request.NewStatus))
            Fail(TaskErrorKind.Validation, "INVALID_STATUS_TRANSITION",
                $"Cannot change from {task.Status} to {request.NewStatus}");
        if (request.NewStatus == TaskItemStatus.DONE && task.AssigneeUserId is null)
            Fail(TaskErrorKind.Validation, "NO_ASSIGNEE", "Cannot set DONE without assignee");

        var oldStatus = task.Status;
        task.Status = request.NewStatus;
        task.UpdatedAt = DateTime.UtcNow;
        AddHistory(task.Id, request.ActorUserId, TaskChangeType.StatusChanged,
            oldStatus.ToString(), request.NewStatus.ToString());
        await _tasks.SaveAsync(ct);
    }

    public async Task AssignAsync(Guid id, AssignAssigneeRequest request, CancellationToken ct)
    {
        RequireUser(request.ActorUserId, "ActorUserId is required");
        var task = await GetTaskAsync(id, ct);
        await EnsureProjectNotArchivedAsync(task.ProjectId, "Cannot assign assignee in archived project", ct);
        await EnsureAssigneeIsMemberAsync(request.AssigneeUserId, task.ProjectId, ct);
        await CheckAccessAsync(request.ActorUserId, task.ProjectId, ProjectAction.AssignTask, ct);

        var oldAssignee = task.AssigneeUserId;
        task.AssigneeUserId = request.AssigneeUserId;
        task.UpdatedAt = DateTime.UtcNow;
        AddHistory(task.Id, request.ActorUserId, TaskChangeType.AssigneeChanged,
            oldAssignee?.ToString(), request.AssigneeUserId?.ToString());
        await _tasks.SaveAsync(ct);
    }

    public async Task DeleteAsync(Guid id, Guid actorUserId, CancellationToken ct)
    {
        RequireUser(actorUserId, "X-User-Id is required");
        var task = await GetTaskAsync(id, ct);
        await EnsureProjectNotArchivedAsync(task.ProjectId, "Cannot delete task in archived project", ct);
        await CheckAccessAsync(actorUserId, task.ProjectId, ProjectAction.DeleteTask, ct,
            allowCreatorOverride: task.CreatedByUserId == actorUserId);
        task.IsDeleted = true;
        task.UpdatedAt = DateTime.UtcNow;
        await _tasks.SaveAsync(ct);
    }

    private async Task<Project> GetProjectAsync(Guid projectId, CancellationToken ct) =>
        await _tasks.GetProjectAsync(projectId, ct)
        ?? throw new TaskOperationException(TaskErrorKind.NotFound, "PROJECT_NOT_FOUND", "Project not found");

    private async Task<TaskItem> GetTaskAsync(Guid id, CancellationToken ct) =>
        await _tasks.GetTaskAsync(id, ct)
        ?? throw new TaskOperationException(TaskErrorKind.NotFound, "TASK_NOT_FOUND", "Task not found");

    private async Task EnsureProjectNotArchivedAsync(Guid projectId, string message, CancellationToken ct)
    {
        var project = await _tasks.GetProjectAsync(projectId, ct);
        // In the original controller, a missing project did not block mutations of an existing task.
        if (project?.IsArchived == true)
            Fail(TaskErrorKind.Conflict, "PROJECT_ARCHIVED", message);
    }

    private static void EnsureNotArchived(Project project, string message)
    {
        if (project.IsArchived)
            Fail(TaskErrorKind.Conflict, "PROJECT_ARCHIVED", message);
    }

    private async Task EnsureAssigneeIsMemberAsync(Guid? assignee, Guid projectId, CancellationToken ct)
    {
        if (assignee.HasValue && assignee != Guid.Empty &&
            !await _access.IsMemberAsync(assignee.Value, projectId, ct))
            Fail(TaskErrorKind.Validation, "ASSIGNEE_NOT_MEMBER", "Assignee is not a member of this project");
    }

    private async Task CheckAccessAsync(
        Guid? actor,
        Guid projectId,
        ProjectAction action,
        CancellationToken ct,
        bool allowCreatorOverride = false)
    {
        var userId = actor.GetValueOrDefault();

        if (userId == Guid.Empty)
        {
            Fail(
                TaskErrorKind.Unauthorized,
                "USER_ID_REQUIRED",
                "X-User-Id header is required");
        }

        var access = await _access.CheckAccessAsync(
            userId, projectId, action, ct);

        if (allowCreatorOverride)
            return;

        if (access is null)
        {
            Fail(
                TaskErrorKind.DependencyUnavailable,
                "USER_ACCESS_SERVICE_ERROR",
                "Could not check access in UserAccessService");
        }

        if (!access!.Allowed)
        {
            Fail(
                TaskErrorKind.Forbidden,
                "ACCESS_DENIED",
                "User does not have permission to perform this action");
        }
    }

    private static string NormalizeTitle(string? title)
    {
        var result = title?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(result))
            Fail(TaskErrorKind.Validation, "VALIDATION_ERROR", "Title is required");
        if (result.Length > 250)
            Fail(TaskErrorKind.Validation, "VALIDATION_ERROR", "Title must be <= 250 characters");
        return result;
    }

    private static string NormalizeDescription(string? description)
    {
        var result = description?.Trim() ?? string.Empty;
        if (result.Length > 10000)
            Fail(TaskErrorKind.Validation, "VALIDATION_ERROR", "Description must be <= 10000 characters");
        return result;
    }

    private static void RequireUser(Guid userId, string message)
    {
        if (userId == Guid.Empty)
            Fail(TaskErrorKind.Validation, "VALIDATION_ERROR", message);
    }

    private static bool IsValidTransition(TaskItemStatus from, TaskItemStatus to) =>
        (from, to) is
            (TaskItemStatus.TODO, TaskItemStatus.IN_PROGRESS) or
            (TaskItemStatus.IN_PROGRESS, TaskItemStatus.REVIEW) or
            (TaskItemStatus.REVIEW, TaskItemStatus.DONE);

    private void AddHistory(Guid taskId, Guid userId, TaskChangeType kind, string? oldValue, string? newValue) =>
        _tasks.AddHistory(new TaskHistory
        {
            Id = Guid.NewGuid(), TaskId = taskId, ChangedByUserId = userId,
            ChangeType = kind, OldValue = oldValue, NewValue = newValue,
            CreatedAt = DateTime.UtcNow
        });

    private static TaskResponse ToResponse(TaskItem task, List<LabelResponse>? labels = null) => new()
    {
        Id = task.Id,
        ProjectId = task.ProjectId,
        Title = task.Title,
        Description = task.Description,
        Status = task.Status,
        AssigneeUserId = task.AssigneeUserId,
        CreatedByUserId = task.CreatedByUserId,
        CreatedAt = task.CreatedAt,
        UpdatedAt = task.UpdatedAt,
        Labels = labels ?? new List<LabelResponse>()
    };

    private static void Fail(TaskErrorKind kind, string code, string message) =>
        throw new TaskOperationException(kind, code, message);
}
