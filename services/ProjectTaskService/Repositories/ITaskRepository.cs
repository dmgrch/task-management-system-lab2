using ProjectTaskService.Dtos;
using ProjectTaskService.Entities;

namespace ProjectTaskService.Repositories;

public interface ITaskRepository
{
    Task<Project?> GetProjectAsync(Guid projectId, CancellationToken ct);
    Task<TaskItem?> GetTaskAsync(Guid taskId, CancellationToken ct);
    Task<List<TaskItem>> GetUnassignedTasksAsync(Guid projectId, CancellationToken ct);
    Task<List<LabelResponse>> GetActiveLabelsAsync(Guid taskId, CancellationToken ct);
    void AddTask(TaskItem task);
    void AddHistory(TaskHistory history);
    Task SaveAsync(CancellationToken ct);
}
