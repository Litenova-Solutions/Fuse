using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;

namespace OrderingApp.Ordering;

// A MediatR pipeline behavior (R5: pipeline-behavior wiring). Wraps handling of CreateOrderCommand.
public sealed class LoggingBehavior : IPipelineBehavior<CreateOrderCommand, int>
{
    public Task<int> Handle(CreateOrderCommand request, Func<Task<int>> next, CancellationToken cancellationToken) =>
        next();
}
