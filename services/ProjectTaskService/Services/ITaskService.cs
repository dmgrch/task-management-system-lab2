using ProjectTaskService.Dtos;

namespace ProjectTaskService.Services;

public interface ITaskService
{
    Task<TaskResponse> CreateAsync(CreateTaskRequest request, CancellationToken ct);
    Task<TaskResponse> GetAsync(Guid id, Guid? actorUserId, CancellationToken ct);
    Task<IReadOnlyList<TaskResponse>> GetUnassignedAsync(
        Guid projectId, Guid? actorUserId, CancellationToken ct);
    Task UpdateAsync(Guid id, UpdateTaskRequest request, Guid actorUserId, CancellationToken ct);
    Task ChangeStatusAsync(Guid id, ChangeStatusRequest request, CancellationToken ct);
    Task AssignAsync(Guid id, AssignAssigneeRequest request, CancellationToken ct);
    Task DeleteAsync(Guid id, Guid actorUserId, CancellationToken ct);
}
