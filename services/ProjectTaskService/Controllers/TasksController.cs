using Microsoft.AspNetCore.Mvc;
using ProjectTaskService.Dtos;
using ProjectTaskService.Services;

namespace ProjectTaskService.Controllers;

[ApiController]
[Route("tasks")]
public class TasksController : ControllerBase
{
    private readonly ITaskService _tasks;

    public TasksController(ITaskService tasks) => _tasks = tasks;

    [HttpPost]
    public Task<IActionResult> CreateTask(CreateTaskRequest request, CancellationToken ct) =>
        ExecuteAsync(async () =>
        {
            var task = await _tasks.CreateAsync(request, ct);
            return CreatedAtAction(nameof(GetTaskById), new { id = task.Id }, task);
        });

    [HttpGet("{id:guid}")]
    public Task<IActionResult> GetTaskById(Guid id,
        [FromHeader(Name = "X-User-Id")] Guid? actorUserId, CancellationToken ct) =>
        ExecuteAsync(async () => Ok(await _tasks.GetAsync(id, actorUserId, ct)));

    [HttpGet("/projects/{projectId:guid}/tasks/unassigned")]
    public Task<IActionResult> GetUnassignedTasks(Guid projectId,
        [FromHeader(Name = "X-User-Id")] Guid? actorUserId, CancellationToken ct) =>
        ExecuteAsync(async () => Ok(await _tasks.GetUnassignedAsync(projectId, actorUserId, ct)));

    [HttpPatch("{id:guid}")]
    public Task<IActionResult> UpdateTask(Guid id, UpdateTaskRequest request,
        [FromHeader(Name = "X-User-Id")] Guid actorUserId, CancellationToken ct) =>
        ExecuteAsync(async () =>
        {
            await _tasks.UpdateAsync(id, request, actorUserId, ct);
            return NoContent();
        });

    [HttpPatch("{id:guid}/status")]
    public Task<IActionResult> ChangeStatus(Guid id, ChangeStatusRequest request, CancellationToken ct) =>
        ExecuteAsync(async () =>
        {
            await _tasks.ChangeStatusAsync(id, request, ct);
            return NoContent();
        });

    [HttpPatch("{id:guid}/assignee")]
    public Task<IActionResult> AssignAssignee(Guid id, AssignAssigneeRequest request, CancellationToken ct) =>
        ExecuteAsync(async () =>
        {
            await _tasks.AssignAsync(id, request, ct);
            return NoContent();
        });

    [HttpDelete("{id:guid}")]
    public Task<IActionResult> DeleteTask(Guid id,
        [FromHeader(Name = "X-User-Id")] Guid actorUserId, CancellationToken ct) =>
        ExecuteAsync(async () =>
        {
            await _tasks.DeleteAsync(id, actorUserId, ct);
            return NoContent();
        });

    private async Task<IActionResult> ExecuteAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (TaskOperationException ex)
        {
            var statusCode = ex.Kind switch
            {
                TaskErrorKind.Validation => 400,
                TaskErrorKind.Unauthorized => 401,
                TaskErrorKind.Forbidden => 403,
                TaskErrorKind.NotFound => 404,
                TaskErrorKind.Conflict => 409,
                TaskErrorKind.DependencyUnavailable => 502,
                _ => 500
            };
            return StatusCode(statusCode, new ErrorResponse { Code = ex.Code, Message = ex.Message });
        }
    }
}
