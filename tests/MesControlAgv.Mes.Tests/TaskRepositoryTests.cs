using Microsoft.EntityFrameworkCore;
using MesControlAgv.Mes.Data;
using MesControlAgv.Mes.Services;

namespace MesControlAgv.Mes.Tests;

public class TaskRepositoryTests
{
    [Fact]
    public async Task Creating_a_task_writes_a_created_audit_event()
    {
        var options = new DbContextOptionsBuilder<MesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var database = new MesDbContext(options);
        var repository = new TaskRepository(database);

        var task = await repository.CreateAsync(2, 4, CancellationToken.None);
        var events = await repository.GetEventsAsync(task.Id, CancellationToken.None);

        Assert.Equal(MesControlAgv.Domain.TaskStatus.Created, task.Status);
        Assert.Single(events);
        Assert.Equal("TaskCreated", events[0].EventType);
        Assert.True(task.CreatedAt <= DateTime.UtcNow);
        Assert.Null(task.EndedAt);
    }
}
