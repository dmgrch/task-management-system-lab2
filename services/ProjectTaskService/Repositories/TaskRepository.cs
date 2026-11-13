using Microsoft.EntityFrameworkCore;
using ProjectTaskService.Data;
using ProjectTaskService.Dtos;
using ProjectTaskService.Entities;

namespace ProjectTaskService.Repositories;

public class TaskRepository : ITaskRepository
{
    private readonly ProjectTaskDbContext _db;

    public TaskRepository(ProjectTaskDbContext db) => _db = db;

    public Task<Project?> GetProjectAsync(Guid projectId, CancellationToken ct) =>
        _db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct);

    public Task<TaskItem?> GetTaskAsync(Guid taskId, CancellationToken ct) =>
        _db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId && !t.IsDeleted, ct);

    public Task<List<TaskItem>> GetUnassignedTasksAsync(Guid projectId, CancellationToken ct) =>
        _db.Tasks
            .Where(task => task.ProjectId == projectId &&
                task.AssigneeUserId == null &&
                !task.IsDeleted)
            .OrderBy(task => task.CreatedAt)
            .ThenBy(task => task.Id)
            .ToListAsync(ct);

    public Task<List<LabelResponse>> GetActiveLabelsAsync(Guid taskId, CancellationToken ct) =>
        _db.TaskLabels
            .Where(link => link.TaskId == taskId)
            .Join(_db.Labels, link => link.LabelId, label => label.Id,
                (_, label) => label)
            .Where(label => label.IsActive)
            .OrderBy(label => label.Name)
            .Select(label => new LabelResponse
            {
                Id = label.Id,
                ProjectId = label.ProjectId,
                Name = label.Name,
                IsActive = label.IsActive
            })
            .ToListAsync(ct);

    public void AddTask(TaskItem task) => _db.Tasks.Add(task);
    public void AddHistory(TaskHistory history) => _db.TaskHistories.Add(history);
    public async Task SaveAsync(CancellationToken ct) => await _db.SaveChangesAsync(ct);
}
